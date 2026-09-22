namespace DecoSOP.Services.Extraction;

/// <summary>Outcome of trying to pull searchable text out of one file.</summary>
public enum ExtractionStatus
{
    /// <summary>Text was recovered.</summary>
    Ok,

    /// <summary>The file opened cleanly but holds no text — a scanned PDF, a photo, an empty
    /// document. Distinct from <see cref="Failed"/>: there is nothing to retry, but it is
    /// exactly the set OCR can rescue later.</summary>
    Empty,

    /// <summary>Something went wrong (corrupt file, locked by another process, timeout).
    /// Worth retrying a bounded number of times.</summary>
    Failed,

    /// <summary>No extractor handles this type, or the tool one needs isn't installed.
    /// Not an error and not worth retrying.</summary>
    Skipped
}

/// <summary>The text recovered from a single file, plus how it was obtained.</summary>
public sealed record ExtractionResult(
    ExtractionStatus Status,
    string Text,
    string Extractor,
    string? Error = null)
{
    public static ExtractionResult FromText(string? text, string extractor)
        => string.IsNullOrWhiteSpace(text)
            ? new ExtractionResult(ExtractionStatus.Empty, string.Empty, extractor)
            : new ExtractionResult(ExtractionStatus.Ok, text, extractor);

    public static ExtractionResult Empty(string extractor)
        => new(ExtractionStatus.Empty, string.Empty, extractor);

    public static ExtractionResult Failed(string extractor, string error)
        => new(ExtractionStatus.Failed, string.Empty, extractor, error);

    public static ExtractionResult Failed(string extractor, Exception ex)
        => new(ExtractionStatus.Failed, string.Empty, extractor, $"{ex.GetType().Name}: {ex.Message}");

    public static ExtractionResult Skipped(string extractor, string reason)
        => new(ExtractionStatus.Skipped, string.Empty, extractor, reason);
}
