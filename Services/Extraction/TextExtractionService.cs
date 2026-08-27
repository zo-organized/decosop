namespace DecoSOP.Services.Extraction;

/// <summary>
/// Routes files to the extractor that handles their type, and chunks work into whatever batch
/// size each extractor asks for. Callers hand over a mixed list of paths and get back one
/// result per path, whatever happened to it.
/// </summary>
public sealed class TextExtractionService
{
    private readonly IReadOnlyList<ITextExtractor> _extractors;
    private readonly Dictionary<string, ITextExtractor> _byExtension;
    private readonly ILogger<TextExtractionService> _logger;

    public TextExtractionService(IEnumerable<ITextExtractor> extractors, ILogger<TextExtractionService> logger)
    {
        _extractors = extractors.ToList();
        _logger = logger;

        _byExtension = new Dictionary<string, ITextExtractor>(StringComparer.OrdinalIgnoreCase);
        foreach (var extractor in _extractors)
            foreach (var ext in extractor.Extensions)
                _byExtension[ext] = extractor;
    }

    /// <summary>Every extension the pipeline knows how to read, for reporting and diagnostics.</summary>
    public IReadOnlyCollection<string> SupportedExtensions => _byExtension.Keys;

    /// <summary>The extractor that would handle this file, or null if the type isn't supported.</summary>
    public ITextExtractor? ResolveFor(string fileNameOrPath)
        => _byExtension.GetValueOrDefault(Path.GetExtension(fileNameOrPath));

    /// <summary>Extract one file.</summary>
    public async Task<ExtractionResult> ExtractAsync(string path, CancellationToken ct = default)
    {
        var results = await ExtractManyAsync([path], ct);
        return results.TryGetValue(path, out var r)
            ? r
            : ExtractionResult.Failed("dispatcher", "Extractor returned no result for this file");
    }

    /// <summary>
    /// Extract a mixed batch. Files are grouped by extractor, then split into that extractor's
    /// preferred batch size — which is what lets the LibreOffice path amortise process startup
    /// across many files instead of paying it per file.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, ExtractionResult>> ExtractManyAsync(
        IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        var results = new Dictionary<string, ExtractionResult>(paths.Count, StringComparer.OrdinalIgnoreCase);
        var byExtractor = new Dictionary<ITextExtractor, List<string>>();

        foreach (var path in paths)
        {
            var extractor = ResolveFor(path);
            if (extractor is null)
            {
                var ext = Path.GetExtension(path);
                results[path] = ExtractionResult.Skipped("dispatcher",
                    string.IsNullOrEmpty(ext) ? "File has no extension" : $"No extractor for {ext} files");
                continue;
            }

            if (!extractor.IsAvailable)
            {
                results[path] = ExtractionResult.Skipped(extractor.Name,
                    $"{extractor.Name} is unavailable on this machine");
                continue;
            }

            (byExtractor.TryGetValue(extractor, out var list)
                ? list
                : byExtractor[extractor] = []).Add(path);
        }

        foreach (var (extractor, list) in byExtractor)
        {
            for (var i = 0; i < list.Count; i += extractor.MaxBatchSize)
            {
                ct.ThrowIfCancellationRequested();
                var chunk = list.GetRange(i, Math.Min(extractor.MaxBatchSize, list.Count - i));

                try
                {
                    var chunkResults = await extractor.ExtractAsync(chunk, ct);
                    foreach (var path in chunk)
                    {
                        results[path] = chunkResults.TryGetValue(path, out var r)
                            ? r
                            : ExtractionResult.Failed(extractor.Name, "Extractor returned no result for this file");
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // A whole chunk blowing up must not take down the run.
                    _logger.LogError(ex, "Extractor {Extractor} failed on a batch of {Count} file(s)",
                        extractor.Name, chunk.Count);
                    foreach (var path in chunk)
                        results[path] = ExtractionResult.Failed(extractor.Name, ex);
                }
            }
        }

        return results;
    }
}
