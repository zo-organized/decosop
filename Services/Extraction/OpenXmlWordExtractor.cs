using NPOI.XWPF.Extractor;
using NPOI.XWPF.UserModel;

namespace DecoSOP.Services.Extraction;

/// <summary>Reads modern Word documents (.docx). Body text, tables, headers and footers.</summary>
public sealed class OpenXmlWordExtractor : SingleFileExtractor
{
    public override string Name => "npoi-docx";
    public override IReadOnlySet<string> Extensions { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".docx", ".docm" };

    protected override string ExtractOne(string path, CancellationToken ct)
    {
        using var fs = File.OpenRead(path);
        using var doc = new XWPFDocument(fs);
        return new XWPFWordExtractor(doc).Text;
    }
}
