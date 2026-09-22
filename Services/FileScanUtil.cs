using Microsoft.AspNetCore.StaticFiles;

namespace DecoSOP.Services;

/// <summary>
/// Shared helpers for scanning a folder tree into category/file structure: recursive
/// enumeration (a 1:1 mirror — no folder or extension filtering), name/title derivation
/// (folder names are used verbatim), and content-type mapping. Used by the folder reconciler.
/// </summary>
public static class FileScanUtil
{
    private static readonly EnumerationOptions Recurse = new() { RecurseSubdirectories = true };

    /// <summary>
    /// Recursively enumerate every file under baseDir, returning each file's relative path
    /// (forward slashes) and absolute path, ordered by relative path. No folder or extension
    /// filtering — the index is a 1:1 mirror of the folder. Only ~$ Office lock files
    /// (transient, not real content) are skipped; inaccessible directories are ignored.
    /// </summary>
    public static IEnumerable<(string RelPath, string FullPath)> WalkFiles(string baseDir)
        => Directory.EnumerateFiles(baseDir, "*", Recurse)
            .Where(f => !Path.GetFileName(f).StartsWith("~$"))
            .Select(f => (RelPath: Path.GetRelativePath(baseDir, f).Replace('\\', '/'), FullPath: f))
            .OrderBy(t => t.RelPath, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Recursively enumerate subdirectories under baseDir, each as a list of name segments
    /// (the category chain) from the root. Folder names are used verbatim so categories
    /// match the real folders exactly.
    /// </summary>
    public static IEnumerable<IReadOnlyList<string>> WalkDirectoryChains(string baseDir)
        => Directory.EnumerateDirectories(baseDir, "*", Recurse)
            .Select(d => Path.GetRelativePath(baseDir, d).Replace('\\', '/'))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .Select(IReadOnlyList<string> (p) => p.Split('/'));

    /// <summary>File title = the file name without its extension, verbatim (the extension shows as a badge).</summary>
    public static string CleanTitle(string filename) => Path.GetFileNameWithoutExtension(filename);

    /// <summary>The category-name chain for a file's relative path (folder names verbatim; excludes the filename).</summary>
    public static IReadOnlyList<string> CategoryChainForRelPath(string relPath)
    {
        var parts = relPath.Replace('\\', '/').Split('/');
        if (parts.Length <= 1)
            return ["General"];
        return parts[..^1].ToList();
    }

    private static readonly FileExtensionContentTypeProvider Mime = CreateMime();

    private static FileExtensionContentTypeProvider CreateMime()
    {
        var p = new FileExtensionContentTypeProvider();
        p.Mappings.TryAdd(".odt", "application/vnd.oasis.opendocument.text");
        p.Mappings.TryAdd(".ods", "application/vnd.oasis.opendocument.spreadsheet");
        return p;
    }

    public static string GetContentType(string filename)
        => Mime.TryGetContentType(filename, out var type) ? type : "application/octet-stream";
}
