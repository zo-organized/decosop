using DecoSOP.Data;
using DecoSOP.Models;
using Microsoft.EntityFrameworkCore;

namespace DecoSOP.Services;

/// <summary>
/// Scoped cache that eliminates duplicate DB calls between the sidebar and pages.
/// Uses a SemaphoreSlim to serialize DB access so Task.Run callers from NavMenu
/// and page components never issue concurrent queries on the same DbContext.
/// Once results are cached, the semaphore is bypassed entirely (instant reads).
/// </summary>
public class DataCacheService
{
    private readonly SopFileService _sopFile;
    private readonly DocumentService _doc;
    private readonly UserPreferenceService _prefs;
    private readonly AppDbContext _db;
    private readonly SemaphoreSlim _sem = new(1, 1);
    private readonly Dictionary<string, object> _cache = new();

    public DataCacheService(SopFileService sopFile, DocumentService doc,
        UserPreferenceService prefs, AppDbContext db)
    {
        _sopFile = sopFile;
        _doc = doc;
        _prefs = prefs;
        _db = db;
    }

    private async Task<T> CachedAsync<T>(string key, Func<Task<T>> load) where T : class
    {
        if (_cache.TryGetValue(key, out var hit)) return (T)hit;
        await _sem.WaitAsync();
        try { return await LoadLockedAsync(key, load); }
        finally { _sem.Release(); }
    }

    /// <summary>Cache lookup/load for use INSIDE a load callback (the semaphore is already held).</summary>
    private async Task<T> LoadLockedAsync<T>(string key, Func<Task<T>> load) where T : class
    {
        if (_cache.TryGetValue(key, out var hit)) return (T)hit;
        var value = await load();
        _cache[key] = value;
        return value;
    }

    private void Remove(params string[] keys)
    {
        foreach (var key in keys) _cache.Remove(key);
    }

    // ---- Sop (files) ----

    public Task<List<SopCategory>> GetSopTreeAsync()
        => CachedAsync("sop.tree", _sopFile.GetCategoryTreeAsync);

    public Task<Dictionary<int, UserPreference>> GetSopCategoryPrefsAsync()
        => CachedAsync("sop.catPrefs", () => _prefs.GetAllForTypeAsync(nameof(SopCategory)));

    public Task<Dictionary<int, UserPreference>> GetSopFilePrefsAsync()
        => CachedAsync("sop.filePrefs", () => _prefs.GetAllForTypeAsync(nameof(SopFile)));

    public Task<List<SopCategory>> GetSopFavoriteCategoriesAsync()
        => CachedAsync("sop.favCats", async () =>
        {
            var favIds = await _prefs.GetFavoritedIdsAsync(nameof(SopCategory));
            if (favIds.Count == 0) return [];
            var favIdSet = new HashSet<int>(favIds);
            var tree = await LoadLockedAsync("sop.tree", _sopFile.GetCategoryTreeAsync);
            return FlattenTree<SopCategory>(tree, c => c.Children)
                .Where(c => favIdSet.Contains(c.Id)).ToList();
        });

    public Task<List<SopFile>> GetSopFavoriteDocumentsAsync()
        => CachedAsync("sop.favDocs", async () =>
        {
            var favIds = await _prefs.GetFavoritedIdsAsync(nameof(SopFile));
            if (favIds.Count == 0) return [];
            return await _db.SopFiles
                .AsNoTracking()
                .Include(d => d.Category)
                .Where(d => favIds.Contains(d.Id))
                .OrderBy(d => d.Title)
                .ToListAsync();
        });

    public async Task<SopCategory?> FindSopCategoryAsync(int id)
    {
        var tree = await GetSopTreeAsync();
        return FindInTree<SopCategory>(tree, id, c => c.Id, c => c.Children);
    }

    public async Task<List<(int Id, string Name)>> GetSopBreadcrumbsAsync(int categoryId)
    {
        var cat = await FindSopCategoryAsync(categoryId);
        return BuildBreadcrumbs(cat, c => c.Parent, c => (c.Id, c.Name));
    }

    public void InvalidateSop() => Remove("sop.tree", "sop.favCats", "sop.favDocs", "sop.catPrefs", "sop.filePrefs");
    public void InvalidateSopFavorites() => Remove("sop.favCats", "sop.favDocs", "sop.catPrefs", "sop.filePrefs");

    // ---- Document ----

    public Task<List<DocumentCategory>> GetDocTreeAsync()
        => CachedAsync("doc.tree", _doc.GetCategoryTreeAsync);

    public Task<Dictionary<int, UserPreference>> GetDocCategoryPrefsAsync()
        => CachedAsync("doc.catPrefs", () => _prefs.GetAllForTypeAsync(nameof(DocumentCategory)));

    public Task<Dictionary<int, UserPreference>> GetDocDocPrefsAsync()
        => CachedAsync("doc.docPrefs", () => _prefs.GetAllForTypeAsync(nameof(OfficeDocument)));

    public Task<List<DocumentCategory>> GetDocFavoriteCategoriesAsync()
        => CachedAsync("doc.favCats", async () =>
        {
            var favIds = await _prefs.GetFavoritedIdsAsync(nameof(DocumentCategory));
            if (favIds.Count == 0) return [];
            var favIdSet = new HashSet<int>(favIds);
            var tree = await LoadLockedAsync("doc.tree", _doc.GetCategoryTreeAsync);
            return FlattenTree<DocumentCategory>(tree, c => c.Children)
                .Where(c => favIdSet.Contains(c.Id)).ToList();
        });

    public Task<List<OfficeDocument>> GetDocFavoriteDocumentsAsync()
        => CachedAsync("doc.favDocs", async () =>
        {
            var favIds = await _prefs.GetFavoritedIdsAsync(nameof(OfficeDocument));
            if (favIds.Count == 0) return [];
            return await _db.OfficeDocuments
                .AsNoTracking()
                .Include(d => d.Category)
                .Where(d => favIds.Contains(d.Id))
                .OrderBy(d => d.Title)
                .ToListAsync();
        });

    public async Task<DocumentCategory?> FindDocCategoryAsync(int id)
    {
        var tree = await GetDocTreeAsync();
        return FindInTree<DocumentCategory>(tree, id, c => c.Id, c => c.Children);
    }

    public async Task<List<(int Id, string Name)>> GetDocBreadcrumbsAsync(int categoryId)
    {
        var cat = await FindDocCategoryAsync(categoryId);
        return BuildBreadcrumbs(cat, c => c.Parent, c => (c.Id, c.Name));
    }

    public void InvalidateDoc() => Remove("doc.tree", "doc.favCats", "doc.favDocs", "doc.catPrefs", "doc.docPrefs");
    public void InvalidateDocFavorites() => Remove("doc.favCats", "doc.favDocs", "doc.catPrefs", "doc.docPrefs");

    // ---- Invalidate all ----
    public void InvalidateAll() { InvalidateSop(); InvalidateDoc(); }

    /// <summary>
    /// Runs an async action under the shared semaphore so it never overlaps with
    /// cache reads or other DB operations on the same scoped DbContext.
    /// Use this for direct service calls (toggle favorite, create category, etc.)
    /// that bypass the cache.
    /// </summary>
    public async Task RunExclusiveAsync(Func<Task> action)
    {
        await _sem.WaitAsync();
        try { await action(); }
        finally { _sem.Release(); }
    }

    public async Task<T> RunExclusiveAsync<T>(Func<Task<T>> action)
    {
        await _sem.WaitAsync();
        try { return await action(); }
        finally { _sem.Release(); }
    }

    // ---- Helpers ----

    private static T? FindInTree<T>(IEnumerable<T> roots, int id,
        Func<T, int> getId, Func<T, IEnumerable<T>> getChildren) where T : class
    {
        foreach (var node in roots)
        {
            if (getId(node) == id) return node;
            var found = FindInTree(getChildren(node), id, getId, getChildren);
            if (found is not null) return found;
        }
        return null;
    }

    private static List<T> FlattenTree<T>(IEnumerable<T> roots, Func<T, IEnumerable<T>> getChildren)
    {
        var result = new List<T>();
        foreach (var node in roots)
        {
            result.Add(node);
            result.AddRange(FlattenTree(getChildren(node), getChildren));
        }
        return result;
    }

    private static List<(int Id, string Name)> BuildBreadcrumbs<T>(
        T? current, Func<T, T?> getParent, Func<T, (int Id, string Name)> toTuple) where T : class
    {
        var crumbs = new List<(int Id, string Name)>();
        while (current is not null)
        {
            crumbs.Insert(0, toTuple(current));
            current = getParent(current);
        }
        return crumbs;
    }
}
