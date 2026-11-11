using DecoSOP.Data;
using DecoSOP.Models;
using DecoSOP.Services;
using DecoSOP.Services.Users;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DecoSOP.Tests;

public sealed class UserTests : IDisposable
{
    private const string MachineIp = "10.0.0.7";
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"decosop-users-{Guid.NewGuid():N}.db");
    private readonly Factory _factory;

    public UserTests()
    {
        _factory = new Factory(_dbPath);
        using var db = _factory.CreateDbContext();
        SchemaUpgrader.EnsureTablesAsync(db).GetAwaiter().GetResult();
    }

    [Fact]
    public async Task UpgradeAddsNewTablesToAnOlderDatabaseAndKeepsItsData()
    {
        await using (var db = _factory.CreateDbContext())
        {
            db.UserPreferences.Add(new UserPreference { ClientId = MachineIp, EntityType = ListEntity.SopFile, EntityId = 1, IsFavorited = true });
            await db.SaveChangesAsync();
            // What a database from before users and lists looks like.
            await db.Database.ExecuteSqlRawAsync("DROP TABLE UserListItems; DROP TABLE UserLists; DROP TABLE AppUsers;");
        }

        await using (var db = _factory.CreateDbContext())
        {
            await SchemaUpgrader.EnsureTablesAsync(db);
            await SchemaUpgrader.EnsureTablesAsync(db);   // and running it again is harmless
            db.AppUsers.Add(new AppUser { Name = "Pat" });
            await db.SaveChangesAsync();
            Assert.Equal(1, await db.UserPreferences.CountAsync());
            Assert.Equal(1, await db.AppUsers.CountAsync());
        }
    }

    [Fact]
    public async Task ANewUserStartsWithThisPcsSetup()
    {
        await using (var db = _factory.CreateDbContext())
        {
            db.UserPreferences.Add(new UserPreference { ClientId = MachineIp, EntityType = ListEntity.SopFile, EntityId = 4, IsFavorited = true, Color = "blue" });
            db.UserLists.Add(new UserList
            {
                OwnerKey = MachineIp, Name = UserListService.ReviewName, IsBuiltIn = true,
                Items = [new UserListItem { EntityType = ListEntity.OnlinePage, EntityId = 9 }]
            });
            await db.SaveChangesAsync();
        }

        var user = await Users().AddAsync("  Jennifer   D ");
        Assert.Equal("Jennifer D", user.Name);

        await using (var db = _factory.CreateDbContext())
        {
            var key = ClientIdentityService.UserKey(user.Id);
            var pref = await db.UserPreferences.SingleAsync(p => p.ClientId == key);
            Assert.True(pref.IsFavorited);
            Assert.Equal("blue", pref.Color);
            var list = await db.UserLists.Include(l => l.Items).SingleAsync(l => l.OwnerKey == key);
            Assert.Equal(9, Assert.Single(list.Items).EntityId);
            // The PC keeps its own copy.
            Assert.Equal(1, await db.UserPreferences.CountAsync(p => p.ClientId == MachineIp));
        }
    }

    [Fact]
    public async Task NamesAreUniqueIgnoringCase()
    {
        await Users().AddAsync("Pat");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Users().AddAsync("PAT"));
        await Assert.ThrowsAsync<ArgumentException>(() => Users().AddAsync("   "));
    }

    [Fact]
    public async Task RemovingAUserRemovesTheirSetupOnly()
    {
        var pat = await Users().AddAsync("Pat");
        var key = ClientIdentityService.UserKey(pat.Id);
        await using (var db = _factory.CreateDbContext())
        {
            db.UserPreferences.Add(new UserPreference { ClientId = key, EntityType = ListEntity.SopFile, EntityId = 1, IsFavorited = true });
            db.UserPreferences.Add(new UserPreference { ClientId = MachineIp, EntityType = ListEntity.SopFile, EntityId = 1, IsFavorited = true });
            db.UserLists.Add(new UserList { OwnerKey = key, Name = "Training", Items = [new UserListItem { EntityType = ListEntity.SopFile, EntityId = 1 }] });
            await db.SaveChangesAsync();
        }

        await Users().DeleteAsync(pat.Id);

        await using (var db = _factory.CreateDbContext())
        {
            Assert.False(await db.AppUsers.AnyAsync());
            Assert.False(await db.UserLists.AnyAsync());
            Assert.False(await db.UserListItems.AnyAsync());
            Assert.Equal(MachineIp, (await db.UserPreferences.SingleAsync()).ClientId);
        }
    }

    [Fact]
    public void TheChosenUserIsTheKeyWhenSetOtherwiseThePc()
    {
        Assert.Equal(MachineIp, Identity(null).ClientId);
        var chosen = Identity("12");
        Assert.Equal("user:12", chosen.ClientId);
        Assert.Equal(MachineIp, chosen.MachineId);
        Assert.Equal(MachineIp, Identity("not-a-number").ClientId);
    }

    private AppUserService Users() => new(_factory, Identity(null));

    private static ClientIdentityService Identity(string? userCookie)
    {
        var http = new DefaultHttpContext();
        http.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(MachineIp);
        if (userCookie is not null) http.Request.Headers.Cookie = $"{ClientIdentityService.UserCookie}={userCookie}";
        return new ClientIdentityService(new HttpContextAccessor { HttpContext = http });
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch (IOException) { }
    }

    private sealed class Factory(string path) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext()
            => new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={path}").Options);
    }
}
