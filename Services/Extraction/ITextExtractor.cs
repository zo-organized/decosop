namespace DecoSOP.Services.Extraction;

/// <summary>
/// Pulls searchable text out of one family of file types.
///
/// Extraction is expressed as a batch even though most implementations handle a single file at
/// a time, because the LibreOffice-backed extractor is dramatically faster when it converts many
/// files per process launch (0.34s/file batched vs 3s/file one at a time — process startup
/// dominates). <see cref="MaxBatchSize"/> lets each implementation say what it wants; the
/// dispatcher in <see cref="TextExtractionService"/> does the chunking.
/// </summary>
public interface ITextExtractor
{
    /// <summary>Short stable identifier recorded against each indexed file, e.g. "pdfpig".</summary>
    string Name { get; }

    /// <summary>Extensions this extractor claims, lowercase and including the leading dot.</summary>
    IReadOnlySet<string> Extensions { get; }

    /// <summary>How many files to hand over per call. 1 for in-process readers.</summary>
    int MaxBatchSize => 1;

    /// <summary>Whether this extractor can run at all right now (e.g. is LibreOffice installed).</summary>
    bool IsAvailable => true;

    /// <summary>
    /// Extract every given file. The returned map is keyed by the input path and must contain an
    /// entry for every path passed in, so a partial batch failure can't silently lose files.
    /// </summary>
    Task<IReadOnlyDictionary<string, ExtractionResult>> ExtractAsync(
        IReadOnlyList<string> paths, CancellationToken ct = default);
}
