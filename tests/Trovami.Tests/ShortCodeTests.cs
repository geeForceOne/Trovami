using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Trovami.Core;
using Trovami.Core.Models;
using Trovami.Core.Services;

namespace Trovami.Tests;

public class ShortCodeTests
{
    [Fact]
    public async Task NewLocationsAndItemsGetACodeTheyCanBeFoundBy()
    {
        using var t = new TestDb();
        var anna = await t.AddUserAsync("anna");
        var home = await t.Households.CreateAsync(anna, "Home");
        var house = await t.Locations.CreateAsync(anna, home, null, "House", LocationKind.House);
        var lamp = Guid.NewGuid();
        await t.Items.CreateAsync(anna, home, new ItemCreate(lamp, house, "Lamp", null));

        var node = (await t.Locations.TreeAsync(anna, home)).Find(house)!;
        var item = await t.Items.GetAsync(anna, lamp);
        Assert.NotNull(ShortCodes.Normalize(node.ShortCode));
        Assert.NotNull(ShortCodes.Normalize(item.ShortCode));

        var tree = await t.Locations.TreeAsync(anna, home);
        Assert.Equal(house, tree.FindByCode(node.ShortCode.ToLowerInvariant())?.Id);
        Assert.Equal(lamp, (await t.Items.GetByCodeAsync(anna, item.ShortCode.ToLowerInvariant())).Id);
    }

    [Fact]
    public async Task OnlyMembersFindAnItemByItsCode()
    {
        using var t = new TestDb();
        var anna = await t.AddUserAsync("anna");
        var ben = await t.AddUserAsync("ben");
        var home = await t.Households.CreateAsync(anna, "Home");
        var lamp = Guid.NewGuid();
        await t.Items.CreateAsync(anna, home, new ItemCreate(lamp, null, "Lamp", null));
        var code = (await t.Items.GetAsync(anna, lamp)).ShortCode;

        await Assert.ThrowsAsync<NotFoundException>(() => t.Items.GetByCodeAsync(ben, code));
        await Assert.ThrowsAsync<NotFoundException>(() => t.Items.GetByCodeAsync(anna, "NOPE22"));
    }

    [Theory]
    [InlineData("K7F3QZ", "K7F3QZ")]
    [InlineData("k7f3qz", "K7F3QZ")]
    [InlineData("K7F3Q", null)]
    [InlineData("K7F3QZZ", null)]
    [InlineData("K7F3Q0", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void OnlyWellFormedCodesAreAccepted(string? input, string? expected) =>
        Assert.Equal(expected, ShortCodes.Normalize(input));

    [Fact]
    public async Task BelowListsEverythingUnderALocation()
    {
        using var t = new TestDb();
        var anna = await t.AddUserAsync("anna");
        var home = await t.Households.CreateAsync(anna, "Home");
        var house = await t.Locations.CreateAsync(anna, home, null, "House", LocationKind.House);
        var cellar = await t.Locations.CreateAsync(anna, home, house, "Cellar", LocationKind.Cellar);
        var box = await t.Locations.CreateAsync(anna, home, cellar, "Box", LocationKind.Box);
        var kitchen = await t.Locations.CreateAsync(anna, home, house, "Kitchen", LocationKind.Room);
        await t.Items.CreateAsync(anna, home, new ItemCreate(Guid.NewGuid(), cellar, "Wine", null));
        await t.Items.CreateAsync(anna, home, new ItemCreate(Guid.NewGuid(), box, "Lights", null));
        await t.Items.CreateAsync(anna, home, new ItemCreate(Guid.NewGuid(), kitchen, "Spoon", null));
        await t.Items.CreateAsync(anna, home, new ItemCreate(Guid.NewGuid(), null, "Inbox thing", null));

        var below = await t.Items.BelowAsync(anna, home, cellar);
        Assert.Equal(["Lights", "Wine"], below.Select(i => i.ShownName).Order());
        Assert.Equal(3, (await t.Items.BelowAsync(anna, home, house)).Count);
    }

    // The migration gives every existing location and item its own code.
    [Fact]
    public void UpdatingAnExistingDatabaseFillsInCodes()
    {
        var dir = Path.Combine(Path.GetTempPath(), "trovami-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var factory = new TestDb.Factory(Path.Combine(dir, "trovami.db"));
            using (var db = factory.CreateDbContext())
            {
                var migrator = db.GetService<IMigrator>();
                migrator.Migrate("20261029214751_Initial");
                var household = Guid.NewGuid();
                db.Database.ExecuteSqlRaw("PRAGMA foreign_keys = OFF;");
                db.Database.ExecuteSql($"INSERT INTO Households (Id, Name, OwnerId, CreatedAt, AiProvider) VALUES ({household}, 'Home', 'x', '2026-01-01', 'None')");
                for (var i = 0; i < 300; i++)
                {
                    db.Database.ExecuteSql($"INSERT INTO Locations (Id, HouseholdId, Name, Kind, CreatedAt, Version, UpdatedAt) VALUES ({Guid.NewGuid()}, {household}, {"L" + i}, 'Box', '2026-01-01', 0, '2026-01-01')");
                    db.Database.ExecuteSql($"INSERT INTO Items (Id, HouseholdId, NameSource, AiNaming, Quantity, PhotoExpected, CreatedAt, UpdatedAt) VALUES ({Guid.NewGuid()}, {household}, 'None', 'NotNeeded', 1, 0, '2026-01-01', '2026-01-01')");
                }
                migrator.Migrate();
            }
            using (var db = factory.CreateDbContext())
            {
                foreach (var codes in new[] { db.Locations.Select(l => l.ShortCode).ToList(), db.Items.Select(i => i.ShortCode).ToList() })
                {
                    Assert.Equal(300, codes.Count);
                    Assert.All(codes, c => Assert.Equal(c, ShortCodes.Normalize(c)));
                    Assert.Equal(300, codes.Distinct().Count());
                }
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}
