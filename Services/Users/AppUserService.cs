using DecoSOP.Data;
using DecoSOP.Models;
using Microsoft.EntityFrameworkCore;

namespace DecoSOP.Services.Users;

/// <summary>The office's list of people (see <see cref="AppUser"/>): add, rename, remove.</summary>
public class AppUserService(IDbContextFactory<AppDbContext> dbFactory, ClientIdentityService identity)
{
    public const int MaxNameLength = 60;

    public async Task<List<AppUser>> GetAllAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.AppUsers.AsNoTracking().OrderBy(u => u.Name).ToListAsync();
    }

    /// <summary>The user this browser is browsing as, or null (none chosen, or since removed).</summary>
    public async Task<AppUser?> GetCurrentAsync()
    {
        if (identity.UserId is not int id) return null;
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.AppUsers.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id);
    }

    /// <summary>
    /// Adds a person. They start with whatever this PC had set up (favorites, pins, colours,
    /// lists) — so the first person to add their name on their own PC keeps everything they had.
    /// </summary>
    public async Task<AppUser> AddAsync(string name)
    {
        name = CleanName(name);
        await using var db = await dbFactory.CreateDbContextAsync();
        if (await db.AppUsers.AnyAsync(u => u.Name == name))
            throw new InvalidOperationException($"There is already someone called \"{name}\".");

        var user = new AppUser { Name = name, CreatedAt = DateTime.UtcNow };
        db.AppUsers.Add(user);
        await db.SaveChangesAsync();

        await CopySetupAsync(db, identity.MachineId, ClientIdentityService.UserKey(user.Id));
        return user;
    }

    public async Task RenameAsync(int id, string name)
    {
        name = CleanName(name);
        await using var db = await dbFactory.CreateDbContextAsync();
        var user = await db.AppUsers.FindAsync(id) ?? throw new InvalidOperationException("That person no longer exists.");
        if (await db.AppUsers.AnyAsync(u => u.Id != id && u.Name == name))
            throw new InvalidOperationException($"There is already someone called \"{name}\".");
        user.Name = name;
        await db.SaveChangesAsync();
    }

    /// <summary>Removes a person together with their favorites, pins, colours and lists.</summary>
    public async Task DeleteAsync(int id)
    {
        var key = ClientIdentityService.UserKey(id);
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.UserLists.Where(l => l.OwnerKey == key).ExecuteDeleteAsync();   // items cascade
        await db.UserPreferences.Where(p => p.ClientId == key).ExecuteDeleteAsync();
        await db.AppUsers.Where(u => u.Id == id).ExecuteDeleteAsync();
    }

    private static string CleanName(string? name)
    {
        name = string.Join(' ', (name ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (name.Length == 0) throw new ArgumentException("Enter a name.");
        if (name.Length > MaxNameLength) throw new ArgumentException($"Names can be at most {MaxNameLength} characters.");
        return name;
    }

    private static async Task CopySetupAsync(AppDbContext db, string fromKey, string toKey)
    {
        var prefs = await db.UserPreferences.AsNoTracking().Where(p => p.ClientId == fromKey).ToListAsync();
        db.UserPreferences.AddRange(prefs.Select(p => new UserPreference
        {
            ClientId = toKey, EntityType = p.EntityType, EntityId = p.EntityId,
            IsFavorited = p.IsFavorited, IsPinned = p.IsPinned, Color = p.Color
        }));

        var lists = await db.UserLists.AsNoTracking().Include(l => l.Items).Where(l => l.OwnerKey == fromKey).ToListAsync();
        db.UserLists.AddRange(lists.Select(l => new UserList
        {
            OwnerKey = toKey, Name = l.Name, IsBuiltIn = l.IsBuiltIn, SortOrder = l.SortOrder, CreatedAt = l.CreatedAt,
            Items = l.Items.Select(i => new UserListItem { EntityType = i.EntityType, EntityId = i.EntityId, AddedAt = i.AddedAt }).ToList()
        }));

        await db.SaveChangesAsync();
    }
}
