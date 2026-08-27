using System.Diagnostics;

namespace DecoSOP.Services.Extraction;

/// <summary>
/// Extracts legacy binary Office formats by driving LibreOffice headlessly.
///
/// This exists because NPOI no longer ships its legacy Word reader, and .doc is the single
/// largest format in this library. LibreOffice is already an (optional) installer component
/// powering Office previews, so it costs nothing new.
///
/// Batching is the whole game: launching one process per file costs ~3s each because startup
/// dominates, while converting 50 files in a single process costs ~0.34s each. Two details make
/// batching actually work — every launch needs its own UserInstallation profile (without it a
/// second invocation silently hands off to the already-running instance and converts nothing),
/// and output files are named after the source basename, so a batch must never contain two
/// files sharing a basename or one would overwrite the other.
/// </summary>
public sealed class LibreOfficeTextExtractor : ITextExtractor
{
    private readonly ILogger<LibreOfficeTextExtractor> _logger;

    public LibreOfficeTextExtractor(ILogger<LibreOfficeTextExtractor> logger) => _logger = logger;

    public string Name => "libreoffice";

    public IReadOnlySet<string> Extensions { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".doc", ".rtf", ".odt", ".wps", ".ppt", ".pptx", ".odp" };

    public int MaxBatchSize => 40;

    public bool IsAvailable => PdfConversionService.IsLibreOfficeAvailable();

    /// <summary>
    /// How a family of formats is converted. Writer documents export straight to plain text, but
    /// Impress has no plain-text filter at all — asking for one fails and produces nothing — so
    /// presentations are rendered to PDF and read back through the PDF text layer instead.
    /// </summary>
    private sealed record ConvertTarget(string Filter, string OutputExtension);

    private static readonly ConvertTarget ToText = new("txt:Text (encoded):UTF8", ".txt");
    private static readonly ConvertTarget ToPdf = new("pdf", ".pdf");

    private static readonly HashSet<string> PresentationFormats =
        new(StringComparer.OrdinalIgnoreCase) { ".ppt", ".pptx", ".odp" };

    private static ConvertTarget TargetFor(string path)
        => PresentationFormats.Contains(Path.GetExtension(path)) ? ToPdf : ToText;

    public async Task<IReadOnlyDictionary<string, ExtractionResult>> ExtractAsync(
        IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        var results = new Dictionary<string, ExtractionResult>(paths.Count, StringComparer.OrdinalIgnoreCase);

        var soffice = PdfConversionService.GetSofficePath();
        if (soffice is null)
        {
            foreach (var p in paths)
                results[p] = ExtractionResult.Skipped(Name, "LibreOffice is not installed on this machine");
            return results;
        }

        // One conversion filter per process launch, then split into runs containing no duplicate
        // basenames — the converter names its output after the source basename and would
        // otherwise clobber one file's text with another's.
        foreach (var byTarget in paths.GroupBy(TargetFor))
        {
            foreach (var group in GroupByUniqueBaseName(byTarget.ToList()))
            {
                ct.ThrowIfCancellationRequested();
                await ConvertGroupAsync(soffice, byTarget.Key, group, results, ct);
            }
        }

        return results;
    }

    private IEnumerable<List<string>> GroupByUniqueBaseName(IReadOnlyList<string> paths)
    {
        var remaining = new List<string>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in paths)
        {
            if (seenPaths.Add(p)) remaining.Add(p);   // guard against the same file listed twice
        }

        while (remaining.Count > 0)
        {
            var group = new List<string>();
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var deferred = new List<string>();

            foreach (var p in remaining)
            {
                var baseName = Path.GetFileNameWithoutExtension(p);
                if (group.Count < MaxBatchSize && taken.Add(baseName)) group.Add(p);
                else deferred.Add(p);
            }

            yield return group;
            remaining = deferred;
        }
    }

    private async Task ConvertGroupAsync(
        string soffice, ConvertTarget target, List<string> group,
        Dictionary<string, ExtractionResult> results, CancellationToken ct)
    {
        var workDir = Path.Combine(Path.GetTempPath(), $"decosop_extract_{Guid.NewGuid():N}");
        var outDir = Path.Combine(workDir, "out");
        var profileDir = Path.Combine(workDir, "profile");
        Directory.CreateDirectory(outDir);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = soffice,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add($"-env:UserInstallation=file:///{profileDir.Replace('\\', '/')}");
            psi.ArgumentList.Add("--headless");
            psi.ArgumentList.Add("--norestore");
            psi.ArgumentList.Add("--convert-to");
            psi.ArgumentList.Add(target.Filter);
            psi.ArgumentList.Add("--outdir");
            psi.ArgumentList.Add(outDir);
            foreach (var p in group) psi.ArgumentList.Add(p);

            using var process = Process.Start(psi);
            if (process is null)
            {
                Fail(group, results, "Could not start LibreOffice");
                return;
            }

            // Drain both pipes while waiting, so a chatty conversion can't fill a buffer and deadlock.
            var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);

            var budget = TimeSpan.FromSeconds(60 + 15 * group.Count);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(budget);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                TryKill(process);
                _logger.LogWarning("LibreOffice timed out after {Seconds}s converting {Count} file(s)",
                    budget.TotalSeconds, group.Count);
                // Whatever finished before the timeout is still collected below.
            }

            await Task.WhenAny(Task.WhenAll(stdout, stderr), Task.Delay(2000, CancellationToken.None));

            CollectOutputs(target, group, outDir, results, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Fail(group, results, $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try { if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true); } catch { }
        }
    }

    private void CollectOutputs(ConvertTarget target, List<string> group, string outDir,
        Dictionary<string, ExtractionResult> results, CancellationToken ct)
    {
        foreach (var source in group)
        {
            if (results.ContainsKey(source)) continue;

            var produced = Path.Combine(outDir, Path.GetFileNameWithoutExtension(source) + target.OutputExtension);
            if (!File.Exists(produced))
            {
                results[source] = ExtractionResult.Failed(Name, "LibreOffice produced no output for this file");
                continue;
            }

            try
            {
                var raw = target == ToPdf
                    ? PdfTextReader.ReadText(produced, ct)
                    // ReadAllText detects and strips the BOM the UTF8 filter writes.
                    : File.ReadAllText(produced);

                results[source] = ExtractionResult.FromText(TextNormalizer.Normalize(raw), Name);
            }
            catch (Exception ex)
            {
                results[source] = ExtractionResult.Failed(Name, ex);
            }
        }
    }

    private void Fail(List<string> group, Dictionary<string, ExtractionResult> results, string error)
    {
        foreach (var p in group)
            if (!results.ContainsKey(p))
                results[p] = ExtractionResult.Failed(Name, error);
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
    }
}
