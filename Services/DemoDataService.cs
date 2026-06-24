using DecoSOP.Data;
using DecoSOP.Models;
using Microsoft.EntityFrameworkCore;

namespace DecoSOP.Services;

public static class DemoDataService
{
    /// <summary>
    /// Seeds a few empty placeholder categories for the SOP and Document modules.
    /// Returns true if data was seeded, false if skipped (data already exists).
    /// </summary>
    public static async Task<bool> SeedDemoDataAsync(AppDbContext db)
    {
        if (await db.SopCategories.AnyAsync() || await db.DocumentCategories.AnyAsync())
            return false;

        db.SopCategories.AddRange(
            new SopCategory { Name = "Procedures", SortOrder = 0 },
            new SopCategory { Name = "Forms & Templates", SortOrder = 1 },
            new SopCategory { Name = "Compliance", SortOrder = 2 });

        db.DocumentCategories.AddRange(
            new DocumentCategory { Name = "Forms", SortOrder = 0 },
            new DocumentCategory { Name = "Templates", SortOrder = 1 },
            new DocumentCategory { Name = "References", SortOrder = 2 });

        await db.SaveChangesAsync();
        return true;
    }
}
