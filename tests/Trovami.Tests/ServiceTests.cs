using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Trovami.Core;
using Trovami.Core.Ai;
using Trovami.Core.Models;
using Trovami.Core.Services;

namespace Trovami.Tests;

public class ServiceTests
{
    [Fact]
    public async Task OnlyMembersSeeAHousehold()
    {
        using var t = new TestDb();
        var anna = await t.AddUserAsync("anna");
        var ben = await t.AddUserAsync("ben");
        var home = await t.Households.CreateAsync(anna, "Home");

        await Assert.ThrowsAsync<NotFoundException>(() => t.Locations.TreeAsync(ben, home));
        Assert.True(await t.Households.InviteAsync(anna, home, ben));
        await Assert.ThrowsAsync<NotFoundException>(() => t.Locations.TreeAsync(ben, home));

        var invite = Assert.Single(await t.Households.InvitesForAsync(ben));
        await t.Households.AcceptAsync(ben, invite.Id);
        Assert.Equal(0, (await t.Locations.TreeAsync(ben, home)).Count);
    }

    [Fact]
    public async Task ALocationCantMoveIntoItself()
    {
        using var t = new TestDb();
        var anna = await t.AddUserAsync("anna");
        var home = await t.Households.CreateAsync(anna, "Home");
        var house = await t.Locations.CreateAsync(anna, home, null, "House", LocationKind.House);
        var room = await t.Locations.CreateAsync(anna, home, house, "Kitchen", LocationKind.Room);

        await Assert.ThrowsAsync<InputException>(() => t.Locations.MoveAsync(anna, house, room));
        await Assert.ThrowsAsync<InputException>(() => t.Locations.MoveAsync(anna, house, house));
    }

    [Fact]
    public async Task DeletingMovesContentsUp()
    {
        using var t = new TestDb();
        var anna = await t.AddUserAsync("anna");
        var home = await t.Households.CreateAsync(anna, "Home");
        var house = await t.Locations.CreateAsync(anna, home, null, "House", LocationKind.House);
        var room = await t.Locations.CreateAsync(anna, home, house, "Kitchen", LocationKind.Room);
        var drawer = await t.Locations.CreateAsync(anna, home, room, "Drawer", LocationKind.Drawer);
        var spoon = Guid.NewGuid();
        await t.Items.CreateAsync(anna, home, new ItemCreate(spoon, room, "Spoon", null));

        await Assert.ThrowsAsync<InputException>(() => t.Locations.DeleteAsync(anna, room, moveContents: false));
        await t.Locations.DeleteAsync(anna, room, moveContents: true);

        var tree = await t.Locations.TreeAsync(anna, home);
        Assert.Equal(house, tree.Find(drawer)!.ParentId);
        Assert.Equal(house, (await t.Items.GetAsync(anna, spoon)).LocationId);
    }

    [Fact]
    public async Task CreatingAnItemTwiceIsHarmless()
    {
        using var t = new TestDb();
        var anna = await t.AddUserAsync("anna");
        var home = await t.Households.CreateAsync(anna, "Home");
        var id = Guid.NewGuid();

        Assert.True(await t.Items.CreateAsync(anna, home, new ItemCreate(id, null, "Drill", null)));
        Assert.False(await t.Items.CreateAsync(anna, home, new ItemCreate(id, null, "Drill", null)));
        Assert.Single(await t.Items.InboxAsync(anna, home));
    }

    [Fact]
    public async Task SearchFindsItemsByTheirPath()
    {
        using var t = new TestDb();
        var anna = await t.AddUserAsync("anna");
        var home = await t.Households.CreateAsync(anna, "Home");
        var cellar = await t.Locations.CreateAsync(anna, home, null, "Cellar", LocationKind.Cellar);
        var attic = await t.Locations.CreateAsync(anna, home, null, "Attic", LocationKind.Attic);
        await t.Items.CreateAsync(anna, home, new ItemCreate(Guid.NewGuid(), cellar, "Drill", null));
        await t.Items.CreateAsync(anna, home, new ItemCreate(Guid.NewGuid(), attic, "Drill bits", null));

        var result = await t.Items.SearchAsync(anna, home, "cellar drill");
        var hit = Assert.Single(result.Items);
        Assert.Equal("Drill", hit.Name);
        Assert.Equal("Cellar", hit.Path);
    }

    [Fact]
    public async Task SyncSendsOnlyWhatChangedAndDeletions()
    {
        using var t = new TestDb();
        var anna = await t.AddUserAsync("anna");
        var home = await t.Households.CreateAsync(anna, "Home");
        var house = await t.Locations.CreateAsync(anna, home, null, "House", LocationKind.House);

        var first = await t.Sync.PullAsync(anna, 0, []);
        Assert.Single(first.Locations);
        Assert.Single(first.Households);

        var box = await t.Locations.CreateAsync(anna, home, house, "Box", LocationKind.Box);
        await t.Tags.LinkAsync(anna, "ABCDEFGH", box, TagKind.Nfc);
        var second = await t.Sync.PullAsync(anna, first.Cursor, [home]);
        Assert.Equal(box, Assert.Single(second.Locations).Id);
        Assert.Equal("ABCDEFGH", Assert.Single(second.Tags).Code);

        await t.Locations.DeleteAsync(anna, box, moveContents: true);
        var third = await t.Sync.PullAsync(anna, second.Cursor, [home]);
        Assert.True(Assert.Single(third.Locations).Deleted);
        Assert.True(Assert.Single(third.Tags).Deleted);
    }

    [Fact]
    public async Task ANewlyJoinedHouseholdComesInFull()
    {
        using var t = new TestDb();
        var anna = await t.AddUserAsync("anna");
        var ben = await t.AddUserAsync("ben");
        var home = await t.Households.CreateAsync(anna, "Home");
        await t.Locations.CreateAsync(anna, home, null, "House", LocationKind.House);
        var benHome = await t.Households.CreateAsync(ben, "Second home");
        var cursor = (await t.Sync.PullAsync(ben, 0, [])).Cursor;

        await t.Households.InviteAsync(anna, home, ben);
        await t.Households.AcceptAsync(ben, (await t.Households.InvitesForAsync(ben))[0].Id);

        var pull = await t.Sync.PullAsync(ben, cursor, [benHome]);
        Assert.Equal(2, pull.Households.Count);
        Assert.Equal("House", Assert.Single(pull.Locations).Name);
    }

    [Fact]
    public async Task PushedItemsLandInTheInboxWhenTheirLocationIsGone()
    {
        using var t = new TestDb();
        var anna = await t.AddUserAsync("anna");
        var home = await t.Households.CreateAsync(anna, "Home");
        var id = Guid.NewGuid();

        var result = await t.Sync.PushAsync(anna, new SyncPush(null, null, [new PushItem(id, home, Guid.NewGuid(), null, null, 1, false)]));
        Assert.True(Assert.Single(result.Items).Ok);
        Assert.Null((await t.Items.GetAsync(anna, id)).LocationId);
    }

    [Fact]
    public async Task ATagOfAnotherHouseholdIsRefused()
    {
        using var t = new TestDb();
        var anna = await t.AddUserAsync("anna");
        var home = await t.Households.CreateAsync(anna, "Home");
        var flat = await t.Households.CreateAsync(anna, "Flat");
        var a = await t.Locations.CreateAsync(anna, home, null, "A", LocationKind.Box);
        var b = await t.Locations.CreateAsync(anna, flat, null, "B", LocationKind.Box);

        await t.Tags.LinkAsync(anna, "TAG12345", a, TagKind.Nfc);
        await Assert.ThrowsAsync<InputException>(() => t.Tags.LinkAsync(anna, "TAG12345", b, TagKind.Nfc));
    }

    [Fact]
    public async Task PrintingAgainReusesTheQrCode()
    {
        using var t = new TestDb();
        var anna = await t.AddUserAsync("anna");
        var home = await t.Households.CreateAsync(anna, "Home");
        var box = await t.Locations.CreateAsync(anna, home, null, "Box", LocationKind.Box);

        var first = Assert.Single(await t.Tags.LabelsForPrintAsync(anna, home, [box]));
        var second = Assert.Single(await t.Tags.LabelsForPrintAsync(anna, home, [box]));
        Assert.Equal(first.Code, second.Code);
        Assert.Equal(TagService.UriPrefix + first.Code, first.Uri);
        Assert.Equal(first.Code, TagService.CodeFromUri(first.Uri.ToLowerInvariant()));
    }

    [Fact]
    public async Task TheAiNeverOverwritesANameTypedByAPerson()
    {
        using var t = new TestDb();
        var anna = await t.AddUserAsync("anna");
        var home = await t.Households.CreateAsync(anna, "Home");
        t.Naming.NamerFactory = (_, _, _) => new FixedNamer("Cordless drill");
        await t.Naming.SaveSettingsAsync(anna, home, AiProvider.Claude, null, "sk-test");

        var named = Guid.NewGuid();
        var typed = Guid.NewGuid();
        await t.Items.CreateAsync(anna, home, new ItemCreate(named, null, null, null, PhotoExpected: true));
        await t.Items.CreateAsync(anna, home, new ItemCreate(typed, null, "My drill", null, PhotoExpected: true));
        await t.Items.SetPhotoAsync(anna, named, new MemoryStream(PhotoTests.Jpeg(40, 30)));
        await t.Items.SetPhotoAsync(anna, typed, new MemoryStream(PhotoTests.Jpeg(40, 30)));

        await t.Naming.NameAsync(named, default);
        await t.Naming.NameAsync(typed, default);

        var a = await t.Items.GetAsync(anna, named);
        Assert.Equal("Cordless drill", a.Name);
        Assert.Equal(NameSource.Ai, a.NameSource);
        Assert.Equal("My drill", (await t.Items.GetAsync(anna, typed)).Name);
    }

    [Fact]
    public async Task WithoutAKeyTheItemStaysUnnamed()
    {
        using var t = new TestDb();
        var anna = await t.AddUserAsync("anna");
        var home = await t.Households.CreateAsync(anna, "Home");
        var id = Guid.NewGuid();
        await t.Items.CreateAsync(anna, home, new ItemCreate(id, null, null, null, PhotoExpected: true));
        await t.Items.SetPhotoAsync(anna, id, new MemoryStream(PhotoTests.Jpeg(40, 30)));

        await t.Naming.NameAsync(id, default);
        var item = await t.Items.GetAsync(anna, id);
        Assert.Null(item.Name);
        Assert.Equal(AiNaming.Failed, item.AiNaming);
        Assert.Equal(ItemView.UnnamedText, item.ShownName);
    }

    [Fact]
    public async Task DeletingAnOwnerHandsTheHouseholdOver()
    {
        using var t = new TestDb();
        var anna = await t.AddUserAsync("anna");
        var ben = await t.AddUserAsync("ben");
        var shared = await t.Households.CreateAsync(anna, "Shared");
        await t.Households.CreateAsync(anna, "Alone");
        await t.Households.InviteAsync(anna, shared, ben);
        await t.Households.AcceptAsync(ben, (await t.Households.InvitesForAsync(ben))[0].Id);

        Assert.Equal((1, 1), await t.Households.OwnedSummaryAsync(anna));
        Assert.Equal(1, await t.Households.HandOverOrDeleteOwnedAsync(anna));
        Assert.True(Assert.Single(await t.Households.ListAsync(ben)).IsOwner);
    }

    [Fact]
    public async Task TheAiAddsKeywordsThatFindSearchesButNeverOverwritesAPersons()
    {
        using var t = new TestDb();
        var anna = await t.AddUserAsync("anna");
        var home = await t.Households.CreateAsync(anna, "Home");
        t.Naming.NamerFactory = (_, _, _) => new FixedNamer("Cordless drill", "blue", "power tool");
        await t.Naming.SaveSettingsAsync(anna, home, AiProvider.Claude, null, "sk-test");

        var typed = Guid.NewGuid();
        var edited = Guid.NewGuid();
        await t.Items.CreateAsync(anna, home, new ItemCreate(typed, null, "My drill", null, PhotoExpected: true));
        await t.Items.CreateAsync(anna, home, new ItemCreate(edited, null, null, null, PhotoExpected: true));
        await t.Items.SetPhotoAsync(anna, typed, new MemoryStream(PhotoTests.Jpeg(40, 30)));
        await t.Items.SetPhotoAsync(anna, edited, new MemoryStream(PhotoTests.Jpeg(40, 30)));
        await t.Items.UpdateAsync(anna, edited, null, null, 1, ["Red ", "red", "garden"]);

        await t.Naming.NameAsync(typed, default);
        await t.Naming.NameAsync(edited, default);

        // A typed name keeps its name but still gets keywords from the photo.
        var a = await t.Items.GetAsync(anna, typed);
        Assert.Equal("My drill", a.Name);
        Assert.Equal(["blue", "power tool"], Described(a));
        Assert.Equal(AiNaming.NotNeeded, a.AiNaming);
        // A person set keywords first: the AI still names it, but leaves the keywords alone.
        var b = await t.Items.GetAsync(anna, edited);
        Assert.Equal("Cordless drill", b.Name);
        Assert.Equal(["red", "garden"], Described(b));

        var found = await t.Items.SearchAsync(anna, home, "power tool");
        Assert.Equal(typed, Assert.Single(found.Items).Id);
    }

    [Fact]
    public async Task KeywordsAreCountedRenamedMergedAndRemovedAcrossItems()
    {
        using var t = new TestDb();
        var anna = await t.AddUserAsync("anna");
        var home = await t.Households.CreateAsync(anna, "Home");
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        await t.Items.CreateAsync(anna, home, new ItemCreate(a, null, "Drill", null));
        await t.Items.CreateAsync(anna, home, new ItemCreate(b, null, "Hammer", null));
        await t.Items.UpdateAsync(anna, a, "Drill", null, 1, ["tools", "blue", "metallic"]);
        await t.Items.UpdateAsync(anna, b, "Hammer", null, 1, ["tool", "metal"]);

        Assert.Equal([new("blue", 1), new("metal", 1), new("metallic", 1), new("tool", 1), new("tools", 1)],
            await t.Items.KeywordsAsync(anna, home));
        // "metal" doesn't match "metallic".
        Assert.Equal(b, Assert.Single(await t.Items.WithKeywordAsync(anna, home, "metal")).Id);

        Assert.Equal(1, await t.Items.ReplaceKeywordAsync(anna, home, "tools", "Tool"));
        Assert.Equal(1, await t.Items.ReplaceKeywordAsync(anna, home, "metallic", "metal"));
        Assert.Equal(1, await t.Items.ReplaceKeywordAsync(anna, home, "blue", null));

        Assert.Equal([new("metal", 2), new("tool", 2)], await t.Items.KeywordsAsync(anna, home));
        Assert.Equal(["tool", "metal"], Described(await t.Items.GetAsync(anna, a)));
    }

    [Fact]
    public async Task SavingOnlyKeywordsLeavesTheRestAloneAndStopsTheAi()
    {
        using var t = new TestDb();
        var anna = await t.AddUserAsync("anna");
        var home = await t.Households.CreateAsync(anna, "Home");
        var item = Guid.NewGuid();
        await t.Items.CreateAsync(anna, home, new ItemCreate(item, null, null, "In the blue bag", 3));

        await t.Items.SetKeywordsAsync(anna, item, ["Red ", "red", "garden"]);

        var view = await t.Items.GetAsync(anna, item);
        Assert.Equal(["red", "garden"], Described(view));
        Assert.Null(view.Name);
        Assert.NotEqual(NameSource.Person, view.NameSource);
        Assert.Equal("In the blue bag", view.Notes);
        Assert.Equal(3, view.Quantity);

        await t.Items.SetKeywordsAsync(anna, item, []);
        Assert.Empty(Described(await t.Items.GetAsync(anna, item)));

        var cellar = await t.Locations.CreateAsync(anna, home, null, "Cellar", LocationKind.Cellar);
        await t.Locations.SetKeywordsAsync(anna, cellar, ["Damp", "cold"]);
        var node = (await t.Locations.TreeAsync(anna, home)).Find(cellar)!;
        Assert.Equal(["damp", "cold"], node.Keywords);
        Assert.Equal("Cellar", node.Name);
    }

    [Fact]
    public async Task TheAiIsNotShownTheUnsureKeywordAsOneToReuse()
    {
        using var t = new TestDb();
        var anna = await t.AddUserAsync("anna");
        var home = await t.Households.CreateAsync(anna, "Home");
        var seen = new List<IReadOnlyList<string>>();
        t.Naming.NamerFactory = (_, _, _) => new RecordingNamer(seen);
        await t.Naming.SaveSettingsAsync(anna, home, AiProvider.Claude, null, "sk-test");

        var earlier = Guid.NewGuid();
        await t.Items.CreateAsync(anna, home, new ItemCreate(earlier, null, "Odd thing", null));
        await t.Items.SetKeywordsAsync(anna, earlier, [AiNamer.UnsureKeyword, "brass"]);

        var item = Guid.NewGuid();
        await t.Items.CreateAsync(anna, home, new ItemCreate(item, null, null, null, PhotoExpected: true));
        await t.Items.SetPhotoAsync(anna, item, new MemoryStream(PhotoTests.Jpeg(40, 30)));
        await t.Naming.NameAsync(item, CancellationToken.None);

        var known = Assert.Single(seen);
        Assert.Contains("brass", known);
        Assert.DoesNotContain(AiNamer.UnsureKeyword, known);
    }

    private sealed class RecordingNamer(List<IReadOnlyList<string>> seen) : IAiNamer
    {
        public Task<AiDescription> DescribeAsync(byte[] jpeg, IReadOnlyList<string> known, CancellationToken ct)
        {
            seen.Add(known);
            return Task.FromResult(new AiDescription("Thing", ["metal"]));
        }
    }

    [Fact]
    public void TheAiReplyGivesANameAndKeywords()
    {
        var both = AiNamer.ParseReply("**Cordless drill.**\nKeywords: Blue, plastic , power tool, blue\n");
        Assert.Equal("Cordless drill", both.Name);
        Assert.Equal(["blue", "plastic", "power tool"], both.Keywords);

        var unknown = AiNamer.ParseReply("UNKNOWN\nKeywords: red");
        Assert.Null(unknown.Name);
        Assert.Equal([AiNamer.UnsureKeyword, "red"], unknown.Keywords);
        Assert.Equal([AiNamer.UnsureKeyword], AiNamer.ParseReply("UNKNOWN").Keywords);
        Assert.Empty(AiNamer.ParseReply("").Keywords);

        var guess = AiNamer.ParseReply("Old radio\nKeywords: brown, unsure");
        Assert.Equal("Old radio", guess.Name);
        Assert.Contains(AiNamer.UnsureKeyword, guess.Keywords);

        Assert.Empty(AiNamer.ParseReply("Passport").Keywords);
    }

    [Fact]
    public async Task WithoutAHouseholdKeyTheDevelopersKeyIsUsed()
    {
        using var t = new TestDb();
        var anna = await t.AddUserAsync("anna");
        var home = await t.Households.CreateAsync(anna, "Home");
        (AiProvider, string)? used = null;
        t.Naming.NamerFactory = (provider, key, _) => { used = (provider, key); return new FixedNamer("Passport", "document"); };
        t.Naming.DevFallback = new DevAiKey(AiProvider.Claude, "sk-dev");

        var id = Guid.NewGuid();
        await t.Items.CreateAsync(anna, home, new ItemCreate(id, null, null, null, PhotoExpected: true));
        await t.Items.SetPhotoAsync(anna, id, new MemoryStream(PhotoTests.Jpeg(40, 30)));
        await t.Naming.NameAsync(id, default);

        Assert.Equal((AiProvider.Claude, "sk-dev"), used);
        Assert.Equal("Passport", (await t.Items.GetAsync(anna, id)).Name);
    }

    [Fact]
    public async Task TheAiIsToldTheKeywordsTheHouseholdAlreadyUses()
    {
        using var t = new TestDb();
        var anna = await t.AddUserAsync("anna");
        var home = await t.Households.CreateAsync(anna, "Home");
        var namer = new FixedNamer("Hammer", "metal");
        t.Naming.NamerFactory = (_, _, _) => namer;
        await t.Naming.SaveSettingsAsync(anna, home, AiProvider.Claude, null, "sk-test");

        var drill = Guid.NewGuid();
        var saw = Guid.NewGuid();
        var hammer = Guid.NewGuid();
        await t.Items.CreateAsync(anna, home, new ItemCreate(drill, null, "Drill", null));
        await t.Items.CreateAsync(anna, home, new ItemCreate(saw, null, "Saw", null));
        await t.Items.UpdateAsync(anna, drill, "Drill", null, 1, ["tool", "blue"]);
        await t.Items.UpdateAsync(anna, saw, "Saw", null, 1, ["tool", "metal"]);
        await t.Items.CreateAsync(anna, home, new ItemCreate(hammer, null, null, null, PhotoExpected: true));
        await t.Items.SetPhotoAsync(anna, hammer, new MemoryStream(PhotoTests.Jpeg(40, 30)));

        await t.Naming.NameAsync(hammer, default);

        // Most used first.
        Assert.Equal(["tool", "blue", "metal"], namer.Known);
        Assert.StartsWith("What is this? Keywords this household already uses: tool, blue, metal", AiNamer.Question(namer.Known));
        Assert.Equal("What is this?", AiNamer.Question([]));
    }

    private static async Task SetKeywordsAsync(TestDb t, string userId, Guid location, params string[] keywords) =>
        await t.Locations.UpdateAsync(userId, location, new LocationUpdate(
            (await t.Locations.TreeAsync(userId, await HouseholdOfAsync(t, location))).Find(location)!.Name, LocationKind.Other, null, null, null, keywords));

    private static async Task<Guid> HouseholdOfAsync(TestDb t, Guid location)
    {
        await using var db = t.Db.CreateDbContext();
        return await db.Locations.Where(l => l.Id == location).Select(l => l.HouseholdId).FirstAsync();
    }

    [Fact]
    public async Task AnItemArrivingAtALocationGetsItsKeywordsAndThoseAboveIt()
    {
        using var t = new TestDb();
        var anna = await t.AddUserAsync("anna");
        var home = await t.Households.CreateAsync(anna, "Home");
        var cellar = await t.Locations.CreateAsync(anna, home, null, "Cellar", LocationKind.Cellar);
        var box = await t.Locations.CreateAsync(anna, home, cellar, "Box", LocationKind.Box);
        var plain = await t.Locations.CreateAsync(anna, home, null, "Attic", LocationKind.Attic);
        await SetKeywordsAsync(t, anna, cellar, "Stored", "damp");
        await SetKeywordsAsync(t, anna, box, "christmas", "stored");

        // Added on the web or pushed by the phone.
        var added = Guid.NewGuid();
        await t.Items.CreateAsync(anna, home, new ItemCreate(added, box, "Star", null));
        Assert.Equal(["stored", "damp", "christmas"], Described(await t.Items.GetAsync(anna, added)));
        var pushed = Guid.NewGuid();
        await t.Sync.PushAsync(anna, new SyncPush(null, null, [new PushItem(pushed, home, box, "Bauble", null, 1, false)]));
        Assert.Equal(["stored", "damp", "christmas"], Described(await t.Items.GetAsync(anna, pushed)));

        // Moved in from the Inbox, next to what it has; moving away keeps them all.
        var moved = Guid.NewGuid();
        await t.Items.CreateAsync(anna, home, new ItemCreate(moved, null, "Lights", null));
        Assert.Empty(Described(await t.Items.GetAsync(anna, moved)));
        await t.Items.UpdateAsync(anna, moved, "Lights", null, 1, ["electric"]);
        await t.Items.MoveAsync(anna, [moved], cellar);
        Assert.Equal(["electric", "stored", "damp"], Described(await t.Items.GetAsync(anna, moved)));
        await t.Items.MoveAsync(anna, [moved], plain);
        Assert.Equal(["electric", "stored", "damp"], Described(await t.Items.GetAsync(anna, moved)));

        // Find and the keyword pages see them like any other keyword.
        Assert.Equal(2, (await t.Items.SearchAsync(anna, home, "christmas")).Items.Count);
        Assert.Contains(new KeywordCount("christmas", 2), await t.Items.KeywordsAsync(anna, home));
    }

    [Fact]
    public async Task LocationKeywordsLeaveItemsAlreadyThereAndComeBackOnlyByMoving()
    {
        using var t = new TestDb();
        var anna = await t.AddUserAsync("anna");
        var home = await t.Households.CreateAsync(anna, "Home");
        var box = await t.Locations.CreateAsync(anna, home, null, "Box", LocationKind.Box);
        var other = await t.Locations.CreateAsync(anna, home, null, "Shelf", LocationKind.Shelf);

        var early = Guid.NewGuid();
        await t.Items.CreateAsync(anna, home, new ItemCreate(early, box, "Early", null));
        await SetKeywordsAsync(t, anna, box, "christmas");
        Assert.Empty(Described(await t.Items.GetAsync(anna, early)));

        // A person takes it off: saving or "moving" to the same place doesn't add it back.
        var late = Guid.NewGuid();
        await t.Items.CreateAsync(anna, home, new ItemCreate(late, box, "Late", null));
        await t.Items.UpdateAsync(anna, late, "Late", null, 1, []);
        await t.Items.MoveAsync(anna, [late], box);
        Assert.Empty(Described(await t.Items.GetAsync(anna, late)));

        // Arriving again does.
        await t.Items.MoveAsync(anna, [late], other);
        await t.Items.MoveAsync(anna, [late], box);
        Assert.Equal(["christmas"], Described(await t.Items.GetAsync(anna, late)));
    }

    [Fact]
    public async Task ItemsMovedUpByDeletingALocationGetTheKeywordsOfTheOneTheyLandIn()
    {
        using var t = new TestDb();
        var anna = await t.AddUserAsync("anna");
        var home = await t.Households.CreateAsync(anna, "Home");
        var room = await t.Locations.CreateAsync(anna, home, null, "Room", LocationKind.Room);
        var box = await t.Locations.CreateAsync(anna, home, room, "Box", LocationKind.Box);
        var item = Guid.NewGuid();
        await t.Items.CreateAsync(anna, home, new ItemCreate(item, box, "Star", null));
        await SetKeywordsAsync(t, anna, room, "indoors");

        await t.Locations.DeleteAsync(anna, box, moveContents: true);

        Assert.Equal(["indoors"], Described(await t.Items.GetAsync(anna, item)));
    }

    [Fact]
    public async Task TheAiAddsItsKeywordsToThoseFromTheLocationButNotAfterAPersonsEdit()
    {
        using var t = new TestDb();
        var anna = await t.AddUserAsync("anna");
        var home = await t.Households.CreateAsync(anna, "Home");
        var namer = new FixedNamer("Star", "gold", "christmas");
        t.Naming.NamerFactory = (_, _, _) => namer;
        await t.Naming.SaveSettingsAsync(anna, home, AiProvider.Claude, null, "sk-test");
        var box = await t.Locations.CreateAsync(anna, home, null, "Box", LocationKind.Box);
        await SetKeywordsAsync(t, anna, box, "christmas", "fragile");

        var merged = Guid.NewGuid();
        await t.Items.CreateAsync(anna, home, new ItemCreate(merged, box, null, null, PhotoExpected: true));
        await t.Items.SetPhotoAsync(anna, merged, new MemoryStream(PhotoTests.Jpeg(40, 30)));
        await t.Naming.NameAsync(merged, default);
        var a = await t.Items.GetAsync(anna, merged);
        Assert.Equal(["christmas", "fragile", "gold"], Described(a));
        Assert.Equal(AiNaming.NotNeeded, a.AiNaming);
        // Location keywords count as known, even if no item had them yet.
        Assert.Contains("fragile", namer.Known);

        // A person's edit before the AI gets to it is final, and the AI doesn't come back later.
        var edited = Guid.NewGuid();
        await t.Items.CreateAsync(anna, home, new ItemCreate(edited, box, "Bauble", null, PhotoExpected: true));
        await t.Items.UpdateAsync(anna, edited, "Bauble", null, 1, ["christmas"]);
        await t.Items.SetPhotoAsync(anna, edited, new MemoryStream(PhotoTests.Jpeg(40, 30)));
        await t.Naming.NameAsync(edited, default);
        Assert.Equal(["christmas"], Described(await t.Items.GetAsync(anna, edited)));

        // Moving after the AI is done adds the location's, without asking the AI again.
        await t.Items.MoveAsync(anna, [merged], null);
        var shelf = await t.Locations.CreateAsync(anna, home, null, "Shelf", LocationKind.Shelf);
        await SetKeywordsAsync(t, anna, shelf, "upstairs");
        await t.Items.MoveAsync(anna, [merged], shelf);
        var moved = await t.Items.GetAsync(anna, merged);
        Assert.Equal(["christmas", "fragile", "gold", "upstairs"], Described(moved));
        Assert.Equal(AiNaming.NotNeeded, moved.AiNaming);
    }

    [Fact]
    public async Task AtTheKeywordLimitTheLocationsKeywordsAreKeptOverTheAis()
    {
        using var t = new TestDb();
        var anna = await t.AddUserAsync("anna");
        var home = await t.Households.CreateAsync(anna, "Home");
        var fromAi = Enumerable.Range(1, 14).Select(i => $"ai{i}").ToArray();
        t.Naming.NamerFactory = (_, _, _) => new FixedNamer("Red cable-knit crew-neck sweater", fromAi);
        await t.Naming.SaveSettingsAsync(anna, home, AiProvider.Claude, null, "sk-test");
        var closet = await t.Locations.CreateAsync(anna, home, null, "Closet", LocationKind.Closet);
        var fromLocation = Enumerable.Range(1, 10).Select(i => $"place{i}").ToArray();
        await SetKeywordsAsync(t, anna, closet, fromLocation);

        var item = Guid.NewGuid();
        await t.Items.CreateAsync(anna, home, new ItemCreate(item, closet, null, null, PhotoExpected: true));
        await t.Items.SetPhotoAsync(anna, item, new MemoryStream(PhotoTests.Jpeg(40, 30)));
        await t.Naming.NameAsync(item, default);

        var keywords = Described(await t.Items.GetAsync(anna, item));
        Assert.Equal(Item.MaxKeywords, keywords.Count);
        Assert.Equal(fromLocation, keywords.Take(10));
        Assert.Equal("ai10", keywords[^1]);
    }

    [Fact]
    public async Task AFailedAiCallKeepsTheLocationKeywordsAndIsRetriedWithAKey()
    {
        using var t = new TestDb();
        var anna = await t.AddUserAsync("anna");
        var home = await t.Households.CreateAsync(anna, "Home");
        var box = await t.Locations.CreateAsync(anna, home, null, "Box", LocationKind.Box);
        await SetKeywordsAsync(t, anna, box, "christmas");

        // No key yet, and a name typed: only the keywords are still open.
        var item = Guid.NewGuid();
        await t.Items.CreateAsync(anna, home, new ItemCreate(item, box, "Star", null, PhotoExpected: true));
        await t.Items.SetPhotoAsync(anna, item, new MemoryStream(PhotoTests.Jpeg(40, 30)));
        await t.Naming.NameAsync(item, default);
        var failed = await t.Items.GetAsync(anna, item);
        Assert.Equal(AiNaming.Failed, failed.AiNaming);
        Assert.Equal(["christmas"], Described(failed));

        t.Naming.NamerFactory = (_, _, _) => new FixedNamer("Ignored", "gold");
        await t.Naming.SaveSettingsAsync(anna, home, AiProvider.Claude, null, "sk-test");
        await t.Naming.NameAsync(item, default);

        var named = await t.Items.GetAsync(anna, item);
        Assert.Equal("Star", named.Name);
        Assert.Equal(["christmas", "gold"], Described(named));
    }

    [Fact]
    public async Task RenamingOrRemovingAKeywordChangesTheLocationsThatAddItToo()
    {
        using var t = new TestDb();
        var anna = await t.AddUserAsync("anna");
        var home = await t.Households.CreateAsync(anna, "Home");
        var box = await t.Locations.CreateAsync(anna, home, null, "Box", LocationKind.Box);
        await SetKeywordsAsync(t, anna, box, "xmas", "old");

        Assert.Equal(0, await t.Items.ReplaceKeywordAsync(anna, home, "xmas", "christmas"));
        await t.Items.ReplaceKeywordAsync(anna, home, "old", null);

        var tree = await t.Locations.TreeAsync(anna, home);
        Assert.Equal(["christmas"], tree.Find(box)!.Keywords);
        await t.Items.ReplaceKeywordAsync(anna, home, "christmas", null);
        Assert.Empty((await t.Locations.TreeAsync(anna, home)).Find(box)!.Keywords);
    }

    [Fact]
    public async Task ExistingKeywordsStayFinalAfterTheMigrationAndEmptyOnesStayOpenForTheAi()
    {
        using var t = new TestDb();
        var anna = await t.AddUserAsync("anna");
        var home = await t.Households.CreateAsync(anna, "Home");
        var set = Guid.NewGuid();
        var open = Guid.NewGuid();
        await t.Items.CreateAsync(anna, home, new ItemCreate(set, null, "Set", null));
        await t.Items.UpdateAsync(anna, set, "Set", null, 1, ["blue"]);
        await t.Items.CreateAsync(anna, home, new ItemCreate(open, null, "Open", null));
        await using var db = t.Db.CreateDbContext();
        // Items from before the migration have no "new", or anything else.
        await db.Items.Where(i => i.Id == open).ExecuteUpdateAsync(u => u.SetProperty(i => i.Keywords, (string?)null));
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261030144329_LabelSize");
        await migrator.MigrateAsync();

        Assert.True(await db.Items.Where(i => i.Id == set).Select(i => i.KeywordsFinal).FirstAsync());
        Assert.False(await db.Items.Where(i => i.Id == open).Select(i => i.KeywordsFinal).FirstAsync());
    }

    [Fact]
    public async Task EveryItemAddedGetsNewAndTakingItOffLeavesTheAiFree()
    {
        using var t = new TestDb();
        var anna = await t.AddUserAsync("anna");
        var home = await t.Households.CreateAsync(anna, "Home");
        var box = await t.Locations.CreateAsync(anna, home, null, "Box", LocationKind.Box);
        await SetKeywordsAsync(t, anna, box, "christmas");

        var item = Guid.NewGuid();
        await t.Items.CreateAsync(anna, home, new ItemCreate(item, box, null, null, PhotoExpected: true));
        Assert.Equal([ItemKeywords.New, "christmas"], (await t.Items.GetAsync(anna, item)).Keywords);

        // Taken off by hand, and by removing the keyword everywhere: neither stops the AI.
        await t.Items.SetKeywordsAsync(anna, item, ["christmas"]);
        await t.Items.SetKeywordsAsync(anna, item, [ItemKeywords.New, "christmas"]);
        await t.Items.ReplaceKeywordAsync(anna, home, ItemKeywords.New, null);
        t.Naming.NamerFactory = (_, _, _) => new FixedNamer("Bauble", "gold");
        await t.Naming.SaveSettingsAsync(anna, home, AiProvider.Claude, null, "sk-test");
        await t.Items.SetPhotoAsync(anna, item, new MemoryStream(PhotoTests.Jpeg(40, 30)));
        await t.Naming.NameAsync(item, default);
        Assert.Equal(["christmas", "gold"], (await t.Items.GetAsync(anna, item)).Keywords);
    }

    [Fact]
    public async Task NewAndUnsureAreNotOfferedToTheAiAndItCantAddNew()
    {
        using var t = new TestDb();
        var anna = await t.AddUserAsync("anna");
        var home = await t.Households.CreateAsync(anna, "Home");
        await t.Items.CreateAsync(anna, home, new ItemCreate(Guid.NewGuid(), null, "Earlier", null));
        var namer = new FixedNamer("Lamp", "brass");
        t.Naming.NamerFactory = (_, _, _) => namer;
        await t.Naming.SaveSettingsAsync(anna, home, AiProvider.Claude, null, "sk-test");
        var item = Guid.NewGuid();
        await t.Items.CreateAsync(anna, home, new ItemCreate(item, null, null, null, PhotoExpected: true));
        await t.Items.SetPhotoAsync(anna, item, new MemoryStream(PhotoTests.Jpeg(40, 30)));
        await t.Naming.NameAsync(item, default);

        Assert.DoesNotContain(ItemKeywords.New, namer.Known);
        Assert.DoesNotContain(ItemKeywords.New, AiNamer.ParseReply("Lamp\nKeywords: new, brass").Keywords);
    }

    [Fact]
    public async Task NewComesOffAfterTheHouseholdsDaysUnlessThatIsOff()
    {
        using var t = new TestDb();
        var anna = await t.AddUserAsync("anna");
        var home = await t.Households.CreateAsync(anna, "Home");
        var kept = await t.Households.CreateAsync(anna, "Kept");
        await t.Households.SetNewKeywordDaysAsync(anna, kept, 0);
        var old = Guid.NewGuid();
        var recent = Guid.NewGuid();
        var oldKept = Guid.NewGuid();
        await t.Items.CreateAsync(anna, home, new ItemCreate(old, null, "Old", null));
        await t.Items.SetKeywordsAsync(anna, old, [ItemKeywords.New, "newspaper"]);
        await t.Items.CreateAsync(anna, home, new ItemCreate(recent, null, "Recent", null));
        await t.Items.CreateAsync(anna, kept, new ItemCreate(oldKept, null, "Old too", null));
        await using (var db = t.Db.CreateDbContext())
        {
            var longAgo = DateTime.UtcNow.AddDays(-Household.DefaultNewKeywordDays - 1);
            await db.Items.Where(i => i.Id == old || i.Id == oldKept).ExecuteUpdateAsync(u => u.SetProperty(i => i.CreatedAt, longAgo));
        }

        Assert.Equal(1, await t.Items.RemoveExpiredNewKeywordAsync());

        Assert.Equal(["newspaper"], (await t.Items.GetAsync(anna, old)).Keywords);
        Assert.Equal([ItemKeywords.New], (await t.Items.GetAsync(anna, recent)).Keywords);
        Assert.Equal([ItemKeywords.New], (await t.Items.GetAsync(anna, oldKept)).Keywords);
    }

    [Fact]
    public async Task OnlyTheOwnerSetsTheDaysForNewWithinTheLimits()
    {
        using var t = new TestDb();
        var anna = await t.AddUserAsync("anna");
        var ben = await t.AddUserAsync("ben");
        var home = await t.Households.CreateAsync(anna, "Home");
        Assert.Equal(Household.DefaultNewKeywordDays, (await t.Households.ListAsync(anna)).Single().NewKeywordDays);

        await t.Households.SetNewKeywordDaysAsync(anna, home, 7);
        Assert.Equal(7, (await t.Households.ListAsync(anna)).Single().NewKeywordDays);

        await Assert.ThrowsAnyAsync<Exception>(() => t.Households.SetNewKeywordDaysAsync(ben, home, 3));
        await Assert.ThrowsAsync<InputException>(() => t.Households.SetNewKeywordDaysAsync(anna, home, -1));
        await Assert.ThrowsAsync<InputException>(() => t.Households.SetNewKeywordDaysAsync(anna, home, Household.MaxNewKeywordDays + 1));
    }

    [Fact]
    public void NewAndUnsureDontCountAgainstTheKeywordLimit()
    {
        var many = Enumerable.Range(1, Item.MaxKeywords + 5).Select(i => $"word{i}").ToList();
        var kept = ItemKeywords.Clean([ItemKeywords.New, .. many, AiNamer.UnsureKeyword]);

        Assert.Equal(Item.MaxKeywords + 2, kept.Count);
        Assert.Equal(ItemKeywords.New, kept[0]);
        Assert.Equal($"word{Item.MaxKeywords}", kept[^2]);
        Assert.Equal(AiNamer.UnsureKeyword, kept[^1]);
    }

    [Theory]
    [InlineData("Unsure lava lamp", "Lava lamp")]
    [InlineData("unsure: lava lamp", "Lava lamp")]
    [InlineData("Lava lamp (unsure)", "Lava lamp")]
    [InlineData("Lava lamp - unsure", "Lava lamp")]
    public void UnsureInTheNameBecomesTheKeyword(string name, string expected)
    {
        var reply = AiNamer.ParseReply($"{name}\nKeywords: blue, glowing");

        Assert.Equal(expected, reply.Name);
        Assert.Equal([AiNamer.UnsureKeyword, "blue", "glowing"], reply.Keywords);
    }

    [Fact]
    public void UnsureAloneIsNoNameAndOtherWordsInNamesStay()
    {
        var alone = AiNamer.ParseReply("Unsure\nKeywords: blue");
        Assert.Null(alone.Name);
        Assert.Equal([AiNamer.UnsureKeyword, "blue"], alone.Keywords);

        var sure = AiNamer.ParseReply("Unsurfaced board\nKeywords: wood");
        Assert.Equal("Unsurfaced board", sure.Name);
        Assert.Equal(["wood"], sure.Keywords);
    }

    [Fact]
    public async Task APinnedKeywordStaysWithoutItemsAndAColorLastsUntilItsRemoved()
    {
        using var t = new TestDb();
        var anna = await t.AddUserAsync("anna");
        var home = await t.Households.CreateAsync(anna, "Home");
        var item = Guid.NewGuid();
        await t.Items.CreateAsync(anna, home, new ItemCreate(item, null, "Lamp", null));
        await t.Items.SetKeywordsAsync(anna, item, ["pinned", "colored"]);
        await t.Items.SetKeywordPinnedAsync(anna, home, "Pinned", true);
        await t.Items.SetKeywordColorAsync(anna, home, "colored", "blue");

        var keywords = await t.Items.KeywordsAsync(anna, home);
        Assert.Contains(new KeywordCount("pinned", 1, null, true), keywords);
        Assert.Contains(new KeywordCount("colored", 1, "blue", false), keywords);

        // Removed: the pinned one stays with no items, the other is gone with its color.
        await t.Items.ReplaceKeywordAsync(anna, home, "pinned", null);
        await t.Items.ReplaceKeywordAsync(anna, home, "colored", null);
        Assert.Equal([new KeywordCount("pinned", 0, null, true)], await t.Items.KeywordsAsync(anna, home));
        await using (var db = t.Db.CreateDbContext())
            Assert.Equal(1, await db.KeywordSettings.CountAsync());

        // Neither a color nor a pin left: nothing is kept.
        await t.Items.SetKeywordPinnedAsync(anna, home, "pinned", false);
        Assert.Empty(await t.Items.KeywordsAsync(anna, home));
        await using (var db = t.Db.CreateDbContext())
            Assert.Equal(0, await db.KeywordSettings.CountAsync());
    }

    [Fact]
    public async Task AnUnknownColorIsNoColorAndOthersCantChangeAHouseholdsKeywords()
    {
        using var t = new TestDb();
        var anna = await t.AddUserAsync("anna");
        var ben = await t.AddUserAsync("ben");
        var home = await t.Households.CreateAsync(anna, "Home");
        await t.Items.SetKeywordPinnedAsync(anna, home, "tools", true);
        await t.Items.SetKeywordColorAsync(anna, home, "tools", "url(evil)");

        Assert.Null((await t.Items.KeywordsAsync(anna, home)).Single().Color);
        await Assert.ThrowsAnyAsync<Exception>(() => t.Items.SetKeywordPinnedAsync(ben, home, "tools", false));
        await Assert.ThrowsAnyAsync<Exception>(() => t.Items.KeywordsAsync(ben, home));
        await Assert.ThrowsAsync<InputException>(() => t.Items.SetKeywordPinnedAsync(anna, home, "  ", true));
    }

    [Fact]
    public async Task RenamingAndMergingTakeTheColorAndPinAlong()
    {
        using var t = new TestDb();
        var anna = await t.AddUserAsync("anna");
        var home = await t.Households.CreateAsync(anna, "Home");
        var item = Guid.NewGuid();
        await t.Items.CreateAsync(anna, home, new ItemCreate(item, null, "Lamp", null));
        await t.Items.SetKeywordsAsync(anna, item, ["old", "other"]);
        await t.Items.SetKeywordColorAsync(anna, home, "old", "red");
        await t.Items.SetKeywordPinnedAsync(anna, home, "old", true);
        await t.Items.SetKeywordColorAsync(anna, home, "other", "blue");

        await t.Items.ReplaceKeywordAsync(anna, home, "old", "renamed");
        Assert.Contains(new KeywordCount("renamed", 1, "red", true), await t.Items.KeywordsAsync(anna, home));

        // Into one that exists: its own color wins, and the pin carries over.
        await t.Items.ReplaceKeywordAsync(anna, home, "renamed", "other");
        Assert.Equal([new KeywordCount("other", 1, "blue", true)], await t.Items.KeywordsAsync(anna, home));
    }

    [Fact]
    public async Task SuggestionsAlsoHaveTheKeywordsOnlyOnLocations()
    {
        using var t = new TestDb();
        var anna = await t.AddUserAsync("anna");
        var home = await t.Households.CreateAsync(anna, "Home");
        var box = await t.Locations.CreateAsync(anna, home, null, "Box", LocationKind.Box);
        await SetKeywordsAsync(t, anna, box, "stored");
        var item = Guid.NewGuid();
        await t.Items.CreateAsync(anna, home, new ItemCreate(item, null, "Lamp", null));
        await t.Items.SetKeywordsAsync(anna, item, ["brass"]);

        var suggestions = await t.Items.KeywordSuggestionsAsync(anna, home);

        Assert.Contains(new KeywordCount("brass", 1), suggestions);
        Assert.Contains(new KeywordCount("stored", 0), suggestions);
        Assert.DoesNotContain(new KeywordCount("stored", 0), await t.Items.KeywordsAsync(anna, home));
    }

    // What describes the item, leaving out the "new" every added item starts with.
    private static IReadOnlyList<string> Described(ItemView item) => item.Keywords.Where(k => k != ItemKeywords.New).ToList();

    private sealed class FixedNamer(string name, params string[] keywords) : IAiNamer
    {
        public IReadOnlyList<string> Known { get; private set; } = [];

        public Task<AiDescription> DescribeAsync(byte[] jpeg, IReadOnlyList<string> known, CancellationToken ct)
        {
            Known = known;
            return Task.FromResult(new AiDescription(name, keywords));
        }
    }
}
