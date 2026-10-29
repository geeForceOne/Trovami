using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Trovami.Core.Ai;
using Trovami.Core.Backup;
using Trovami.Core.Data;
using Trovami.Core.Models;
using Trovami.Core.Photos;
using Trovami.Core.Services;

namespace Trovami.Tests;

// A real SQLite database in a temporary folder, with the services wired up as the app does.
public sealed class TestDb : IDisposable
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "trovami-tests-" + Guid.NewGuid().ToString("N"));
    public StoragePaths Paths { get; }
    public Factory Db { get; }
    public PhotoStore Photos { get; }
    public AiNamingQueue Queue { get; } = new();
    public HouseholdService Households { get; }
    public LocationService Locations { get; }
    public ItemService Items { get; }
    public TagService Tags { get; }
    public SyncService Sync { get; }
    public AiNamingService Naming { get; }

    public TestDb()
    {
        Paths = StoragePaths.From(Dir, null, null, null, null, Dir);
        Paths.CreateDirectories();
        Db = new Factory(Paths.DatabasePath);
        using (var db = Db.CreateDbContext())
            db.Database.Migrate();
        Photos = new PhotoStore(Paths);
        Households = new HouseholdService(Db, Photos);
        Locations = new LocationService(Db, Photos);
        Items = new ItemService(Db, Photos, Queue);
        Tags = new TagService(Db);
        Sync = new SyncService(Db, Locations, Tags, Items);
        Naming = new AiNamingService(Db, Photos, Queue, new EphemeralDataProtectionProvider(), NullLogger<AiNamingService>.Instance);
    }

    public async Task<string> AddUserAsync(string name)
    {
        await using var db = Db.CreateDbContext();
        var user = new AppUser { UserName = name + "@example.com", Email = name + "@example.com", DisplayName = name };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(Dir, recursive: true); } catch (IOException) { }
    }

    public sealed class Factory(string path) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={path}").Options);
    }
}
