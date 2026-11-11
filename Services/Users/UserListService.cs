using DecoSOP.Data;
using DecoSOP.Models;
using Microsoft.EntityFrameworkCore;

namespace DecoSOP.Services.Users;

/// <summary>A list as the header shows it.</summary>
public sealed record ListSummary(string Key, string Name, int Count, bool IsBuiltIn);

/// <summary>One thing on a list, resolved to something that can be shown and linked to.</summary>
public sealed record ListEntry(string EntityType, int EntityId, string Title, string? Where, string Href, string Badge)
{
    /// <summary>The file or page was deleted after it was added; it can only be removed from the list.</summary>
    public bool Missing => Href.Length == 0;
}

/// <summary>
/// The current user's lists: Favorites (the long-standing star, stored on
/// <see cref="UserPreference"/>), To Review (every user has one), and any lists they add.
/// A list can hold LOCAL files and folders and ONLINE pages side by side.
/// </summary>
public class UserListService
{
    public const string FavoritesKey = "favorites";
    public const string ReviewKey = "review";
    public const string FavoritesName = "Favorites";
    public const string ReviewName = "To Review";
    public const int MaxNameLength = 40;

    private static readonly string[] FavoriteTypes =
        [ListEntity.SopCategory, ListEntity.SopFile, ListEntity.DocumentCategory, ListEntity.OfficeDocument, ListEntity.OnlinePage];

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IDbContextFactory<OnlineDbContext> _onlineFactory;
    private readonly UserPreferenceService _prefs;
    private readonly DataCacheService _cache;
    private readonly ClientIdentityService _identity;
    private readonly ContextMenuState _menuState;

    public UserListService(IDbContextFactory<AppDbContext> dbFactory, IDbContextFactory<OnlineDbContext> onlineFactory,
        UserPreferenceService prefs, DataCacheService cache, ClientIdentityService identity, ContextMenuState menuState)
    {
        _dbFactory = dbFactory;
        _onlineFactory = onlineFactory;
        _prefs = prefs;
        _cache = cache;
        _identity = identity;
        _menuState = menuState;
        // A star clicked anywhere in the app changes the Favorites list too.
        _cache.FavoritesChanged += RaiseChanged;
    }

    /// <summary>Fires after any list in this browser changes, so the header, list column and buttons refresh.</summary>
    public event Action? Changed;

    private string Owner => _identity.ClientId;

    public async Task<List<ListSummary>> GetListsAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await EnsureReviewListAsync(db);

        var favorites = await db.UserPreferences
            .CountAsync(p => p.ClientId == Owner && p.IsFavorited && FavoriteTypes.Contains(p.EntityType));
        var lists = await db.UserLists.AsNoTracking()
            .Where(l => l.OwnerKey == Owner)
            .OrderByDescending(l => l.IsBuiltIn).ThenBy(l => l.SortOrder).ThenBy(l => l.Name)
            .Select(l => new { l.Id, l.Name, l.IsBuiltIn, Count = l.Items.Count })
            .ToListAsync();

        return [new ListSummary(FavoritesKey, FavoritesName, favorites, true),
                .. lists.Select(l => new ListSummary(l.IsBuiltIn ? ReviewKey : l.Id.ToString(), l.Name, l.Count, l.IsBuiltIn))];
    }

    /// <summary>The list's contents, newest first (favorites: folders first, then by title).</summary>
    public async Task<List<ListEntry>> GetItemsAsync(string key)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        List<(string Type, int Id)> refs;
        if (key == FavoritesKey)
        {
            refs = (await db.UserPreferences.AsNoTracking()
                    .Where(p => p.ClientId == Owner && p.IsFavorited && FavoriteTypes.Contains(p.EntityType))
                    .Select(p => new { p.EntityType, p.EntityId }).ToListAsync())
                .Select(p => (p.EntityType, p.EntityId)).ToList();
        }
        else
        {
            var list = await FindListAsync(db, key);
            if (list is null) return [];
            refs = (await db.UserListItems.AsNoTracking()
                    .Where(i => i.ListId == list.Id).OrderByDescending(i => i.AddedAt)
                    .Select(i => new { i.EntityType, i.EntityId }).ToListAsync())
                .Select(i => (i.EntityType, i.EntityId)).ToList();
        }

        var entries = await ResolveAsync(db, refs);
        return key == FavoritesKey
            ? entries.OrderBy(e => e.Badge == "FOLDER" ? 0 : 1).ThenBy(e => e.Title, StringComparer.OrdinalIgnoreCase).ToList()
            : entries;
    }

    /// <summary>Keys of the lists an item is on (for the "Lists" button's ticks).</summary>
    public async Task<HashSet<string>> GetMembershipAsync(string entityType, int entityId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var keys = new HashSet<string>();
        if (await db.UserPreferences.AnyAsync(p => p.ClientId == Owner && p.EntityType == entityType
                                                   && p.EntityId == entityId && p.IsFavorited))
            keys.Add(FavoritesKey);
        foreach (var l in await db.UserListItems
                     .Where(i => i.List.OwnerKey == Owner && i.EntityType == entityType && i.EntityId == entityId)
                     .Select(i => new { i.List.Id, i.List.IsBuiltIn }).ToListAsync())
            keys.Add(l.IsBuiltIn ? ReviewKey : l.Id.ToString());
        return keys;
    }

    /// <summary>Adds the item to the list, or takes it off if it is already there. Returns whether it is on now.</summary>
    public async Task<bool> ToggleAsync(string key, string entityType, int entityId)
    {
        bool isOn;
        if (key == FavoritesKey)
        {
            isOn = await _cache.RunExclusiveAsync(() => _prefs.ToggleFavoriteAsync(entityType, entityId));
            InvalidateFavorites(entityType);   // raises Changed through FavoritesChanged
            await _menuState.NotifyCategoryModified();   // the sidebar's Favorites section
            return isOn;
        }

        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            var list = await FindListAsync(db, key) ?? throw new InvalidOperationException("That list no longer exists.");
            var existing = await db.UserListItems.FirstOrDefaultAsync(i => i.ListId == list.Id
                                                                         && i.EntityType == entityType && i.EntityId == entityId);
            if (existing is null)
                db.UserListItems.Add(new UserListItem { ListId = list.Id, EntityType = entityType, EntityId = entityId, AddedAt = DateTime.UtcNow });
            else
                db.UserListItems.Remove(existing);
            await db.SaveChangesAsync();
            isOn = existing is null;
        }
        RaiseChanged();
        return isOn;
    }

    public async Task<string> CreateListAsync(string name)
    {
        name = CleanName(name);
        await using var db = await _dbFactory.CreateDbContextAsync();
        await EnsureNameFreeAsync(db, name, exceptId: null);
        var sort = await db.UserLists.Where(l => l.OwnerKey == Owner).MaxAsync(l => (int?)l.SortOrder) ?? 0;
        var list = new UserList { OwnerKey = Owner, Name = name, SortOrder = sort + 1, CreatedAt = DateTime.UtcNow };
        db.UserLists.Add(list);
        await db.SaveChangesAsync();
        RaiseChanged();
        return list.Id.ToString();
    }

    public async Task RenameListAsync(string key, string name)
    {
        name = CleanName(name);
        await using var db = await _dbFactory.CreateDbContextAsync();
        var list = await FindCustomListAsync(db, key);
        await EnsureNameFreeAsync(db, name, list.Id);
        list.Name = name;
        await db.SaveChangesAsync();
        RaiseChanged();
    }

    public async Task DeleteListAsync(string key)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        db.UserLists.Remove(await FindCustomListAsync(db, key));
        await db.SaveChangesAsync();
        RaiseChanged();
    }

    // ---- helpers ----

    private void RaiseChanged() => Changed?.Invoke();

    private void InvalidateFavorites(string entityType)
    {
        if (entityType is ListEntity.SopFile or ListEntity.SopCategory) _cache.InvalidateSopFavorites();
        else if (entityType is ListEntity.OfficeDocument or ListEntity.DocumentCategory) _cache.InvalidateDocFavorites();
        else RaiseChanged();   // ONLINE pages are not in the LOCAL cache
    }

    private async Task EnsureReviewListAsync(AppDbContext db)
    {
        if (await db.UserLists.AnyAsync(l => l.OwnerKey == Owner && l.IsBuiltIn)) return;
        db.UserLists.Add(new UserList { OwnerKey = Owner, Name = ReviewName, IsBuiltIn = true, CreatedAt = DateTime.UtcNow });
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException) { db.ChangeTracker.Clear(); }   // another tab created it at the same moment
    }

    private Task<UserList?> FindListAsync(AppDbContext db, string key)
        => key == ReviewKey
            ? db.UserLists.FirstOrDefaultAsync(l => l.OwnerKey == Owner && l.IsBuiltIn)
            : int.TryParse(key, out var id)
                ? db.UserLists.FirstOrDefaultAsync(l => l.OwnerKey == Owner && l.Id == id)
                : Task.FromResult<UserList?>(null);

    private async Task<UserList> FindCustomListAsync(AppDbContext db, string key)
    {
        var list = await FindListAsync(db, key) ?? throw new InvalidOperationException("That list no longer exists.");
        if (list.IsBuiltIn) throw new InvalidOperationException($"\"{list.Name}\" is built in and cannot be changed.");
        return list;
    }

    private async Task EnsureNameFreeAsync(AppDbContext db, string name, int? exceptId)
    {
        if (name.Equals(FavoritesName, StringComparison.OrdinalIgnoreCase)
            || await db.UserLists.AnyAsync(l => l.OwnerKey == Owner && l.Name == name && l.Id != exceptId))
            throw new InvalidOperationException($"You already have a list called \"{name}\".");
    }

    private static string CleanName(string? name)
    {
        name = string.Join(' ', (name ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (name.Length == 0) throw new ArgumentException("Enter a name for the list.");
        if (name.Length > MaxNameLength) throw new ArgumentException($"List names can be at most {MaxNameLength} characters.");
        return name;
    }

    /// <summary>Looks every reference up in its own table, keeping the given order.</summary>
    private async Task<List<ListEntry>> ResolveAsync(AppDbContext db, List<(string Type, int Id)> refs)
    {
        List<int> Ids(string type) => refs.Where(r => r.Type == type).Select(r => r.Id).ToList();
        var found = new Dictionary<(string, int), ListEntry>();

        var sopIds = Ids(ListEntity.SopFile);
        foreach (var f in await db.SopFiles.AsNoTracking().Where(f => sopIds.Contains(f.Id))
                     .Select(f => new { f.Id, f.Title, f.FileName, Where = f.Category.Name }).ToListAsync())
            found[(ListEntity.SopFile, f.Id)] = new(ListEntity.SopFile, f.Id, f.Title, f.Where, $"/sops/{f.Id}",
                SopFileService.GetFileExtension(f.FileName));

        var docIds = Ids(ListEntity.OfficeDocument);
        foreach (var d in await db.OfficeDocuments.AsNoTracking().Where(d => docIds.Contains(d.Id))
                     .Select(d => new { d.Id, d.Title, d.FileName, Where = d.Category.Name }).ToListAsync())
            found[(ListEntity.OfficeDocument, d.Id)] = new(ListEntity.OfficeDocument, d.Id, d.Title, d.Where, $"/documents/{d.Id}",
                SopFileService.GetFileExtension(d.FileName));

        var sopCatIds = Ids(ListEntity.SopCategory);
        foreach (var c in await db.SopCategories.AsNoTracking().Where(c => sopCatIds.Contains(c.Id))
                     .Select(c => new { c.Id, c.Name }).ToListAsync())
            found[(ListEntity.SopCategory, c.Id)] = new(ListEntity.SopCategory, c.Id, c.Name, "SOPs", $"/sops/category/{c.Id}", "FOLDER");

        var docCatIds = Ids(ListEntity.DocumentCategory);
        foreach (var c in await db.DocumentCategories.AsNoTracking().Where(c => docCatIds.Contains(c.Id))
                     .Select(c => new { c.Id, c.Name }).ToListAsync())
            found[(ListEntity.DocumentCategory, c.Id)] = new(ListEntity.DocumentCategory, c.Id, c.Name, "Documents", $"/documents/category/{c.Id}", "FOLDER");

        var pageIds = Ids(ListEntity.OnlinePage);
        if (pageIds.Count > 0)
        {
            await using var online = await _onlineFactory.CreateDbContextAsync();
            var pages = await online.Pages.AsNoTracking().Where(p => pageIds.Contains(p.Id) && p.DeletedAt == null)
                .Select(p => new { p.Id, p.Title, p.Module, p.SectionId }).ToListAsync();
            var sectionIds = pages.Where(p => p.SectionId != null).Select(p => p.SectionId!.Value).Distinct().ToList();
            var sections = await online.Sections.AsNoTracking().Where(s => sectionIds.Contains(s.Id))
                .ToDictionaryAsync(s => s.Id, s => s.Name);
            foreach (var p in pages)
                found[(ListEntity.OnlinePage, p.Id)] = new(ListEntity.OnlinePage, p.Id, p.Title,
                    p.SectionId is int sid && sections.TryGetValue(sid, out var name) ? name : null,
                    $"/online/{OnlineModule.RouteSegment(p.Module)}/page/{p.Id}", "ONLINE");
        }

        return refs.Select(r => found.TryGetValue(r, out var e)
                ? e
                : new ListEntry(r.Type, r.Id, "(no longer available)", null, "", ""))
            .ToList();
    }
}
