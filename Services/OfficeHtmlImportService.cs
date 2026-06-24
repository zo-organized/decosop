using System.Diagnostics;
using System.Text.RegularExpressions;

namespace DecoSOP.Services;

/// <summary>
/// Converts an Office document (DOCX, DOC, XLSX, etc.) into self-contained HTML
/// using LibreOffice, with all images inlined as base64 data URIs so the result
/// can be stored in a single HtmlContent field and edited as a Web SOP / Web Doc.
/// </summary>
public static class OfficeHtmlImportService
{
    private static readonly HashSet<string> ConvertibleExtensions =
        [".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".odt", ".ods", ".odp", ".rtf"];

    public static bool CanConvert(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ConvertibleExtensions.Contains(ext) && PdfConversionService.IsLibreOfficeAvailable();
    }

    /// <summary>
    /// Convert the file at <paramref name="sourceFilePath"/> to self-contained HTML.
    /// Returns null if LibreOffice is unavailable or conversion fails.
    /// </summary>
    public static async Task<string?> ConvertToHtmlAsync(string sourceFilePath)
    {
        var soffice = PdfConversionService.GetSofficePath();
        if (soffice is null || !File.Exists(sourceFilePath))
            return null;

        var workDir = Path.Combine(Path.GetTempPath(), $"decosop_html_{Guid.NewGuid():N}");
        var profileDir = Path.Combine(Path.GetTempPath(), $"decosop_loprofile_{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = soffice,
                Arguments = $"--headless --norestore --convert-to html --outdir \"{workDir}\" \"{sourceFilePath}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                Environment =
                {
                    ["UserInstallation"] = $"file:///{profileDir.Replace('\\', '/')}"
                }
            };

            using var process = Process.Start(psi);
            if (process is null) return null;

            await process.StandardOutput.ReadToEndAsync();
            await process.StandardError.ReadToEndAsync();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(); } catch { }
                return null;
            }

            if (process.ExitCode != 0)
                return null;

            var htmlFile = Directory.EnumerateFiles(workDir, "*.html").FirstOrDefault()
                        ?? Directory.EnumerateFiles(workDir, "*.htm").FirstOrDefault();
            if (htmlFile is null)
                return null;

            var rawHtml = await File.ReadAllTextAsync(htmlFile);
            return PostProcess(rawHtml, workDir);
        }
        catch
        {
            return null;
        }
        finally
        {
            try { Directory.Delete(workDir, true); } catch { }
            try { if (Directory.Exists(profileDir)) Directory.Delete(profileDir, true); } catch { }
        }
    }

    /// <summary>
    /// Strip the document wrapper down to the body content and inline local images
    /// as base64 so the HTML is self-contained.
    /// </summary>
    private static string PostProcess(string html, string workDir)
    {
        // Keep only the <body> inner content.
        var bodyMatch = Regex.Match(html, @"<body[^>]*>(.*?)</body>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var body = bodyMatch.Success ? bodyMatch.Groups[1].Value : html;

        // Inline <img src="local-file"> references as base64 data URIs.
        // (Done before whitespace normalization because image filenames may contain spaces.)
        body = Regex.Replace(body, @"<img\b[^>]*?\bsrc\s*=\s*""([^""]+)""[^>]*?>", match =>
        {
            var src = match.Groups[1].Value;

            // Leave absolute URLs and already-inlined data URIs untouched.
            if (src.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                || src.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                return match.Value;

            var imgPath = Path.Combine(workDir, Uri.UnescapeDataString(src));
            if (!File.Exists(imgPath))
                return match.Value;

            try
            {
                var bytes = File.ReadAllBytes(imgPath);
                var mime = MimeForExtension(Path.GetExtension(imgPath));
                var dataUri = $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
                // Replace just the src value, preserving the rest of the tag.
                return match.Value.Replace($"\"{src}\"", $"\"{dataUri}\"");
            }
            catch
            {
                return match.Value;
            }
        }, RegexOptions.IgnoreCase);

        // Drop empty/whitespace-only paragraphs — Word uses these purely as vertical spacers,
        // and they bloat the reflowed HTML (and add blank lines under pre-wrap below).
        body = Regex.Replace(body, @"<p\b[^>]*>(?:\s|&nbsp;|<br\s*/?>)*</p>", "", RegexOptions.IgnoreCase);

        // Collapse insignificant whitespace (spaces + newlines) to single spaces, but KEEP tab
        // characters: Word/LibreOffice use tab stops for columnar "term -> description" layouts.
        // Without this, HTML collapses the tabs and the columns collide; with white-space:pre-wrap
        // + tab-size below, the tabs are honored as column stops again. Collapsing newlines first
        // ensures the editor's soft line-wraps don't become hard line breaks under pre-wrap.
        body = Regex.Replace(body, "[ \\r\\n]+", " ");

        // Remove whitespace sitting *between* tags so white-space:pre-wrap doesn't render it as
        // blank line boxes between paragraphs (tabs inside text runs are preceded by text, not '>',
        // so the column tabs are preserved).
        body = Regex.Replace(body, @">[ \t]+<", "><").Trim();

        return $"<div style=\"white-space: pre-wrap; tab-size: 8;\">{body}</div>";
    }

    private static string MimeForExtension(string ext) => ext.ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".bmp" => "image/bmp",
        ".webp" => "image/webp",
        ".svg" => "image/svg+xml",
        _ => "application/octet-stream"
    };
}
