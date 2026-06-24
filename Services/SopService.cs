using DecoSOP.Data;
using DecoSOP.Models;
using Microsoft.EntityFrameworkCore;

namespace DecoSOP.Services;

public class WebSopService
{
    private readonly AppDbContext _db;

    public WebSopService(AppDbContext db) => _db = db;

    private static void ValidateName(string? name, string field = "Name")
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException($"{field} is required.");
        if (name.Trim().Length > 200)
            throw new ArgumentException($"{field} must be 200 characters or fewer.");
    }

    // --- Categories ---

    /// <summary>
    /// Returns the full category tree (roots with nested children) with documents at each level.
    /// </summary>
    public async Task<List<Category>> GetCategoryTreeAsync()
    {
        // Load all categories and documents in one query; EF Core fixes up navigation properties
        var all = await _db.Categories
            .Include(c => c.Documents.OrderBy(d => d.SortOrder))
            .OrderBy(c => c.SortOrder)
            .ToListAsync();

        // Return only root categories (ParentId == null); children are already wired via EF fixup
        return all.Where(c => c.ParentId == null).ToList();
    }

    /// <summary>
    /// Returns a flat list of all categories (for dropdowns).
    /// </summary>
    public async Task<List<Category>> GetAllCategoriesAsync()
        => await _db.Categories.OrderBy(c => c.SortOrder).ToListAsync();

    public async Task<Category> CreateCategoryAsync(string name, int? parentId = null)
    {
        ValidateName(name, "Category name");
        var maxSort = await _db.Categories
            .Where(c => c.ParentId == parentId)
            .MaxAsync(c => (int?)c.SortOrder) ?? -1;
        var category = new Category { Name = name.Trim(), SortOrder = maxSort + 1, ParentId = parentId };
        _db.Categories.Add(category);
        await _db.SaveChangesAsync();
        return category;
    }

    public async Task RenameCategoryAsync(int id, string newName)
    {
        ValidateName(newName, "Category name");
        var cat = await _db.Categories.FindAsync(id);
        if (cat is null) return;
        cat.Name = newName.Trim();
        await _db.SaveChangesAsync();
    }

    public async Task DeleteCategoryAsync(int id)
    {
        var cat = await _db.Categories.FindAsync(id);
        if (cat is null) return;
        _db.Categories.Remove(cat);
        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// Builds "Parent > Child > Grandchild" breadcrumb path for a category.
    /// </summary>
    public async Task<string> GetCategoryPathAsync(int categoryId)
    {
        var all = await _db.Categories.AsNoTracking().ToListAsync();
        var parts = new List<string>();
        var current = all.FirstOrDefault(c => c.Id == categoryId);
        while (current is not null)
        {
            parts.Insert(0, current.Name);
            current = current.ParentId.HasValue ? all.FirstOrDefault(c => c.Id == current.ParentId) : null;
        }
        return string.Join(" / ", parts);
    }

    /// <summary>
    /// Returns a single category with its direct children and documents loaded.
    /// </summary>
    public async Task<Category?> GetCategoryWithChildrenAsync(int categoryId)
    {
        // Load all categories + documents so EF fixup wires Children navigations
        var all = await _db.Categories
            .Include(c => c.Documents.OrderBy(d => d.SortOrder))
            .OrderBy(c => c.SortOrder)
            .ToListAsync();

        return all.FirstOrDefault(c => c.Id == categoryId);
    }

    /// <summary>
    /// Returns breadcrumb list of (Id, Name) from root down to the given category.
    /// </summary>
    public async Task<List<(int Id, string Name)>> GetCategoryBreadcrumbsAsync(int categoryId)
    {
        var all = await _db.Categories.AsNoTracking().ToListAsync();
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

    public async Task<SopDocument?> GetDocumentAsync(int id)
        => await _db.Documents
            .Include(d => d.Category)
            .FirstOrDefaultAsync(d => d.Id == id);

    public async Task<SopDocument> CreateDocumentAsync(int categoryId, string title)
    {
        ValidateName(title, "Title");
        var maxSort = await _db.Documents
            .Where(d => d.CategoryId == categoryId)
            .MaxAsync(d => (int?)d.SortOrder) ?? -1;

        var doc = new SopDocument
        {
            CategoryId = categoryId,
            Title = title.Trim(),
            HtmlContent = $"<h1>{System.Net.WebUtility.HtmlEncode(title.Trim())}</h1><p>Start writing your SOP here...</p>",
            SortOrder = maxSort + 1
        };

        _db.Documents.Add(doc);
        await _db.SaveChangesAsync();
        return doc;
    }

    public async Task UpdateDocumentAsync(int id, string title, string htmlContent)
    {
        ValidateName(title, "Title");
        var doc = await _db.Documents.FindAsync(id);
        if (doc is null) return;
        doc.Title = title.Trim();
        doc.HtmlContent = htmlContent;
        doc.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// Returns a title unique within the category, appending " (2)", " (3)", ...
    /// if the desired title is already taken (the table has a unique CategoryId+Title index).
    /// </summary>
    public async Task<string> GetUniqueTitleAsync(int categoryId, string desiredTitle)
    {
        var baseTitle = desiredTitle.Trim();
        if (baseTitle.Length > 200) baseTitle = baseTitle[..200];

        var existing = (await _db.Documents
            .Where(d => d.CategoryId == categoryId)
            .Select(d => d.Title)
            .ToListAsync())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!existing.Contains(baseTitle)) return baseTitle;

        for (int i = 2; i < 1000; i++)
        {
            var candidate = $"{baseTitle} ({i})";
            if (!existing.Contains(candidate)) return candidate;
        }
        return $"{baseTitle} ({Guid.NewGuid():N})";
    }

    public async Task DeleteDocumentAsync(int id)
    {
        var doc = await _db.Documents.FindAsync(id);
        if (doc is null) return;
        _db.Documents.Remove(doc);
        await _db.SaveChangesAsync();
    }

    public async Task<List<SopDocument>> SearchAsync(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];

        var term = query.Trim().ToLower();
        return await _db.Documents
            .Include(d => d.Category)
            .Where(d => d.Title.ToLower().Contains(term)
                     || d.HtmlContent.ToLower().Contains(term))
            .OrderBy(d => d.Category.SortOrder)
            .ThenBy(d => d.SortOrder)
            .ToListAsync();
    }

}
