using System.Text;

namespace DecoSOP.Services.Extraction;

/// <summary>Reads .txt/.csv/.log, guessing between UTF-8 and Windows-1252.</summary>
public sealed class PlainTextExtractor : SingleFileExtractor
{
    public override string Name => "plaintext";
    public override IReadOnlySet<string> Extensions { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".txt", ".csv", ".log", ".md" };

    protected override string ExtractOne(string path, CancellationToken ct)
    {
        var bytes = File.ReadAllBytes(path);

        // A BOM settles it outright.
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);

        // No BOM: try strict UTF-8, and fall back to Windows-1252 when the bytes aren't valid
        // UTF-8. Older office exports are routinely 1252, and decoding those as UTF-8 turns every
        // curly quote and accented character into a replacement char.
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Latin1().GetString(bytes);
        }
    }

    private static Encoding Latin1()
    {
        // Windows-1252 needs the code-pages provider on .NET; Latin1 is built in and differs only
        // in the 0x80-0x9F range, which is close enough for indexing.
        try { return Encoding.GetEncoding(1252); }
        catch (NotSupportedException) { return Encoding.Latin1; }
        catch (ArgumentException) { return Encoding.Latin1; }
    }
}
