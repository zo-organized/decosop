using System.Text;

namespace DecoSOP.Services.Extraction;

/// <summary>
/// Tidies raw extractor output before it reaches the index. Extracted text is full of layout
/// artefacts — page-break runs, cell padding, stray control characters — that add index weight
/// and produce ugly snippets without making anything more findable.
/// </summary>
public static class TextNormalizer
{
    /// <summary>
    /// Collapse whitespace, drop control characters, and cap length. Keeps single newlines so
    /// snippets don't run separate headings together.
    /// </summary>
    public static string Normalize(string? raw, int maxChars = ExtractionLimits.MaxChars)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;

        var sb = new StringBuilder(Math.Min(raw.Length, maxChars));
        var pendingNewlines = 0;
        var pendingSpace = false;

        foreach (var ch in raw)
        {
            if (sb.Length >= maxChars) break;

            if (ch == '\n' || ch == '\r')
            {
                // \r\n counts once: a \r is only a newline if a \n doesn't immediately follow.
                if (ch == '\n' || pendingNewlines == 0) pendingNewlines++;
                pendingSpace = false;
                continue;
            }

            // Control characters, the replacement char from a bad decode, and private-use
            // codepoints all carry no meaning. The last of those matter here: Word stores
            // Wingdings and Symbol glyphs (bullets, ballot boxes, arrows) in U+E000-U+F8FF,
            // so without this they survive extraction and render as tofu boxes in snippets.
            if (ch < ' ' || ch == '\u007f' || ch == '\ufffd' || (ch >= '\ue000' && ch <= '\uf8ff'))
            {
                pendingSpace = true;
                continue;
            }

            if (ch == ' ' || ch == '\t' || ch == '\u00a0')
            {
                pendingSpace = true;
                continue;
            }

            if (sb.Length > 0)
            {
                // At most one blank line survives, and a newline outranks a space.
                if (pendingNewlines > 0) sb.Append(pendingNewlines >= 2 ? "\n\n" : "\n");
                else if (pendingSpace) sb.Append(' ');
            }
            pendingNewlines = 0;
            pendingSpace = false;
            sb.Append(ch);
        }

        return sb.ToString();
    }
}
