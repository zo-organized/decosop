using System.Text;
using UglyToad.PdfPig;

namespace DecoSOP.Services.Extraction;

/// <summary>
/// Reads the text layer out of a PDF. Shared by <see cref="PdfTextExtractor"/> (for PDFs in the
/// library) and <see cref="LibreOfficeTextExtractor"/> (for presentations, which LibreOffice can
/// only render to PDF because Impress has no plain-text export filter).
/// </summary>
public static class PdfTextReader
{
    public static string ReadText(string path, CancellationToken ct = default)
    {
        using var doc = PdfDocument.Open(path);
        var sb = new StringBuilder();
        var pages = 0;

        foreach (var page in doc.GetPages())
        {
            ct.ThrowIfCancellationRequested();
            if (++pages > ExtractionLimits.MaxPdfPages) break;
            if (sb.Length > ExtractionLimits.MaxChars) break;

            var text = page.Text;
            if (!string.IsNullOrWhiteSpace(text))
            {
                sb.Append(text);
                sb.Append('\n');
            }
        }

        return sb.ToString();
    }
}
