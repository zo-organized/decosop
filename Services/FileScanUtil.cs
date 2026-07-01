namespace DecoSOP.Services;

/// <summary>
/// Shared helpers for scanning a folder tree into category/file structure: recursive
/// enumeration (a 1:1 mirror — no folder or extension filtering), name/title derivation
/// (folder names are used verbatim), and content-type mapping. Used by the folder reconciler.
/// </summary>
public static class FileScanUtil
{
    /// <summary>
    /// Recursively enumerate every file under baseDir, returning each file's relative path
    /// (forward slashes) and absolute path. No folder or extension filtering — the index is
    /// a 1:1 mirror of the folder. Only ~$ Office lock files (transient, not real content)
    /// are skipped. Reads metadata only.
    /// </summary>
    public static IEnumerable<(string RelPath, string FullPath)> WalkFiles(string baseDir)
    {
        var baseInfo = new DirectoryInfo(baseDir);
        foreach (var (rel, full) in WalkDirectory(baseInfo, baseInfo.FullName))
            yield return (rel.Replace('\\', '/'), full);
    }

    private static IEnumerable<(string RelPath, string FullPath)> WalkDirectory(DirectoryInfo dir, string basePath)
    {
        FileInfo[] files;
        try { files = dir.GetFiles(); }
        catch (UnauthorizedAccessException) { yield break; }

        foreach (var file in files.OrderBy(f => f.Name))
        {
            if (file.Name.StartsWith("~$"))
                continue; // transient Office lock file (not real content; OneDrive doesn't sync these)
            var relPath = Path.GetRelativePath(basePath, file.FullName);
            yield return (relPath, file.FullName);
        }

        DirectoryInfo[] subdirs;
        try { subdirs = dir.GetDirectories(); }
        catch (UnauthorizedAccessException) { yield break; }

        foreach (var subdir in subdirs.OrderBy(d => d.Name))
        {
            foreach (var item in WalkDirectory(subdir, basePath))
                yield return item;
        }
    }

    /// <summary>
    /// Recursively enumerate non-skipped subdirectories under baseDir, returning each
    /// as a list of name segments (the category chain) from the root. Folder names are
    /// used verbatim so categories match the real folders exactly.
    /// </summary>
    public static IEnumerable<IReadOnlyList<string>> WalkDirectoryChains(string baseDir)
    {
        var baseInfo = new DirectoryInfo(baseDir);
        return WalkChains(baseInfo, new List<string>());
    }

    private static IEnumerable<IReadOnlyList<string>> WalkChains(DirectoryInfo dir, List<string> prefix)
    {
        DirectoryInfo[] subdirs;
        try { subdirs = dir.GetDirectories(); }
        catch (UnauthorizedAccessException) { yield break; }

        foreach (var subdir in subdirs.OrderBy(d => d.Name))
        {
            var chain = new List<string>(prefix) { subdir.Name };
            yield return chain;
            foreach (var nested in WalkChains(subdir, chain))
                yield return nested;
        }
    }

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

    public static string GetContentType(string filename)
    {
        var ext = Path.GetExtension(filename).ToLowerInvariant();
        return ext switch
        {
            ".pdf" => "application/pdf",
            ".doc" => "application/msword",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".xls" => "application/vnd.ms-excel",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".ppt" => "application/vnd.ms-powerpoint",
            ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            ".txt" => "text/plain",
            ".csv" => "text/csv",
            ".rtf" => "application/rtf",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".zip" => "application/zip",
            ".odt" => "application/vnd.oasis.opendocument.text",
            ".ods" => "application/vnd.oasis.opendocument.spreadsheet",
            _ => "application/octet-stream"
        };
    }
}
