using System.Text.RegularExpressions;

namespace DecoSOP.Services;

/// <summary>
/// Shared helpers for scanning a folder tree into category/file structure:
/// recursive enumeration with skip rules, folder/title name cleaning, and
/// content-type mapping. Used by the folder reconciler.
/// </summary>
public static class FileScanUtil
{
    private static readonly string[] SkipPatterns =
    [
        @"^zz\s*archive$",
        @"^z\s*archive$",
        @"^x\s*old$",
        @"^z\s*old\b",
        @"^poss\s*older\b",
        @"^!+\s*.*to\s+go\s+thru",
        @"to\s+go\s+thru",
        @"^archive$"
    ];

    public static readonly HashSet<string> IncludeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx",
        ".txt", ".csv", ".png", ".jpg", ".jpeg", ".gif", ".zip",
        ".rtf", ".odt", ".ods"
    };

    /// <summary>
    /// Recursively enumerate importable files under baseDir, returning each file's
    /// relative path (forward slashes) and absolute path. Skips ~$ lock files,
    /// archive folders, and non-included extensions. Reads metadata only.
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
                continue;
            if (!IncludeExtensions.Contains(file.Extension))
                continue;
            var relPath = Path.GetRelativePath(basePath, file.FullName);
            yield return (relPath, file.FullName);
        }

        DirectoryInfo[] subdirs;
        try { subdirs = dir.GetDirectories(); }
        catch (UnauthorizedAccessException) { yield break; }

        foreach (var subdir in subdirs.OrderBy(d => d.Name))
        {
            if (ShouldSkipDir(subdir.Name))
                continue;
            foreach (var item in WalkDirectory(subdir, basePath))
                yield return item;
        }
    }

    /// <summary>
    /// Recursively enumerate non-skipped subdirectories under baseDir, returning each
    /// as a list of cleaned name segments (the category chain) from the root.
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
            if (ShouldSkipDir(subdir.Name))
                continue;
            var chain = new List<string>(prefix) { CleanDirName(subdir.Name) };
            yield return chain;
            foreach (var nested in WalkChains(subdir, chain))
                yield return nested;
        }
    }

    public static bool ShouldSkipDir(string dirname)
    {
        foreach (var pattern in SkipPatterns)
            if (Regex.IsMatch(dirname, pattern, RegexOptions.IgnoreCase))
                return true;
        return false;
    }

    public static string CleanDirName(string dirname)
    {
        var name = dirname;
        name = Regex.Replace(name, @"^[~!^@]+\s*", "");
        name = Regex.Replace(name, @"^\d+\s+", "");
        name = Regex.Replace(name, @"\s+", " ").Trim();
        return string.IsNullOrEmpty(name) ? dirname : name;
    }

    public static string CleanTitle(string filename)
    {
        var name = Path.GetFileNameWithoutExtension(filename);
        name = Regex.Replace(name, @"^[~!^]+\s*", "");
        name = name.Replace('_', ' ');
        name = Regex.Replace(name, @"\s+", " ").Trim();
        return string.IsNullOrEmpty(name) ? filename : name;
    }

    /// <summary>Compute the cleaned category-name chain for a file's relative path (excludes the filename).</summary>
    public static IReadOnlyList<string> CategoryChainForRelPath(string relPath)
    {
        var parts = relPath.Replace('\\', '/').Split('/');
        if (parts.Length <= 1)
            return ["General"];
        return parts[..^1].Select(CleanDirName).ToList();
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
