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
    private readonly InventoryService _inv;
    private readonly UserPreferenceService _prefs;
    private readonly AppDbContext _db;
    private readonly SemaphoreSlim _sem = new(1, 1);

    public DataCacheService(SopFileService sopFile, DocumentService doc, InventoryService inv,
        UserPreferenceService prefs, AppDbContext db)
    {
        _sopFile = sopFile;
        _doc = doc;
        _inv = inv;
        _prefs = prefs;
        _db = db;
    }

    // ---- Sop (files) ----
    private List<SopCategory>? _sopTree;
    private List<SopCategory>? _sopFavCats;
    private List<SopFile>? _sopFavDocs;
    private Dictionary<int, UserPreference>? _sopCatPrefs;
    private Dictionary<int, UserPreference>? _sopFilePrefs;

    public async Task<List<SopCategory>> GetSopTreeAsync()
    {
        if (_sopTree is not null) return _sopTree;
        await _sem.WaitAsync();
        try { return _sopTree ??= await _sopFile.GetCategoryTreeAsync(); }
        finally { _sem.Release(); }
    }

    public async Task<Dictionary<int, UserPreference>> GetSopCategoryPrefsAsync()
    {
        if (_sopCatPrefs is not null) return _sopCatPrefs;
        await _sem.WaitAsync();
        try { return _sopCatPrefs ??= await _prefs.GetAllForTypeAsync(nameof(SopCategory)); }
        finally { _sem.Release(); }
    }

    public async Task<Dictionary<int, UserPreference>> GetSopFilePrefsAsync()
    {
        if (_sopFilePrefs is not null) return _sopFilePrefs;
        await _sem.WaitAsync();
        try { return _sopFilePrefs ??= await _prefs.GetAllForTypeAsync(nameof(SopFile)); }
        finally { _sem.Release(); }
    }

    public async Task<List<SopCategory>> GetSopFavoriteCategoriesAsync()
    {
        if (_sopFavCats is not null) return _sopFavCats;
        await _sem.WaitAsync();
        try
        {
            if (_sopFavCats is not null) return _sopFavCats;
            var favIds = await _prefs.GetFavoritedIdsAsync(nameof(SopCategory));
            if (favIds.Count == 0) { _sopFavCats = []; return _sopFavCats; }
            var favIdSet = new HashSet<int>(favIds);
            var tree = _sopTree ?? await _sopFile.GetCategoryTreeAsync();
            _sopTree ??= tree;
            _sopFavCats = FlattenTree<SopCategory>(tree, c => c.Children)
                .Where(c => favIdSet.Contains(c.Id)).ToList();
            return _sopFavCats;
        }
        finally { _sem.Release(); }
    }

    public async Task<List<SopFile>> GetSopFavoriteDocumentsAsync()
    {
        if (_sopFavDocs is not null) return _sopFavDocs;
        await _sem.WaitAsync();
        try
        {
            if (_sopFavDocs is not null) return _sopFavDocs;
            var favIds = await _prefs.GetFavoritedIdsAsync(nameof(SopFile));
            if (favIds.Count == 0) { _sopFavDocs = []; return _sopFavDocs; }
            _sopFavDocs = await _db.SopFiles
                .AsNoTracking()
                .Include(d => d.Category)
                .Where(d => favIds.Contains(d.Id))
                .OrderBy(d => d.Title)
                .ToListAsync();
            return _sopFavDocs;
        }
        finally { _sem.Release(); }
    }

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

    public void InvalidateSop() { _sopTree = null; _sopFavCats = null; _sopFavDocs = null; _sopCatPrefs = null; _sopFilePrefs = null; }
    public void InvalidateSopFavorites() { _sopFavCats = null; _sopFavDocs = null; _sopCatPrefs = null; _sopFilePrefs = null; }

    // ---- Document ----
    private List<DocumentCategory>? _docTree;
    private List<DocumentCategory>? _docFavCats;
    private List<OfficeDocument>? _docFavDocs;
    private Dictionary<int, UserPreference>? _docCatPrefs;
    private Dictionary<int, UserPreference>? _docDocPrefs;

    public async Task<List<DocumentCategory>> GetDocTreeAsync()
    {
        if (_docTree is not null) return _docTree;
        await _sem.WaitAsync();
        try { return _docTree ??= await _doc.GetCategoryTreeAsync(); }
        finally { _sem.Release(); }
    }

    public async Task<Dictionary<int, UserPreference>> GetDocCategoryPrefsAsync()
    {
        if (_docCatPrefs is not null) return _docCatPrefs;
        await _sem.WaitAsync();
        try { return _docCatPrefs ??= await _prefs.GetAllForTypeAsync(nameof(DocumentCategory)); }
        finally { _sem.Release(); }
    }

    public async Task<Dictionary<int, UserPreference>> GetDocDocPrefsAsync()
    {
        if (_docDocPrefs is not null) return _docDocPrefs;
        await _sem.WaitAsync();
        try { return _docDocPrefs ??= await _prefs.GetAllForTypeAsync(nameof(OfficeDocument)); }
        finally { _sem.Release(); }
    }

    public async Task<List<DocumentCategory>> GetDocFavoriteCategoriesAsync()
    {
        if (_docFavCats is not null) return _docFavCats;
        await _sem.WaitAsync();
        try
        {
            if (_docFavCats is not null) return _docFavCats;
            var favIds = await _prefs.GetFavoritedIdsAsync(nameof(DocumentCategory));
            if (favIds.Count == 0) { _docFavCats = []; return _docFavCats; }
            var favIdSet = new HashSet<int>(favIds);
            var tree = _docTree ?? await _doc.GetCategoryTreeAsync();
            _docTree ??= tree;
            _docFavCats = FlattenTree<DocumentCategory>(tree, c => c.Children)
                .Where(c => favIdSet.Contains(c.Id)).ToList();
            return _docFavCats;
        }
        finally { _sem.Release(); }
    }

    public async Task<List<OfficeDocument>> GetDocFavoriteDocumentsAsync()
    {
        if (_docFavDocs is not null) return _docFavDocs;
        await _sem.WaitAsync();
        try
        {
            if (_docFavDocs is not null) return _docFavDocs;
            var favIds = await _prefs.GetFavoritedIdsAsync(nameof(OfficeDocument));
            if (favIds.Count == 0) { _docFavDocs = []; return _docFavDocs; }
            _docFavDocs = await _db.OfficeDocuments
                .AsNoTracking()
                .Include(d => d.Category)
                .Where(d => favIds.Contains(d.Id))
                .OrderBy(d => d.Title)
                .ToListAsync();
            return _docFavDocs;
        }
        finally { _sem.Release(); }
    }

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

    public void InvalidateDoc() { _docTree = null; _docFavCats = null; _docFavDocs = null; _docCatPrefs = null; _docDocPrefs = null; }
    public void InvalidateDocFavorites() { _docFavCats = null; _docFavDocs = null; _docCatPrefs = null; _docDocPrefs = null; }

    // ---- Inventory ----
    private List<InventoryCategory>? _invTree;
    private List<InventoryCategory>? _invFavCats;
    private List<InventoryItem>? _invFavItems;
    private Dictionary<int, UserPreference>? _invCatPrefs;
    private Dictionary<int, UserPreference>? _invItemPrefs;
    private List<InventoryStaff>? _invStaff;
    private List<InventoryLocation>? _invLocations;

    public async Task<List<InventoryCategory>> GetInvTreeAsync()
    {
        if (_invTree is not null) return _invTree;
        await _sem.WaitAsync();
        try { return _invTree ??= await _inv.GetCategoryTreeAsync(); }
        finally { _sem.Release(); }
    }

    public async Task<Dictionary<int, UserPreference>> GetInvCategoryPrefsAsync()
    {
        if (_invCatPrefs is not null) return _invCatPrefs;
        await _sem.WaitAsync();
        try { return _invCatPrefs ??= await _prefs.GetAllForTypeAsync(nameof(InventoryCategory)); }
        finally { _sem.Release(); }
    }

    public async Task<Dictionary<int, UserPreference>> GetInvItemPrefsAsync()
    {
        if (_invItemPrefs is not null) return _invItemPrefs;
        await _sem.WaitAsync();
        try { return _invItemPrefs ??= await _prefs.GetAllForTypeAsync(nameof(InventoryItem)); }
        finally { _sem.Release(); }
    }

    public async Task<List<InventoryStaff>> GetInvStaffAsync()
    {
        if (_invStaff is not null) return _invStaff;
        await _sem.WaitAsync();
        try { return _invStaff ??= await _inv.GetStaffAsync(); }
        finally { _sem.Release(); }
    }

    public async Task<List<InventoryLocation>> GetInvLocationsAsync()
    {
        if (_invLocations is not null) return _invLocations;
        await _sem.WaitAsync();
        try { return _invLocations ??= await _inv.GetLocationsAsync(); }
        finally { _sem.Release(); }
    }

    public async Task<List<InventoryCategory>> GetInvFavoriteCategoriesAsync()
    {
        if (_invFavCats is not null) return _invFavCats;
        await _sem.WaitAsync();
        try
        {
            if (_invFavCats is not null) return _invFavCats;
            var favIds = await _prefs.GetFavoritedIdsAsync(nameof(InventoryCategory));
            if (favIds.Count == 0) { _invFavCats = []; return _invFavCats; }
            var favIdSet = new HashSet<int>(favIds);
            var tree = _invTree ?? await _inv.GetCategoryTreeAsync();
            _invTree ??= tree;
            _invFavCats = FlattenTree<InventoryCategory>(tree, c => c.Children)
                .Where(c => favIdSet.Contains(c.Id)).ToList();
            return _invFavCats;
        }
        finally { _sem.Release(); }
    }

    public async Task<List<InventoryItem>> GetInvFavoriteItemsAsync()
    {
        if (_invFavItems is not null) return _invFavItems;
        await _sem.WaitAsync();
        try
        {
            if (_invFavItems is not null) return _invFavItems;
            var favIds = await _prefs.GetFavoritedIdsAsync(nameof(InventoryItem));
            if (favIds.Count == 0) { _invFavItems = []; return _invFavItems; }
            _invFavItems = await _inv.GetItemsByIdsAsync(favIds);
            return _invFavItems;
        }
        finally { _sem.Release(); }
    }

    public async Task<InventoryCategory?> FindInvCategoryAsync(int id)
    {
        var tree = await GetInvTreeAsync();
        return FindInTree<InventoryCategory>(tree, id, c => c.Id, c => c.Children);
    }

    public async Task<List<(int Id, string Name)>> GetInvBreadcrumbsAsync(int categoryId)
    {
        var cat = await FindInvCategoryAsync(categoryId);
        return BuildBreadcrumbs(cat, c => c.Parent, c => (c.Id, c.Name));
    }

    public void InvalidateInv() { _invTree = null; _invFavCats = null; _invFavItems = null; _invCatPrefs = null; _invItemPrefs = null; _invStaff = null; _invLocations = null; }
    public void InvalidateInvFavorites() { _invFavCats = null; _invFavItems = null; _invCatPrefs = null; _invItemPrefs = null; }

    // ---- Invalidate all ----
    public void InvalidateAll() { InvalidateSop(); InvalidateDoc(); InvalidateInv(); }

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
