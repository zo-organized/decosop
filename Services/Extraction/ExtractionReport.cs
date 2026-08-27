using System.Diagnostics;
using System.Text;

namespace DecoSOP.Services.Extraction;

/// <summary>
/// Diagnostic pass that runs the extraction pipeline over a folder tree and reports what came
/// back, broken down by file type. Used to validate coverage against the real corpus and to
/// answer "why isn't this file searchable?" on the production host.
///
/// Invoke with: DecoSOP.exe extract-report [folder] [maxFilesPerType]
/// </summary>
public static class ExtractionReport
{
    public static async Task<int> RunAsync(string[] args)
    {
        var root = args.Length > 1 ? args[1] : SopFileService.GetUploadDirectory();
        var perType = args.Length > 2 && int.TryParse(args[2], out var n) ? n : int.MaxValue;

        if (!Directory.Exists(root))
        {
            Console.Error.WriteLine($"Folder not found: {root}");
            return 1;
        }

        using var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole().SetMinimumLevel(LogLevel.Warning));
        var service = new TextExtractionService(
            [
                new PlainTextExtractor(),
                new PdfTextExtractor(),
                new OpenXmlWordExtractor(),
                new SpreadsheetExtractor(),
                new LibreOfficeTextExtractor(loggerFactory.CreateLogger<LibreOfficeTextExtractor>()),
            ],
            loggerFactory.CreateLogger<TextExtractionService>());

        Console.WriteLine($"Root            : {root}");
        Console.WriteLine($"LibreOffice     : {(PdfConversionService.IsLibreOfficeAvailable() ? PdfConversionService.GetSofficePath() : "NOT INSTALLED")}");
        if (perType != int.MaxValue) Console.WriteLine($"Sampling        : {perType} files per type");
        Console.WriteLine();

        var byExtension = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(f => !Path.GetFileName(f).StartsWith("~$"))
            .GroupBy(f => Path.GetExtension(f).ToLowerInvariant())
            .OrderByDescending(g => g.Count())
            .ToList();

        var stats = new List<TypeStats>();
        var totalSw = Stopwatch.StartNew();

        foreach (var group in byExtension)
        {
            var files = Sample(group.ToList(), perType);
            Console.Write($"  {group.Key,-8} {files.Count,6} files ... ");

            var sw = Stopwatch.StartNew();
            var results = await service.ExtractManyAsync(files);
            sw.Stop();

            var stat = TypeStats.From(group.Key, group.Count(), files.Count, sw.Elapsed, results);
            stats.Add(stat);
            Console.WriteLine($"{sw.Elapsed.TotalSeconds,7:F1}s   ok {stat.Ok}  empty {stat.Empty}  failed {stat.Failed}  skipped {stat.Skipped}");
        }
        totalSw.Stop();

        PrintTable(stats);
        PrintProjection(stats, totalSw.Elapsed);
        PrintFailures(stats);
        return 0;
    }

    private static List<string> Sample(List<string> files, int max)
    {
        if (files.Count <= max) return files;
        var stride = Math.Max(1, files.Count / max);
        return files.Where((_, i) => i % stride == 0).Take(max).ToList();
    }

    private static void PrintTable(List<TypeStats> stats)
    {
        Console.WriteLine();
        Console.WriteLine($"{"type",-8} {"tested",7} {"ok",6} {"empty",6} {"failed",7} {"skip",5} {"ms/file",9} {"avg chars",10}  extractor");
        Console.WriteLine(new string('-', 88));
        foreach (var s in stats)
        {
            Console.WriteLine($"{s.Extension,-8} {s.Tested,7} {s.Ok,6} {s.Empty,6} {s.Failed,7} {s.Skipped,5} " +
                              $"{s.MsPerFile,9:F0} {s.AvgChars,10:F0}  {s.Extractor}");
        }
        Console.WriteLine(new string('-', 88));
        Console.WriteLine($"{"TOTAL",-8} {stats.Sum(s => s.Tested),7} {stats.Sum(s => s.Ok),6} {stats.Sum(s => s.Empty),6} " +
                          $"{stats.Sum(s => s.Failed),7} {stats.Sum(s => s.Skipped),5}");
    }

    private static void PrintProjection(List<TypeStats> stats, TimeSpan elapsed)
    {
        // Scale the sampled timings and text volume up to the full corpus.
        double projSeconds = 0, projChars = 0;
        int corpusFiles = 0, projOcr = 0;
        foreach (var s in stats)
        {
            if (s.Tested == 0) continue;
            var scale = s.CorpusCount / (double)s.Tested;
            projSeconds += s.Elapsed.TotalSeconds * scale;
            projChars += s.TotalChars * scale;
            projOcr += (int)Math.Round(s.Empty * scale);
            corpusFiles += s.CorpusCount;
        }

        Console.WriteLine();
        Console.WriteLine($"Elapsed             : {elapsed.TotalMinutes:F1} min for the tested set");
        Console.WriteLine($"Projected full run  : {projSeconds / 60:F1} min for all {corpusFiles} files (single-threaded)");
        Console.WriteLine($"Projected text      : {projChars / 1_048_576:F1} MB");
        Console.WriteLine($"OCR candidates      : {projOcr} files with no text layer");
    }

    private static void PrintFailures(List<TypeStats> stats)
    {
        var failures = stats.SelectMany(s => s.Errors)
            .GroupBy(e => e)
            .OrderByDescending(g => g.Count())
            .Take(12)
            .ToList();

        if (failures.Count == 0) return;

        Console.WriteLine();
        Console.WriteLine("Most common failures and skips:");
        foreach (var f in failures)
            Console.WriteLine($"  {f.Count(),4}x  {Truncate(f.Key, 100)}");
    }

    private static string Truncate(string s, int max)
    {
        s = s.Replace('\r', ' ').Replace('\n', ' ');
        return s.Length <= max ? s : s[..max] + "…";
    }

    private sealed record TypeStats(
        string Extension, int CorpusCount, int Tested, TimeSpan Elapsed,
        int Ok, int Empty, int Failed, int Skipped,
        long TotalChars, string Extractor, List<string> Errors)
    {
        public double MsPerFile => Tested == 0 ? 0 : Elapsed.TotalMilliseconds / Tested;
        public double AvgChars => Ok == 0 ? 0 : TotalChars / (double)Ok;

        public static TypeStats From(string ext, int corpusCount, int tested, TimeSpan elapsed,
            IReadOnlyDictionary<string, ExtractionResult> results)
        {
            var values = results.Values.ToList();
            var errors = values
                .Where(r => r.Status is ExtractionStatus.Failed or ExtractionStatus.Skipped)
                .Select(r => $"{ext}: {r.Error}")
                .ToList();

            return new TypeStats(
                ext, corpusCount, tested, elapsed,
                Ok: values.Count(r => r.Status == ExtractionStatus.Ok),
                Empty: values.Count(r => r.Status == ExtractionStatus.Empty),
                Failed: values.Count(r => r.Status == ExtractionStatus.Failed),
                Skipped: values.Count(r => r.Status == ExtractionStatus.Skipped),
                TotalChars: values.Sum(r => (long)r.Text.Length),
                Extractor: values.Select(r => r.Extractor).FirstOrDefault() ?? "-",
                Errors: errors);
        }
    }
}
