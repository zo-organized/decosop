using DecoSOP.Data;
using DecoSOP.Models;
using Microsoft.EntityFrameworkCore;

namespace DecoSOP.Services;

/// <summary>
/// CRUD + stock/audit operations for the DB-native inventory module (unified assets + consumables),
/// backed by Azure SQL (<see cref="InventoryDbContext"/>). Read queries use AsNoTracking so cached
/// results don't pin the scoped context's change-tracker for the circuit lifetime; the save paths all
/// re-fetch the target row via FindAsync, so they're unaffected. Low-stock/expiring predicates run in
/// SQL (SQL Server compares decimals/dates natively — the old in-memory filter was a SQLite workaround).
/// </summary>
public class InventoryService
{
    private readonly InventoryDbContext _db;

    public InventoryService(InventoryDbContext db) => _db = db;

    private static void ValidateName(string? name, string field = "Name")
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException($"{field} is required.");
        if (name.Trim().Length > 200)
            throw new ArgumentException($"{field} must be 200 characters or fewer.");
    }

    // --- Categories ---

    public async Task<List<InventoryCategory>> GetCategoryTreeAsync()
    {
        var all = await _db.InventoryCategories
            .AsNoTrackingWithIdentityResolution() // no tracking, but keep cross-entity Parent/Children fixup
            .Include(c => c.Items.OrderBy(i => i.SortOrder))
            .OrderBy(c => c.SortOrder)
            .ToListAsync();
        // EF relationship fixup populates Parent/Children on the loaded set.
        return all.Where(c => c.ParentId == null).ToList();
    }

    public async Task<List<InventoryCategory>> GetAllCategoriesAsync()
        => await _db.InventoryCategories.OrderBy(c => c.SortOrder).ThenBy(c => c.Name).ToListAsync();

    public async Task<InventoryCategory> CreateCategoryAsync(string name, int? parentId = null)
    {
        ValidateName(name, "Category name");
        name = name.Trim();
        // Enforce sibling-name uniqueness in app code so behavior is identical on SQLite (NULLs distinct)
        // and Azure SQL (NULLs equal). See the plan's unique-index NULL-nuance note.
        if (await _db.InventoryCategories.AnyAsync(c => c.ParentId == parentId && c.Name == name))
            throw new InvalidOperationException($"A category named \"{name}\" already exists here.");

        var maxSort = await _db.InventoryCategories
            .Where(c => c.ParentId == parentId)
            .MaxAsync(c => (int?)c.SortOrder) ?? -1;

        var cat = new InventoryCategory { Name = name, ParentId = parentId, SortOrder = maxSort + 1 };
        _db.InventoryCategories.Add(cat);
        await _db.SaveChangesAsync();
        return cat;
    }

    public async Task RenameCategoryAsync(int id, string newName)
    {
        ValidateName(newName, "Category name");
        newName = newName.Trim();
        var cat = await _db.InventoryCategories.FindAsync(id);
        if (cat is null || cat.Name == newName) return;
        if (await _db.InventoryCategories.AnyAsync(c => c.ParentId == cat.ParentId && c.Name == newName && c.Id != id))
            throw new InvalidOperationException($"A category named \"{newName}\" already exists here.");
        cat.Name = newName;
        await _db.SaveChangesAsync();
    }

    public async Task DeleteCategoryAsync(int id)
    {
        var cat = await _db.InventoryCategories
            .Include(c => c.Children)
            .Include(c => c.Items)
            .FirstOrDefaultAsync(c => c.Id == id);
        if (cat is null) return;
        if (cat.Children.Count > 0)
            throw new InvalidOperationException("Remove or move the subcategories first.");
        if (cat.Items.Count > 0)
            throw new InvalidOperationException("This category still has items — move or delete them first.");
        _db.InventoryCategories.Remove(cat);
        await _db.SaveChangesAsync();
    }

    public async Task<List<(int Id, string Name)>> GetCategoryBreadcrumbsAsync(int categoryId)
    {
        var all = await _db.InventoryCategories.AsNoTracking().ToListAsync();
        var crumbs = new List<(int Id, string Name)>();
        var current = all.FirstOrDefault(c => c.Id == categoryId);
        while (current is not null)
        {
            crumbs.Insert(0, (current.Id, current.Name));
            current = current.ParentId.HasValue ? all.FirstOrDefault(c => c.Id == current.ParentId) : null;
        }
        return crumbs;
    }

    private async Task<HashSet<int>> GetCategoryAndDescendantIdsAsync(int rootId)
    {
        var all = await _db.InventoryCategories.AsNoTracking()
            .Select(c => new { c.Id, c.ParentId }).ToListAsync();
        var byParent = all.ToLookup(c => c.ParentId);
        var result = new HashSet<int>();
        var stack = new Stack<int>();
        stack.Push(rootId);
        while (stack.Count > 0)
        {
            var id = stack.Pop();
            if (!result.Add(id)) continue;
            foreach (var child in byParent[id]) stack.Push(child.Id);
        }
        return result;
    }

    // --- Items ---

    public async Task<List<InventoryItem>> GetItemsAsync(
        int? categoryId = null, int? locationId = null, InventoryKind? kind = null, string? search = null)
    {
        var q = _db.InventoryItems
            .AsNoTracking()
            .Include(i => i.Category)
            .Include(i => i.Location)
            .AsQueryable();

        if (categoryId is int cid)
        {
            var ids = await GetCategoryAndDescendantIdsAsync(cid);
            q = q.Where(i => ids.Contains(i.CategoryId));
        }
        if (locationId is int lid) q = q.Where(i => i.LocationId == lid);
        if (kind is InventoryKind k) q = q.Where(i => i.Kind == k);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var t = search.Trim().ToLower();
            q = q.Where(i => i.Title.ToLower().Contains(t)
                || (i.Barcode != null && i.Barcode.ToLower().Contains(t))
                || (i.Brand != null && i.Brand.ToLower().Contains(t))
                || (i.Model != null && i.Model.ToLower().Contains(t))
                || (i.ItemType != null && i.ItemType.ToLower().Contains(t))
                || (i.Identifier != null && i.Identifier.ToLower().Contains(t)));
        }
        return await q.OrderBy(i => i.Title).ToListAsync();
    }

    public async Task<InventoryItem?> GetItemAsync(int id)
        => await _db.InventoryItems
            .AsNoTracking()
            .Include(i => i.Category)
            .Include(i => i.Location)
            .FirstOrDefaultAsync(i => i.Id == id);

    /// <summary>Load a specific set of items by id (used to resolve favorited items for the sidebar).</summary>
    public async Task<List<InventoryItem>> GetItemsByIdsAsync(IReadOnlyCollection<int> ids)
    {
        if (ids.Count == 0) return [];
        return await _db.InventoryItems
            .AsNoTracking()
            .Include(i => i.Category)
            .Where(i => ids.Contains(i.Id))
            .OrderBy(i => i.Title)
            .ToListAsync();
    }

    public async Task<InventoryItem> CreateItemAsync(InventoryItem item, string staffName)
    {
        ValidateName(item.Title, "Title");
        item.Title = item.Title.Trim();
        if (string.IsNullOrWhiteSpace(item.Status)) item.Status = "Active";

        var maxSort = await _db.InventoryItems
            .Where(i => i.CategoryId == item.CategoryId)
            .MaxAsync(i => (int?)i.SortOrder) ?? -1;
        item.SortOrder = maxSort + 1;
        item.CreatedAt = item.UpdatedAt = DateTime.UtcNow;
        item.LastUpdatedBy = staffName;

        _db.InventoryItems.Add(item);
        await _db.SaveChangesAsync();
        await LogAsync(item.Id, staffName, "Created", null,
            item.Kind == InventoryKind.Consumable ? item.QuantityOnHand : null, null);
        return item;
    }

    public async Task<InventoryItem?> UpdateItemAsync(InventoryItem item, string staffName)
    {
        var existing = await _db.InventoryItems.FindAsync(item.Id);
        if (existing is null) return null;
        ValidateName(item.Title, "Title");

        existing.Kind = item.Kind;
        existing.CategoryId = item.CategoryId;
        existing.LocationId = item.LocationId;
        existing.Title = item.Title.Trim();
        existing.Description = item.Description;
        existing.Barcode = item.Barcode;
        existing.Lot = item.Lot;
        existing.Identifier = item.Identifier;
        existing.ItemType = item.ItemType;
        existing.Brand = item.Brand;
        existing.Model = item.Model;
        existing.Status = string.IsNullOrWhiteSpace(item.Status) ? "Active" : item.Status;
        existing.StatusStartDate = item.StatusStartDate;
        existing.StatusEndDate = item.StatusEndDate;
        existing.CurrentValue = item.CurrentValue;
        existing.StartingValue = item.StartingValue;
        existing.DateOfValue = item.DateOfValue;
        existing.Manufacturer = item.Manufacturer;
        existing.Seller = item.Seller;
        existing.Price = item.Price;
        existing.PurchaseDate = item.PurchaseDate;
        existing.WarrantyInfo = item.WarrantyInfo;
        existing.WarrantyExpiration = item.WarrantyExpiration;
        existing.SdsOnFile = item.SdsOnFile;
        existing.Use = item.Use;
        existing.QuantityOnHand = item.QuantityOnHand;
        existing.Unit = item.Unit;
        existing.ReorderPoint = item.ReorderPoint;
        existing.ExpirationDate = item.ExpirationDate;
        existing.UpdatedAt = DateTime.UtcNow;
        existing.LastUpdatedBy = staffName;

        await _db.SaveChangesAsync();
        await LogAsync(existing.Id, staffName, "Edited", null, null, null);
        return existing;
    }

    public async Task DeleteItemAsync(int id, string staffName)
    {
        var item = await _db.InventoryItems.FindAsync(id);
        if (item is null) return;
        _db.InventoryItems.Remove(item);   // activities cascade
        await _db.SaveChangesAsync();
    }

    // --- Stock movements ---

    public async Task<InventoryItem?> AdjustStockAsync(int itemId, decimal delta, string staffName, string action, string? note)
    {
        var item = await _db.InventoryItems.FindAsync(itemId);
        if (item is null) return null;
        var newQty = (item.QuantityOnHand ?? 0) + delta;
        if (newQty < 0) newQty = 0;
        item.QuantityOnHand = newQty;
        item.UpdatedAt = DateTime.UtcNow;
        item.LastUpdatedBy = staffName;
        await _db.SaveChangesAsync();
        await LogAsync(itemId, staffName, action, delta, newQty, note);
        return item;
    }

    public async Task<InventoryItem?> SetCountAsync(int itemId, decimal newQty, string staffName, string? note)
    {
        var item = await _db.InventoryItems.FindAsync(itemId);
        if (item is null) return null;
        if (newQty < 0) newQty = 0;
        var delta = newQty - (item.QuantityOnHand ?? 0);
        item.QuantityOnHand = newQty;
        item.UpdatedAt = DateTime.UtcNow;
        item.LastUpdatedBy = staffName;
        await _db.SaveChangesAsync();
        await LogAsync(itemId, staffName, "Count", delta, newQty, note);
        return item;
    }

    // --- Queries ---

    /// <summary>Consumables at or below their reorder point (filtered in SQL — SQL Server compares decimals natively).</summary>
    public async Task<List<InventoryItem>> GetLowStockAsync()
        => await _db.InventoryItems
            .AsNoTracking()
            .Include(i => i.Category).Include(i => i.Location)
            .Where(i => i.Kind == InventoryKind.Consumable
                && i.ReorderPoint != null
                && (i.QuantityOnHand ?? 0) <= i.ReorderPoint!.Value)
            .OrderBy(i => i.Title)
            .ToListAsync();

    /// <summary>Consumables expiring within <paramref name="days"/> days, including already-expired.</summary>
    public async Task<List<InventoryItem>> GetExpiringAsync(int days = 30)
    {
        // Half-open upper bound (< cutoff+1 day) so the whole cutoff calendar day is included, without
        // relying on DateTime.Date translation; no lower bound keeps already-expired items in the result.
        var cutoffExclusive = DateTime.UtcNow.Date.AddDays(days + 1);
        return await _db.InventoryItems
            .AsNoTracking()
            .Include(i => i.Category).Include(i => i.Location)
            .Where(i => i.Kind == InventoryKind.Consumable
                && i.ExpirationDate != null
                && i.ExpirationDate!.Value < cutoffExclusive)
            .OrderBy(i => i.ExpirationDate)
            .ToListAsync();
    }

    public async Task<InventoryItem?> LookupByBarcodeAsync(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var c = code.Trim();
        return await _db.InventoryItems
            .AsNoTracking()
            .Include(i => i.Category).Include(i => i.Location)
            .FirstOrDefaultAsync(i => i.Barcode == c);
    }

    public async Task<List<InventoryActivity>> GetActivityAsync(int itemId, int take = 50)
        => await _db.InventoryActivities
            .AsNoTracking()
            .Where(a => a.ItemId == itemId)
            .OrderByDescending(a => a.Timestamp).ThenByDescending(a => a.Id)
            .Take(take)
            .ToListAsync();

    private async Task LogAsync(int itemId, string? staffName, string action, decimal? delta, decimal? after, string? note)
    {
        _db.InventoryActivities.Add(new InventoryActivity
        {
            ItemId = itemId,
            StaffName = staffName ?? "",
            Action = action,
            QtyDelta = delta,
            QtyAfter = after,
            Note = note,
            Timestamp = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();
    }

    // --- Staff ---

    public async Task<List<InventoryStaff>> GetStaffAsync(bool activeOnly = true)
    {
        var q = _db.InventoryStaff.AsNoTracking().AsQueryable();
        if (activeOnly) q = q.Where(s => s.IsActive);
        return await q.OrderBy(s => s.SortOrder).ThenBy(s => s.Name).ToListAsync();
    }

    public async Task<InventoryStaff> AddStaffAsync(string name)
    {
        ValidateName(name, "Name");
        name = name.Trim();
        if (await _db.InventoryStaff.AnyAsync(s => s.Name == name))
            throw new InvalidOperationException($"\"{name}\" is already in the staff list.");
        var maxSort = await _db.InventoryStaff.MaxAsync(s => (int?)s.SortOrder) ?? -1;
        var s = new InventoryStaff { Name = name, SortOrder = maxSort + 1, IsActive = true };
        _db.InventoryStaff.Add(s);
        await _db.SaveChangesAsync();
        return s;
    }

    public async Task RenameStaffAsync(int id, string name)
    {
        ValidateName(name, "Name");
        var s = await _db.InventoryStaff.FindAsync(id);
        if (s is null) return;
        s.Name = name.Trim();
        await _db.SaveChangesAsync();
    }

    public async Task SetStaffActiveAsync(int id, bool active)
    {
        var s = await _db.InventoryStaff.FindAsync(id);
        if (s is null) return;
        s.IsActive = active;
        await _db.SaveChangesAsync();
    }

    // --- Locations ---

    public async Task<List<InventoryLocation>> GetLocationsAsync(bool activeOnly = true)
    {
        var q = _db.InventoryLocations.AsNoTracking().AsQueryable();
        if (activeOnly) q = q.Where(l => l.IsActive);
        return await q.OrderBy(l => l.SortOrder).ThenBy(l => l.Name).ToListAsync();
    }

    public async Task<InventoryLocation> AddLocationAsync(string name)
    {
        ValidateName(name, "Location name");
        name = name.Trim();
        if (await _db.InventoryLocations.AnyAsync(l => l.Name == name))
            throw new InvalidOperationException($"A location named \"{name}\" already exists.");
        var maxSort = await _db.InventoryLocations.MaxAsync(l => (int?)l.SortOrder) ?? -1;
        var l = new InventoryLocation { Name = name, SortOrder = maxSort + 1, IsActive = true };
        _db.InventoryLocations.Add(l);
        await _db.SaveChangesAsync();
        return l;
    }

    public async Task RenameLocationAsync(int id, string name)
    {
        ValidateName(name, "Location name");
        var l = await _db.InventoryLocations.FindAsync(id);
        if (l is null) return;
        l.Name = name.Trim();
        await _db.SaveChangesAsync();
    }

    public async Task SetLocationActiveAsync(int id, bool active)
    {
        var l = await _db.InventoryLocations.FindAsync(id);
        if (l is null) return;
        l.IsActive = active;
        await _db.SaveChangesAsync();
    }
}
