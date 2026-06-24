namespace DecoSOP.Services;

/// <summary>
/// Builds Microsoft Office URI-scheme links (ms-word:/ms-excel:/ms-powerpoint:) that
/// open a file directly in the desktop Office app for editing.
///
/// The link must point at a location the CLIENT can reach, which on a LAN is not the
/// server's local path. So it is built from the file's relative path plus a configured
/// client-facing base (a UNC share or a SharePoint/OneDrive web URL). When no base is
/// configured, the server's local path is used (only valid when the client IS the server).
/// </summary>
public static class OfficeProtocol
{
    /// <summary>Returns the Office URI scheme and app label for an editable file type, or null.</summary>
    public static (string Scheme, string App)? For(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".doc" or ".docx" or ".docm" or ".dot" or ".dotx" or ".rtf" or ".odt" => ("ms-word", "Word"),
            ".xls" or ".xlsx" or ".xlsm" or ".xlsb" or ".csv" or ".ods" => ("ms-excel", "Excel"),
            ".ppt" or ".pptx" or ".pptm" or ".odp" => ("ms-powerpoint", "PowerPoint"),
            _ => null
        };
    }

    /// <summary>
    /// Build an "open for edit" Office URI, or null if the type isn't an Office document.
    /// <paramref name="relPath"/> is the file's stored relative path (forward slashes);
    /// <paramref name="openBase"/> is the client-facing base (UNC or https URL) or null;
    /// <paramref name="localPath"/> is the server's absolute path (fallback for same-machine).
    /// </summary>
    public static string? OpenForEditUri(string fileName, string relPath, string? openBase, string localPath)
    {
        var info = For(fileName);
        if (info is null) return null;

        string url;
        if (!string.IsNullOrWhiteSpace(openBase))
        {
            var rel = relPath.Replace('\\', '/').TrimStart('/');
            if (openBase.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || openBase.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                // SharePoint / OneDrive web URL base.
                var baseUrl = openBase.TrimEnd('/');
                var encoded = string.Join('/', rel.Split('/').Select(Uri.EscapeDataString));
                url = $"{baseUrl}/{encoded}";
            }
            else
            {
                // UNC or local-path base -> percent-encoded file:// URI.
                var combined = Path.Combine(openBase, rel.Replace('/', Path.DirectorySeparatorChar));
                url = new Uri(combined).AbsoluteUri;
            }
        }
        else
        {
            url = new Uri(localPath).AbsoluteUri;
        }

        return $"{info.Value.Scheme}:ofe|u|{url}";
    }
}
