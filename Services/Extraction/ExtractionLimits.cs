namespace DecoSOP.Services.Extraction;

/// <summary>
/// Caps that keep one pathological file from stalling the queue, exhausting memory, or
/// dominating search ranking. A 400-page ledger is not more relevant than a one-page
/// protocol just because it contains more words.
/// </summary>
public static class ExtractionLimits
{
    /// <summary>Most text kept from a single file (~1M chars ≈ 500 pages of prose).</summary>
    public const int MaxChars = 1_000_000;

    /// <summary>Most PDF pages read from a single document.</summary>
    public const int MaxPdfPages = 400;

    /// <summary>Files larger than this are skipped — they are archives or media, not documents.</summary>
    public const long MaxFileBytes = 250L * 1024 * 1024;

    /// <summary>How long a single in-process extraction may run before it is abandoned.</summary>
    public static readonly TimeSpan SingleFileTimeout = TimeSpan.FromSeconds(60);
}
