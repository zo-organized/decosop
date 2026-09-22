using DecoSOP.Data;
using DecoSOP.Models;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.EntityFrameworkCore;

namespace DecoSOP.Services;

/// <summary>
/// Folder-backed category/file service shared by the SOP and Document modules.
/// The watched folder is the source of truth: categories are real directories, files are
/// real files, and all writes go straight back into the folder. Statics are per closed
/// generic type, so each module keeps its own DataDirectory/SyncRoot/OpenBase.
/// </summary>
public class FolderFileService<TCat, TFile>(AppDbContext db)
    where TCat : class, ICategoryNode<TCat, TFile>, new()
    where TFile : class, IFileNode<TCat>, new()
{
    private static readonly long MaxFileSize = 50 * 1024 * 1024; // 50 MB
    public static string DataDirectory { get; set; } = AppContext.BaseDirectory;

    /// <summary>Local uploads folder name used when no SyncRoot is configured (set per module in Program.cs).</summary>
    public static string FallbackDirName { get; set; } = "uploads";

    /// <summary>When set to an existing directory, files are read/written in place here
    /// (e.g. a OneDrive-synced folder or network share) instead of the local uploads dir.</summary>
    public static string? SyncRoot { get; set; }

    /// <summary>Client-facing base (UNC or SharePoint/OneDrive URL) for "Open in Office" links.</summary>
    public static string? OpenBase { get; set; }

    private static void ValidateName(string? name, string field = "Name")
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException($"{field} is required.");
        if (name.Trim().Length > 200)
            throw new ArgumentException($"{field} must be 200 characters or fewer.");
    }

    // --- Upload directory ---

    public static string GetUploadDirectory()
    {
        if (!string.IsNullOrWhiteSpace(SyncRoot) && Directory.Exists(SyncRoot))
            return SyncRoot;
        var dir = Path.Combine(DataDirectory, FallbackDirName);
        Directory.CreateDirectory(dir);
        return dir;
    }

    // --- Categories ---

    public async Task<List<TCat>> GetCategoryTreeAsync()
    {
        // Two flat reads stitched in memory (EF can't express an ordered Include through the
        // generic interface). SortOrder ordering here is what keeps Children/Documents sorted.
        var cats = await db.Set<TCat>().AsNoTracking().OrderBy(c => c.SortOrder).ToListAsync();
        var files = await db.Set<TFile>().AsNoTracking().OrderBy(f => f.SortOrder).ToListAsync();

        var byId = cats.ToDictionary(c => c.Id);
        foreach (var cat in cats)
        {
            if (cat.ParentId is int pid && byId.TryGetValue(pid, out var parent))
            {
                cat.Parent = parent;
                parent.Children.Add(cat);
            }
        }
        foreach (var file in files)
        {
            if (byId.TryGetValue(file.CategoryId, out var cat))
            {
                file.Category = cat;
                cat.Documents.Add(file);
            }
        }
        return cats.Where(c => c.ParentId == null).ToList();
    }

    public async Task<List<TCat>> GetAllCategoriesAsync()
        => await db.Set<TCat>().OrderBy(c => c.SortOrder).ToListAsync();

    public async Task<TCat> CreateCategoryAsync(string name, int? parentId = null)
    {
        ValidateName(name, "Category name");
        name = name.Trim();
        var maxSort = await db.Set<TCat>()
            .Where(c => c.ParentId == parentId)
            .MaxAsync(c => (int?)c.SortOrder) ?? -1;

        // Create the real folder first (the folder is the source of truth); the DB row mirrors it.
        var chain = parentId is null ? new List<string>() : await GetNameChainAsync(parentId.Value);
        chain.Add(name);
        Directory.CreateDirectory(DiskPathForChain(chain));

        var category = new TCat { Name = name, SortOrder = maxSort + 1, ParentId = parentId };
        db.Set<TCat>().Add(category);
        await db.SaveChangesAsync();
        return category;
    }

    public async Task RenameCategoryAsync(int id, string newName)
    {
        ValidateName(newName, "Category name");
        newName = newName.Trim();
        var cat = await db.Set<TCat>().FindAsync(id);
        if (cat is null || cat.Name == newName) return;

        var oldChain = await GetNameChainAsync(id);
        if (IsSyntheticGeneral(oldChain))
            throw new InvalidOperationException("The General category is managed automatically and can't be renamed.");

        var newChain = oldChain.ToList();
        newChain[^1] = newName;

        // Rename the real folder, then repoint descendant files' stored relative paths.
        var oldPath = DiskPathForChain(oldChain);
        var newPath = DiskPathForChain(newChain);
        if (Directory.Exists(oldPath) && !PathsEqual(oldPath, newPath))
        {
            if (Directory.Exists(newPath))
                throw new InvalidOperationException($"A folder named \"{newName}\" already exists here.");
            Directory.Move(oldPath, newPath);
        }

        var oldPrefix = RelPathForChain(oldChain) + "/";
        var newPrefix = RelPathForChain(newChain) + "/";
        if (oldPrefix != newPrefix)
        {
            var affected = await db.Set<TFile>().Where(f => f.StoredFileName.StartsWith(oldPrefix)).ToListAsync();
            foreach (var f in affected)
                f.StoredFileName = newPrefix + f.StoredFileName.Substring(oldPrefix.Length);
        }

        cat.Name = newName;
        await db.SaveChangesAsync();
    }

    // Categories and files are never deleted from within the app: deletion happens by removing
    // the file/folder on disk (in OneDrive / the watched folder), which the reconciler then
    // mirrors into the DB. This keeps the folder the single source of truth.

    // --- Disk-path helpers (a category's Name is its real folder name) ---

    private async Task<List<string>> GetNameChainAsync(int categoryId)
    {
        var all = await db.Set<TCat>().AsNoTracking().ToListAsync();
        var parts = new List<string>();
        var current = all.FirstOrDefault(c => c.Id == categoryId);
        while (current is not null)
        {
            parts.Insert(0, current.Name);
            current = current.ParentId.HasValue ? all.FirstOrDefault(c => c.Id == current.ParentId) : null;
        }
        return parts;
    }

    /// <summary>The synthetic root "General" bucket (root-level files) has no real folder of its own.</summary>
    private static bool IsSyntheticGeneral(IReadOnlyList<string> chain)
        => chain.Count == 1 && chain[0] == "General";

    /// <summary>Relative path (forward slashes) of a category folder from the root; empty for the synthetic root "General".</summary>
    private static string RelPathForChain(IReadOnlyList<string> chain)
        => IsSyntheticGeneral(chain) ? "" : string.Join("/", chain);

    private static string DiskPathForChain(IReadOnlyList<string> chain)
    {
        var root = GetUploadDirectory();
        var rel = RelPathForChain(chain);
        return rel.Length == 0 ? root : Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
    }

    private static bool PathsEqual(string a, string b)
        => string.Equals(
            Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private static (string Path, string Name) UniqueFilePath(string dir, string fileName)
    {
        if (!File.Exists(Path.Combine(dir, fileName))) return (Path.Combine(dir, fileName), fileName);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var i = 2; ; i++)
        {
            var candidate = $"{stem} ({i}){ext}";
            var p = Path.Combine(dir, candidate);
            if (!File.Exists(p)) return (p, candidate);
        }
    }

    public async Task<string> GetCategoryPathAsync(int categoryId)
        => string.Join(" / ", await GetNameChainAsync(categoryId));

    // --- Documents ---

    public async Task<TFile?> GetDocumentAsync(int id)
    {
        var doc = await db.Set<TFile>().FirstOrDefaultAsync(d => d.Id == id);
        if (doc is not null)
        {
            var cat = await db.Set<TCat>().FirstOrDefaultAsync(c => c.Id == doc.CategoryId);
            if (cat is not null) doc.Category = cat;
        }
        return doc;
    }

    public async Task<TFile> UploadDocumentAsync(int categoryId, string title, IBrowserFile file)
    {
        ValidateName(title, "Title");
        var maxSort = await db.Set<TFile>()
            .Where(d => d.CategoryId == categoryId)
            .MaxAsync(d => (int?)d.SortOrder) ?? -1;

        // Write into the category's real folder using the real file name (the folder is the source of truth).
        var chain = await GetNameChainAsync(categoryId);
        var dir = DiskPathForChain(chain);
        Directory.CreateDirectory(dir);

        var safeName = string.Join("_", file.Name.Split(Path.GetInvalidFileNameChars()));
        var (filePath, finalName) = UniqueFilePath(dir, safeName);

        // Copy file to disk FIRST so a file I/O failure doesn't leave an orphaned DB record
        await using (var stream = file.OpenReadStream(MaxFileSize))
        await using (var fs = new FileStream(filePath, FileMode.Create))
        {
            await stream.CopyToAsync(fs);
        }

        var rel = RelPathForChain(chain);
        var storedFileName = (rel.Length == 0 ? "" : rel + "/") + finalName;

        var doc = new TFile
        {
            CategoryId = categoryId,
            Title = title.Trim(),
            FileName = finalName,
            ContentType = string.IsNullOrWhiteSpace(file.ContentType) ? FileScanUtil.GetContentType(finalName) : file.ContentType,
            FileSize = file.Size,
            SortOrder = maxSort + 1,
            StoredFileName = storedFileName
        };

        try
        {
            db.Set<TFile>().Add(doc);
            await db.SaveChangesAsync();
        }
        catch
        {
            // DB save failed — clean up the copied file
            try { File.Delete(filePath); } catch { }
            throw;
        }

        return doc;
    }

    /// <summary>
    /// Overwrite an existing file's bytes in place (same stored path) with an uploaded
    /// replacement, so a OneDrive-synced/shared folder propagates it as an update rather
    /// than a new file. Replacement must be the same file type.
    /// </summary>
    public async Task ReplaceFileAsync(int id, IBrowserFile file)
    {
        var doc = await db.Set<TFile>().FindAsync(id);
        if (doc is null) return;

        if (!string.Equals(Path.GetExtension(file.Name), Path.GetExtension(doc.FileName), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Replacement must be the same file type ({GetFileExtension(doc.FileName)}).");

        var path = GetFilePath(doc);
        // Write to a temp sibling then atomically swap, so a sync client never sees a half-written file.
        var tmp = path + ".uploadtmp";
        await using (var s = file.OpenReadStream(MaxFileSize))
        await using (var fs = new FileStream(tmp, FileMode.Create))
        {
            await s.CopyToAsync(fs);
        }
        File.Move(tmp, path, overwrite: true);

        doc.FileSize = file.Size;
        doc.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    // --- Helpers ---

    /// <summary>Full disk path for a stored file. May not exist — callers handle not-found.</summary>
    public string GetFilePath(TFile doc)
        => Path.Combine(GetUploadDirectory(), doc.StoredFileName);

    public static string FormatFileSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024.0):F1} MB";
    }

    public static string GetFileExtension(string fileName)
        => Path.GetExtension(fileName).TrimStart('.').ToUpperInvariant();
}
