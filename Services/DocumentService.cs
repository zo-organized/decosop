using DecoSOP.Data;
using DecoSOP.Models;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.EntityFrameworkCore;

namespace DecoSOP.Services;

public class DocumentService
{
    private readonly AppDbContext _db;
    private static readonly long MaxFileSize = 50 * 1024 * 1024; // 50 MB
    public static string DataDirectory { get; set; } = AppContext.BaseDirectory;

    /// <summary>When set to an existing directory, files are read/written in place here
    /// (e.g. a OneDrive-synced folder or network share) instead of the local doc-uploads dir.</summary>
    public static string? SyncRoot { get; set; }

    /// <summary>Client-facing base (UNC or SharePoint/OneDrive URL) for "Open in Office" links.</summary>
    public static string? OpenBase { get; set; }

    public DocumentService(AppDbContext db) => _db = db;

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
        var dir = Path.Combine(DataDirectory, "doc-uploads");
        Directory.CreateDirectory(dir);
        return dir;
    }

    // --- Categories ---

    public async Task<List<DocumentCategory>> GetCategoryTreeAsync()
    {
        var all = await _db.DocumentCategories
            .Include(c => c.Documents.OrderBy(d => d.SortOrder))
            .OrderBy(c => c.SortOrder)
            .ToListAsync();

        return all.Where(c => c.ParentId == null).ToList();
    }

    public async Task<List<DocumentCategory>> GetAllCategoriesAsync()
        => await _db.DocumentCategories.OrderBy(c => c.SortOrder).ToListAsync();

    public async Task<DocumentCategory> CreateCategoryAsync(string name, int? parentId = null)
    {
        ValidateName(name, "Category name");
        name = name.Trim();
        var maxSort = await _db.DocumentCategories
            .Where(c => c.ParentId == parentId)
            .MaxAsync(c => (int?)c.SortOrder) ?? -1;

        // Create the real folder first (the folder is the source of truth); the DB row mirrors it.
        var chain = parentId is null ? new List<string>() : await GetNameChainAsync(parentId.Value);
        chain.Add(name);
        Directory.CreateDirectory(DiskPathForChain(chain));

        var category = new DocumentCategory { Name = name, SortOrder = maxSort + 1, ParentId = parentId };
        _db.DocumentCategories.Add(category);
        await _db.SaveChangesAsync();
        return category;
    }

    public async Task RenameCategoryAsync(int id, string newName)
    {
        ValidateName(newName, "Category name");
        newName = newName.Trim();
        var cat = await _db.DocumentCategories.FindAsync(id);
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
            var affected = await _db.OfficeDocuments.Where(f => f.StoredFileName.StartsWith(oldPrefix)).ToListAsync();
            foreach (var f in affected)
                f.StoredFileName = newPrefix + f.StoredFileName.Substring(oldPrefix.Length);
        }

        cat.Name = newName;
        await _db.SaveChangesAsync();
    }

    // Categories and files are never deleted from within the app: deletion happens by removing
    // the file/folder on disk (in OneDrive / the watched folder), which the reconciler then
    // mirrors into the DB. This keeps the folder the single source of truth.

    // --- Disk-path helpers (a category's Name is its real folder name) ---

    private async Task<List<string>> GetNameChainAsync(int categoryId)
    {
        var all = await _db.DocumentCategories.AsNoTracking().ToListAsync();
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
    {
        var all = await _db.DocumentCategories.AsNoTracking().ToListAsync();
        var parts = new List<string>();
        var current = all.FirstOrDefault(c => c.Id == categoryId);
        while (current is not null)
        {
            parts.Insert(0, current.Name);
            current = current.ParentId.HasValue ? all.FirstOrDefault(c => c.Id == current.ParentId) : null;
        }
        return string.Join(" / ", parts);
    }

    public async Task<DocumentCategory?> GetCategoryWithChildrenAsync(int categoryId)
    {
        var all = await _db.DocumentCategories
            .Include(c => c.Documents.OrderBy(d => d.SortOrder))
            .OrderBy(c => c.SortOrder)
            .ToListAsync();

        return all.FirstOrDefault(c => c.Id == categoryId);
    }

    public async Task<List<(int Id, string Name)>> GetCategoryBreadcrumbsAsync(int categoryId)
    {
        var all = await _db.DocumentCategories.AsNoTracking().ToListAsync();
        var crumbs = new List<(int Id, string Name)>();
        var current = all.FirstOrDefault(c => c.Id == categoryId);
        while (current is not null)
        {
            crumbs.Insert(0, (current.Id, current.Name));
            current = current.ParentId.HasValue ? all.FirstOrDefault(c => c.Id == current.ParentId) : null;
        }
        return crumbs;
    }

    // --- Documents ---

    public async Task<OfficeDocument?> GetDocumentAsync(int id)
        => await _db.OfficeDocuments
            .Include(d => d.Category)
            .FirstOrDefaultAsync(d => d.Id == id);

    public async Task<OfficeDocument> UploadDocumentAsync(int categoryId, string title, IBrowserFile file)
    {
        ValidateName(title, "Title");
        var maxSort = await _db.OfficeDocuments
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

        var doc = new OfficeDocument
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
            _db.OfficeDocuments.Add(doc);
            await _db.SaveChangesAsync();
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
        var doc = await _db.OfficeDocuments.FindAsync(id);
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
        await _db.SaveChangesAsync();
    }

    public async Task<List<OfficeDocument>> SearchAsync(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];

        var term = query.Trim().ToLower();
        return await _db.OfficeDocuments
            .Include(d => d.Category)
            .Where(d => d.Title.ToLower().Contains(term)
                     || d.FileName.ToLower().Contains(term))
            .OrderBy(d => d.Category.SortOrder)
            .ThenBy(d => d.SortOrder)
            .ToListAsync();
    }

    // --- Helpers ---

    public string GetFilePath(OfficeDocument doc)
    {
        var dir = GetUploadDirectory();
        var path = Path.Combine(dir, doc.StoredFileName);
        if (File.Exists(path)) return path;

        // Fallback: strip "{id}_" prefix (handles import mismatch)
        var idx = doc.StoredFileName.IndexOf('_');
        if (idx > 0)
        {
            var fallback = Path.Combine(dir, doc.StoredFileName[(idx + 1)..]);
            if (File.Exists(fallback)) return fallback;
        }
        return path;
    }

    public static string FormatFileSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024.0):F1} MB";
    }

    public static string GetFileExtension(string fileName)
        => Path.GetExtension(fileName).TrimStart('.').ToUpperInvariant();
}
