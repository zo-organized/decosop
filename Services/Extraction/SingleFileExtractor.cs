namespace DecoSOP.Services.Extraction;

/// <summary>
/// Base for extractors that read one file at a time in-process. Handles the batch loop, the
/// per-file try/catch, size checks and normalization, so each implementation only has to know
/// how to turn one file into a string.
/// </summary>
public abstract class SingleFileExtractor : ITextExtractor
{
    public abstract string Name { get; }
    public abstract IReadOnlySet<string> Extensions { get; }

    /// <summary>Read the file and return its raw text. Throw to signal failure.</summary>
    protected abstract string ExtractOne(string path, CancellationToken ct);

    public Task<IReadOnlyDictionary<string, ExtractionResult>> ExtractAsync(
        IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        var results = new Dictionary<string, ExtractionResult>(paths.Count, StringComparer.OrdinalIgnoreCase);

        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();
            results[path] = ExtractSafely(path, ct);
        }

        return Task.FromResult<IReadOnlyDictionary<string, ExtractionResult>>(results);
    }

    private ExtractionResult ExtractSafely(string path, CancellationToken ct)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
                return ExtractionResult.Failed(Name, "File not found");
            if (info.Length == 0)
                return ExtractionResult.Empty(Name);
            if (info.Length > ExtractionLimits.MaxFileBytes)
                return ExtractionResult.Skipped(Name, $"File exceeds {ExtractionLimits.MaxFileBytes / 1048576} MB");

            var raw = ExtractOne(path, ct);
            return ExtractionResult.FromText(TextNormalizer.Normalize(raw), Name);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ExtractionResult.Failed(Name, ex);
        }
    }
}
