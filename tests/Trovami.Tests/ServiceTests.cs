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
        Assert.Equal(["blue", "power tool"], a.Keywords);
        Assert.Equal(AiNaming.NotNeeded, a.AiNaming);
        // A person set keywords first: the AI still names it, but leaves the keywords alone.
        var b = await t.Items.GetAsync(anna, edited);
        Assert.Equal("Cordless drill", b.Name);
        Assert.Equal(["red", "garden"], b.Keywords);

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
        Assert.Equal(["tool", "metal"], (await t.Items.GetAsync(anna, a)).Keywords);
    }

    [Fact]
    public void TheAiReplyGivesANameAndKeywords()
    {
        var both = AiNamer.ParseReply("**Cordless drill.**\nKeywords: Blue, plastic , power tool, blue\n");
        Assert.Equal("Cordless drill", both.Name);
        Assert.Equal(["blue", "plastic", "power tool"], both.Keywords);

        var unknown = AiNamer.ParseReply("UNKNOWN\nKeywords: red");
        Assert.Null(unknown.Name);
        Assert.Equal(["red"], unknown.Keywords);

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
