using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using AngleSharp.Html.Parser;
using DecoSOP.Data;
using DecoSOP.Models;
using Microsoft.EntityFrameworkCore;

namespace DecoSOP.Services.Online;

/// <summary>
/// Which parts of the protocols library are brought into the ONLINE binder. These are the
/// office's own rules for what counts as a live protocol: work-in-progress and filing-queue
/// folders, and the archive/reference folders that start with "z", stay behind — apart from the
/// two "z" folders that are in active use.
/// </summary>
public static class WordImportRules
{
    private static readonly string[] ExcludedPhrases = ["TO FILE", "TO GO THRU"];
    private static readonly string[] KeptZFolders = ["z CATALOGS", "z QUICK REFS"];

    public static readonly IReadOnlySet<string> WordExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".docx", ".docm", ".doc" };

    /// <summary>Applied to every folder at every depth, so an archive inside a department is skipped too.</summary>
    public static bool SkipFolder(string name)
        => IsWorkInProgress(name)
           || name.Trim().Equals("tmp", StringComparison.OrdinalIgnoreCase)   // scripting scratch space, not protocols
           || (name.StartsWith('z') || name.StartsWith('Z'))
              && !KeptZFolders.Contains(name.Trim(), StringComparer.OrdinalIgnoreCase);

    public static bool SkipFile(string fileName)
        => fileName.StartsWith("~$")   // Word's lock file for a document someone has open
           || IsWorkInProgress(fileName)
           || !WordExtensions.Contains(Path.GetExtension(fileName));

    private static bool IsWorkInProgress(string name)
        => name.StartsWith('!')
           || ExcludedPhrases.Any(p => name.Contains(p, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// One-off migration of Word protocols into the ONLINE binder, run from the command line:
///
///   DecoSOP.exe import-online "&lt;folder&gt;" [--module sop|doc] [--db &lt;path&gt;] [--dry-run]
///
/// The folder structure is recreated as binder folders, each Word file becomes a page titled with
/// its file name, and the content is converted to the binder's house style. Re-running is safe:
/// a page whose title already exists in its folder is left alone, so an interrupted import can
/// simply be started again.
/// </summary>
public static class WordImporter
{
    private sealed record Candidate(string FullPath, IReadOnlyList<string> Folders, string Title);

    public static async Task<int> RunAsync(string[] args)
    {
        var folder = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--"));
        if (folder is null || !Directory.Exists(folder))
        {
            Console.Error.WriteLine("Usage: DecoSOP.exe import-online \"<folder>\" [--module sop|doc] [--db <path>] [--dry-run]");
            return 1;
        }

        var module = Option(args, "--module") ?? OnlineModule.Sop;
        if (!OnlineModule.IsValid(module))
        {
            Console.Error.WriteLine("--module must be 'sop' or 'doc'.");
            return 1;
        }
        var dbPath = Option(args, "--db") ?? Path.Combine(AppContext.BaseDirectory, "decosop-online.db");
        var dryRun = args.Contains("--dry-run", StringComparer.OrdinalIgnoreCase);

        var candidates = Collect(folder);
        Console.WriteLine($"Found {candidates.Count} Word file(s) to import from {folder}");
        if (dryRun)
        {
            foreach (var group in candidates.GroupBy(c => c.Folders.FirstOrDefault() ?? "(top level)").OrderBy(g => g.Key))
                Console.WriteLine($"  {group.Count(),5}  {group.Key}");
            return 0;
        }

        var options = new DbContextOptionsBuilder<OnlineDbContext>().UseSqlite($"Data Source={dbPath}").Options;
        await using var db = new OnlineDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
        Console.WriteLine($"Importing into {dbPath} (module: {module})");

        var sections = new SectionResolver(db, module);
        await sections.LoadAsync();

        var reportPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dbPath))!,
            $"online-import-report-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        var report = new StringBuilder("Status,File,Detail\r\n");
        int imported = 0, skipped = 0, failed = 0;
        var clock = Stopwatch.StartNew();

        using var docConverter = new LegacyDocConverter();

        for (var i = 0; i < candidates.Count; i++)
        {
            var c = candidates[i];
            try
            {
                var sectionId = await sections.EnsureAsync(c.Folders);
                if (await db.Pages.AnyAsync(p => p.Module == module && p.SectionId == sectionId
                                                 && p.Title == c.Title && p.DeletedAt == null))
                {
                    skipped++;
                    report.Append(Csv("skipped", c.FullPath, "already imported"));
                    continue;
                }

                var docxPath = Path.GetExtension(c.FullPath).Equals(".doc", StringComparison.OrdinalIgnoreCase)
                    ? docConverter.ToDocx(c.FullPath)
                    : c.FullPath;

                var (docx, colorStyleMap) = WordColors.Tag(docxPath);
                Mammoth.IResult<string> converted;
                using (docx)
                {
                    var converter = new Mammoth.DocumentConverter();
                    if (colorStyleMap.Length > 0) converter = converter.AddStyleMap(colorStyleMap);
                    converted = converter.ConvertToHtml(docx);
                }
                var html = OnlineHtml.Sanitize(ApplyHouseStyle(converted.Value, c.Title));
                if (html.Length > OnlineHtml.MaxHtmlChars)
                    throw new InvalidOperationException("Converted page is larger than the binder allows");

                var modified = File.GetLastWriteTimeUtc(c.FullPath);
                var sort = await db.Pages.Where(p => p.Module == module && p.SectionId == sectionId)
                    .MaxAsync(p => (int?)p.SortOrder) ?? -1;
                db.Pages.Add(new OnlinePage
                {
                    Module = module, SectionId = sectionId, Title = c.Title, Html = html,
                    SortOrder = sort + 1, Version = 1, CreatedAt = modified, UpdatedAt = modified
                });
                await db.SaveChangesAsync();

                imported++;
                report.Append(Csv("imported", c.FullPath,
                    converted.Warnings.Count == 0 ? "" : $"{converted.Warnings.Count} formatting note(s): {string.Join("; ", converted.Warnings.Take(3))}"));
            }
            catch (Exception ex)
            {
                failed++;
                db.ChangeTracker.Clear();
                report.Append(Csv("failed", c.FullPath, $"{ex.GetType().Name}: {ex.Message}"));
            }

            if ((i + 1) % 25 == 0 || i == candidates.Count - 1)
                Console.WriteLine($"  {i + 1}/{candidates.Count}  imported {imported}, skipped {skipped}, failed {failed}  ({clock.Elapsed:mm\\:ss})");
        }

        await File.WriteAllTextAsync(reportPath, report.ToString(), Encoding.UTF8);
        Console.WriteLine($"Done: imported {imported}, skipped {skipped}, failed {failed}. Report: {reportPath}");
        return failed > 0 ? 2 : 0;
    }

    /// <summary>Walk the folder, pruning excluded folders rather than visiting and discarding them.</summary>
    private static List<Candidate> Collect(string root)
    {
        var list = new List<Candidate>();
        void Walk(string dir, List<string> chain)
        {
            IEnumerable<string> files = [], subdirs = [];
            try
            {
                files = Directory.EnumerateFiles(dir);
                subdirs = Directory.EnumerateDirectories(dir);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { return; }

            foreach (var f in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(f);
                if (WordImportRules.SkipFile(name)) continue;
                var title = Path.GetFileNameWithoutExtension(name).Trim();
                if (title.Length > 200) title = title[..200];
                list.Add(new Candidate(f, chain.ToList(), title));
            }

            foreach (var d in subdirs.OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(d);
                if (WordImportRules.SkipFolder(name)) continue;
                chain.Add(name.Trim());
                Walk(d, chain);
                chain.RemoveAt(chain.Count - 1);
            }
        }
        Walk(root, []);
        return list;
    }

    /// <summary>
    /// Bring converted Word content into the binder's house style:
    /// the page title is shown above the content, so a first paragraph or heading that just
    /// repeats it is dropped; and wide tables are tagged to step their text down until they fit
    /// between the margins.
    /// </summary>
    internal static string ApplyHouseStyle(string html, string title)
    {
        var doc = new HtmlParser().ParseDocument($"<body>{html}</body>");
        var body = doc.Body!;

        // Leading empty paragraphs, then a repeated title.
        while (body.FirstElementChild is { } first && first.LocalName == "p"
               && string.IsNullOrWhiteSpace(first.TextContent) && first.QuerySelector("img") is null)
            first.Remove();
        if (body.FirstElementChild is { } lead && Normalize(lead.TextContent) == Normalize(title)
            && lead.QuerySelector("img, table") is null)
            lead.Remove();

        // Text colour (e.g. red for "new content awaiting approval"), tagged by WordColors.
        foreach (var span in body.QuerySelectorAll("span[class^='deco-color-']"))
        {
            var hex = span.ClassName!["deco-color-".Length..];
            span.RemoveAttribute("class");
            if (WordColors.IsHex(hex)) span.SetAttribute("style", $"color:#{hex}");
        }

        foreach (var table in body.QuerySelectorAll("table"))
        {
            var columns = table.QuerySelectorAll("tr")
                .Select(tr => tr.Children.Where(c => c.LocalName is "td" or "th")
                    .Sum(c => int.TryParse(c.GetAttribute("colspan"), out var span) && span > 0 ? span : 1))
                .DefaultIfEmpty(0).Max();
            var fit = columns switch { >= 12 => "fit-7", >= 9 => "fit-8", >= 6 => "fit-9", _ => null };
            if (fit is not null) table.ClassList.Add(fit);
        }

        return body.InnerHtml;
    }

    private static string Normalize(string s)
        => string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim().ToUpperInvariant();

    private static string? Option(string[] args, string name)
    {
        var i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static string Csv(string status, string path, string detail)
        => $"{status},\"{path.Replace("\"", "\"\"")}\",\"{detail.Replace("\"", "\"\"")}\"\r\n";

    /// <summary>Finds or creates the binder folder for a chain of folder names.</summary>
    private sealed class SectionResolver(OnlineDbContext db, string module)
    {
        private readonly Dictionary<(int Parent, string Name), int> _ids = new();

        public async Task LoadAsync()
        {
            foreach (var s in await db.Sections.AsNoTracking().Where(s => s.Module == module).ToListAsync())
                _ids[(s.ParentId ?? 0, s.Name.ToUpperInvariant())] = s.Id;
        }

        public async Task<int?> EnsureAsync(IReadOnlyList<string> chain)
        {
            int? parent = null;
            foreach (var name in chain)
            {
                var key = (parent ?? 0, name.ToUpperInvariant());
                if (!_ids.TryGetValue(key, out var id))
                {
                    var section = new OnlineSection { Module = module, Name = name, ParentId = parent };
                    db.Sections.Add(section);
                    await db.SaveChangesAsync();
                    id = section.Id;
                    _ids[key] = id;
                }
                parent = id;
            }
            return parent;
        }
    }
}

/// <summary>
/// Mammoth keeps structure (bold, lists, tables) but drops text colour, and the protocols use
/// colour for meaning — red marks new content awaiting approval. Mammoth does map character
/// styles, so each coloured run in an in-memory copy of the document is given a made-up
/// character style named after its colour, with a style-map rule turning it into a span;
/// <see cref="WordImporter.ApplyHouseStyle"/> then turns that span into an inline colour.
/// Runs that already have a character style keep it (it may carry meaning Mammoth maps itself).
/// </summary>
internal static class WordColors
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string StylePrefix = "DecoColor";

    public static bool IsHex(string s) => s.Length == 6 && s.All(char.IsAsciiHexDigit);

    /// <summary>The document as a stream ready for Mammoth, and the style map for its colours ("" if none).</summary>
    public static (Stream Docx, string StyleMap) Tag(string docxPath)
    {
        var stream = new MemoryStream();
        using (var file = File.OpenRead(docxPath)) file.CopyTo(stream);

        var colors = new SortedSet<string>();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Update, leaveOpen: true))
        {
            var documentEntry = zip.GetEntry("word/document.xml");
            var stylesEntry = zip.GetEntry("word/styles.xml");
            if (documentEntry is not null && stylesEntry is not null)
            {
                var document = Load(documentEntry);
                foreach (var rPr in document.Descendants(W + "r").Select(r => r.Element(W + "rPr")).OfType<XElement>())
                {
                    var hex = rPr.Element(W + "color")?.Attribute(W + "val")?.Value.ToUpperInvariant();
                    if (hex is null || !IsHex(hex) || IsNearBlack(hex) || rPr.Element(W + "rStyle") is not null) continue;
                    rPr.AddFirst(new XElement(W + "rStyle", new XAttribute(W + "val", StylePrefix + hex)));
                    colors.Add(hex);
                }

                if (colors.Count > 0)
                {
                    var styles = Load(stylesEntry);
                    foreach (var hex in colors)
                        styles.Root!.Add(new XElement(W + "style",
                            new XAttribute(W + "type", "character"), new XAttribute(W + "customStyle", "1"),
                            new XAttribute(W + "styleId", StylePrefix + hex),
                            new XElement(W + "name", new XAttribute(W + "val", $"{StylePrefix} {hex}"))));
                    Save(documentEntry, document);
                    Save(stylesEntry, styles);
                }
            }
        }

        stream.Position = 0;
        var map = string.Join("\n", colors.Select(hex => $"r[style-name='{StylePrefix} {hex}'] => span.deco-color-{hex}"));
        return (stream, map);
    }

    // Black and near-black is just "text"; tagging it would only add noise to every page.
    private static bool IsNearBlack(string hex)
        => Enumerable.Range(0, 3).Max(i => Convert.ToInt32(hex.Substring(i * 2, 2), 16)) < 0x50;

    private static XDocument Load(ZipArchiveEntry entry)
    {
        using var s = entry.Open();
        return XDocument.Load(s);
    }

    private static void Save(ZipArchiveEntry entry, XDocument xml)
    {
        using var s = entry.Open();
        s.SetLength(0);
        xml.Save(s, SaveOptions.DisableFormatting);
    }
}

/// <summary>
/// Turns legacy .doc files into .docx so they can be converted like the rest. Uses LibreOffice
/// when it is installed (as on a server), otherwise Microsoft Word through automation (as on an
/// office PC). Converted copies go to a temporary folder that is removed afterwards; the
/// originals are only ever opened read-only.
/// </summary>
internal sealed class LegacyDocConverter : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"decosop-import-{Guid.NewGuid():N}");
    private dynamic? _word;
    private int _counter;

    public string ToDocx(string docPath)
    {
        Directory.CreateDirectory(_tempDir);
        var target = Path.Combine(_tempDir, $"{++_counter}.docx");

        var soffice = PdfConversionService.GetSofficePath();
        if (soffice is not null)
        {
            // Copy under a unique name first: LibreOffice names its output after the input.
            var src = Path.Combine(_tempDir, $"{_counter}.doc");
            File.Copy(docPath, src, overwrite: true);
            var profile = Path.Combine(_tempDir, "lo-profile");
            using var p = Process.Start(new ProcessStartInfo(soffice,
                $"--headless --norestore -env:UserInstallation=file:///{profile.Replace('\\', '/')} --convert-to docx --outdir \"{_tempDir}\" \"{src}\"")
            { UseShellExecute = false, CreateNoWindow = true })!;
            if (!p.WaitForExit(TimeSpan.FromMinutes(2))) { try { p.Kill(true); } catch { } }
        }
        else
        {
            _word ??= StartWord();
            var missing = Type.Missing;
            // FileName, ConfirmConversions, ReadOnly, AddToRecentFiles, PasswordDocument: a dummy
            // password makes a protected file fail instead of stopping the import on a prompt.
            dynamic doc = WhenWordIsFree(() => _word.Documents.Open(docPath, false, true, false, "decosop-no-password",
                missing, missing, missing, missing, missing, missing, false));
            try { WhenWordIsFree(() => doc.SaveAs2(target, 16)); }   // 16 = wdFormatDocumentDefault (.docx)
            finally { WhenWordIsFree(() => doc.Close(0)); }
        }

        if (!File.Exists(target)) throw new InvalidOperationException("Could not convert this .doc file");
        return target;
    }

    /// <summary>
    /// Word turns automation calls away with RPC_E_SERVERCALL_RETRYLATER while it is still busy
    /// with the previous document (it surfaces as a COMException, or as a MissingMemberException
    /// when the rejection hits the late-bound lookup). That is a "try again", not a failure.
    /// </summary>
    private static object? WhenWordIsFree(Func<object?> call)
    {
        const int RetryLater = unchecked((int)0x8001010A);
        for (var attempt = 1; ; attempt++)
        {
            try { return call(); }
            catch (Exception ex) when (attempt < 20
                && (ex.HResult == RetryLater || ex.Message.Contains("0x8001010A", StringComparison.OrdinalIgnoreCase)))
            {
                Thread.Sleep(250 * Math.Min(attempt, 8));
            }
        }
    }

    private static void WhenWordIsFree(Action call) => WhenWordIsFree(() => { call(); return null; });

    private static dynamic StartWord()
    {
        var type = Type.GetTypeFromProgID("Word.Application")
                   ?? throw new InvalidOperationException("Neither LibreOffice nor Microsoft Word is installed, so .doc files cannot be read");
        dynamic word = Activator.CreateInstance(type)!;
        word.Visible = false;
        word.DisplayAlerts = 0;
        return word;
    }

    public void Dispose()
    {
        try { _word?.Quit(0); } catch { }
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
    }
}
