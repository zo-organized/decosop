using DecoSOP.Data;
using DecoSOP.Models;
using Microsoft.EntityFrameworkCore;

namespace DecoSOP.Services;

/// <summary>
/// Central service for per-user preferences (favorites, pins, folder colors).
/// Replaces the scattered favorite/pin/color methods on domain services.
/// </summary>
public class UserPreferenceService
{
    private readonly AppDbContext _db;
    private readonly ClientIdentityService _identity;

    public UserPreferenceService(AppDbContext db, ClientIdentityService identity)
    {
        _db = db;
        _identity = identity;
    }

    private string ClientId => _identity.ClientId;

    /// <summary>
    /// Returns all preferences for a given entity type for the current client.
    /// Used to overlay preferences onto entity lists in a single query.
    /// </summary>
    public async Task<Dictionary<int, UserPreference>> GetAllForTypeAsync(string entityType)
    {
        return await _db.UserPreferences
            .Where(p => p.ClientId == ClientId && p.EntityType == entityType)
            .ToDictionaryAsync(p => p.EntityId);
    }

    /// <summary>
    /// Returns all favorited entity IDs for a given type.
    /// </summary>
    public async Task<List<int>> GetFavoritedIdsAsync(string entityType)
    {
        return await _db.UserPreferences
            .Where(p => p.ClientId == ClientId
                && p.EntityType == entityType
                && p.IsFavorited)
            .Select(p => p.EntityId)
            .ToListAsync();
    }

    public async Task<bool> ToggleFavoriteAsync(string entityType, int entityId)
    {
        var pref = await GetOrCreateAsync(entityType, entityId);
        pref.IsFavorited = !pref.IsFavorited;
        await _db.SaveChangesAsync();
        return pref.IsFavorited;
    }

    public async Task<bool> TogglePinAsync(string entityType, int entityId)
    {
        var pref = await GetOrCreateAsync(entityType, entityId);
        pref.IsPinned = !pref.IsPinned;
        await _db.SaveChangesAsync();
        return pref.IsPinned;
    }

    public async Task SetColorAsync(string entityType, int entityId, string? color)
    {
        var pref = await GetOrCreateAsync(entityType, entityId);
        pref.Color = color;
        await _db.SaveChangesAsync();
    }

    private async Task<UserPreference> GetOrCreateAsync(string entityType, int entityId)
    {
        var pref = await _db.UserPreferences
            .FirstOrDefaultAsync(p => p.ClientId == ClientId
                && p.EntityType == entityType
                && p.EntityId == entityId);

        if (pref is null)
        {
            pref = new UserPreference
            {
                ClientId = ClientId,
                EntityType = entityType,
                EntityId = entityId
            };
            _db.UserPreferences.Add(pref);
        }

        return pref;
    }
}
