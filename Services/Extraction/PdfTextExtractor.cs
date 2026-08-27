namespace DecoSOP.Services.Extraction;

/// <summary>
/// Reads the text layer of a PDF. Roughly 60% of this library's PDFs are scans with no text
/// layer at all; those come back <see cref="ExtractionStatus.Empty"/> rather than failed, which
/// is what marks them for OCR.
/// </summary>
public sealed class PdfTextExtractor : SingleFileExtractor
{
    public override string Name => "pdfpig";
    public override IReadOnlySet<string> Extensions { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".pdf" };

    protected override string ExtractOne(string path, CancellationToken ct)
        => PdfTextReader.ReadText(path, ct);
}
