using System.Net;
using System.Text.RegularExpressions;
using DecoSOP.Services.Extraction;
using Ganss.Xss;

namespace DecoSOP.Services.Online;

/// <summary>
/// HTML handling for ONLINE pages. DecoSOP has no login, so anyone on the network can write a
/// page — and anyone else then renders it. Every page is therefore sanitized on save (and again
/// on display, in case a restored backup carries something older) down to formatting markup:
/// no scripts, no event handlers, no iframes or forms, no javascript: links.
/// </summary>
public static partial class OnlineHtml
{
    /// <summary>
    /// Largest page accepted from the editor. Pasted screenshots are stored inline as data URIs,
    /// so this is generous, but a single page must never be able to fill the server's disk.
    /// </summary>
    public const int MaxHtmlChars = 20 * 1024 * 1024;

    public static string Sanitize(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;

        // A fresh instance per call: configuration is per-instance and this keeps it trivially
        // thread-safe. Construction is cheap next to parsing the page.
        var sanitizer = new HtmlSanitizer();

        // Tables are the reason this editor exists: keep their sizing and layout attributes.
        foreach (var attr in new[] { "class", "colspan", "rowspan", "width", "height", "border",
                                     "cellpadding", "cellspacing", "scope", "align", "valign" })
            sanitizer.AllowedAttributes.Add(attr);

        // Pasted images arrive as data: URIs. Allow that scheme for images only — a data: link
        // could otherwise carry an HTML document.
        sanitizer.AllowedSchemes.Add("data");
        sanitizer.FilterUrl += (_, e) =>
        {
            if (e.OriginalUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                && !(e.Tag.TagName.Equals("IMG", StringComparison.OrdinalIgnoreCase)
                     && e.OriginalUrl.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)))
            {
                e.SanitizedUrl = null;
            }
        };

        return sanitizer.Sanitize(html);
    }

    /// <summary>The page's readable text, for the search index.</summary>
    public static string ToPlainText(string? html)
    {
        if (string.IsNullOrEmpty(html)) return string.Empty;

        // Data URIs are megabytes of base64 that would otherwise land in the index as one token.
        var text = DataUri().Replace(html, "");
        // Block boundaries become line breaks and cells become spaces, so words from adjacent
        // paragraphs or table cells are not glued together once the tags are gone.
        text = BlockEnd().Replace(text, "\n");
        text = CellEnd().Replace(text, " ");
        text = Tag().Replace(text, "");
        return TextNormalizer.Normalize(WebUtility.HtmlDecode(text));
    }

    [GeneratedRegex(@"data:[^""'\s)]+", RegexOptions.IgnoreCase)]
    private static partial Regex DataUri();

    [GeneratedRegex(@"<br\s*/?>|</(p|div|li|tr|h[1-6]|table|ul|ol|blockquote|pre)\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockEnd();

    [GeneratedRegex(@"</t[dh]\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex CellEnd();

    [GeneratedRegex(@"<[^>]*>")]
    private static partial Regex Tag();
}
