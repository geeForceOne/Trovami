using System.Text.Json;
using Trovami.Core;
using Trovami.Core.Ai;
using Trovami.Core.Models;
using Trovami.Core.Services;

namespace Trovami.Tests;

public class KeywordGroupTests
{
    private static readonly IReadOnlyList<(string Word, int Count)> Counts =
        [("screws", 9), ("screw", 3), ("nails", 4), ("weiss", 2), ("white", 5), ("red", 6), ("unsure", 2)];

    [Fact]
    public void TheReplyKeepsOnlyGroupsOfTheHouseholdsOwnKeywords()
    {
        var reply = """
            Here you go:
            {"groups":[
              {"name":"Screw","related":false,"words":["screw","Screws","bolts"]},
              {"name":"fasteners","related":true,"words":["screws","nails"]},
              {"name":"white","words":["white","weiß"]},
              {"name":"red","words":["red","unsure"]},
              {"name":"unsure","words":["nails","red"]}
            ]}
            """;

        var groups = AiKeywordGrouper.ParseReply(reply, Counts);

        // "bolts" isn't the household's; "screws" is taken by the first group, so the second has one word
        // left and goes; "red" has nobody but "unsure".
        Assert.Equal(3, groups.Count);
        Assert.Equal(("screw", false), (groups[0].Name, groups[0].Related));
        Assert.Equal(["screws", "screw"], groups[0].Words);
        Assert.Equal(["white", "weiss"], groups[1].Words);
        // "nails" and "red" are free again; a status word can't be the name, so the most used word is.
        Assert.Equal("red", groups[2].Name);
        Assert.Equal(["red", "nails"], groups[2].Words);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Sorry, I can't help with that.")]
    [InlineData("{\"groups\": [ {\"name\": \"x\"")]
    [InlineData("{\"groups\": \"none\"}")]
    public void AnUnusableReplyMeansNoGroups(string reply) =>
        Assert.Empty(AiKeywordGrouper.ParseReply(reply, Counts));

    [Fact]
    public async Task TheAiGetsTheKeywordsWithCountsButNotNewOrUnsure()
    {
        using var t = new TestDb();
        var (anna, home, grouper, namer) = await SetUpAsync(t, """{"groups":[{"name":"screw","words":["screw","screws"]}]}""");
        var box = await t.Locations.CreateAsync(anna, home, null, "Box", LocationKind.Box);
        await t.Locations.UpdateAsync(anna, box, new LocationUpdate("Box", LocationKind.Box, null, null, null, ["workshop"]));
        await AddAsync(t, anna, home, "screw", "metal", "unsure");
        await AddAsync(t, anna, home, "screws", "metal");

        var groups = await grouper.SuggestAsync(anna, home);

        var asked = JsonSerializer.Deserialize<Dictionary<string, int>>(namer.Asked!)!;
        Assert.Equal(new Dictionary<string, int> { ["metal"] = 2, ["screw"] = 1, ["screws"] = 1, ["workshop"] = 0 }, asked);
        Assert.Equal("screw", Assert.Single(groups).Name);
    }

    [Fact]
    public async Task OnlyTheOwnerCanAskOrMerge()
    {
        using var t = new TestDb();
        var (anna, home, grouper, _) = await SetUpAsync(t, "{}");
        var ben = await t.AddUserAsync("ben");

        await Assert.ThrowsAnyAsync<Exception>(() => grouper.SuggestAsync(ben, home));
        await Assert.ThrowsAnyAsync<Exception>(() => grouper.MergeAsync(ben, home, ["a", "b"], "a"));
    }

    [Fact]
    public async Task MergingPutsTheKeptWordsUnderTheNameOnItemsAndLocations()
    {
        using var t = new TestDb();
        var (anna, home, grouper, _) = await SetUpAsync(t, "{}");
        var box = await t.Locations.CreateAsync(anna, home, null, "Box", LocationKind.Box);
        await t.Locations.UpdateAsync(anna, box, new LocationUpdate("Box", LocationKind.Box, null, null, null, ["screws"]));
        var a = await AddAsync(t, anna, home, "screw", "metal");
        var b = await AddAsync(t, anna, home, "screws", "screw");
        await t.Items.SetKeywordColorAsync(anna, home, "screws", "blue");

        await grouper.MergeAsync(anna, home, ["screws", "screw"], "Screw");

        Assert.Equal(["screw", "metal"], Described(await t.Items.GetAsync(anna, a)));
        Assert.Equal(["screw"], Described(await t.Items.GetAsync(anna, b)));
        Assert.Equal(["screw"], (await t.Locations.TreeAsync(anna, home)).Find(box)!.Keywords);
        Assert.Equal("blue", (await t.Items.KeywordsAsync(anna, home)).Single(k => k.Keyword == "screw").Color);
    }

    [Fact]
    public async Task ANewNameTakesOverTheWordsAndTheirColor()
    {
        using var t = new TestDb();
        var (anna, home, grouper, _) = await SetUpAsync(t, "{}");
        var a = await AddAsync(t, anna, home, "screws");
        var b = await AddAsync(t, anna, home, "nails", "steel");
        await t.Items.SetKeywordColorAsync(anna, home, "nails", "gray");

        await grouper.MergeAsync(anna, home, ["screws", "nails"], "fasteners");

        Assert.Equal(["fasteners"], Described(await t.Items.GetAsync(anna, a)));
        Assert.Equal(["fasteners", "steel"], Described(await t.Items.GetAsync(anna, b)));
        var keywords = (await t.Items.KeywordsAsync(anna, home)).Where(k => k.Keyword != ItemKeywords.New).ToList();
        Assert.Equal(["fasteners", "steel"], keywords.Select(k => k.Keyword));
        Assert.Equal("gray", keywords[0].Color);
    }

    [Fact]
    public async Task AStatusWordIsNoNameToMergeInto()
    {
        using var t = new TestDb();
        var (anna, home, grouper, _) = await SetUpAsync(t, "{}");
        await AddAsync(t, anna, home, "screws");

        await Assert.ThrowsAsync<InputException>(() => grouper.MergeAsync(anna, home, ["screws"], "new"));
        await Assert.ThrowsAsync<InputException>(() => grouper.MergeAsync(anna, home, ["screws"], "  "));
    }

    private static async Task<(string Anna, Guid Home, AiKeywordGrouper Grouper, Asker Namer)> SetUpAsync(TestDb t, string reply)
    {
        var anna = await t.AddUserAsync("anna");
        var home = await t.Households.CreateAsync(anna, "Home");
        var namer = new Asker(reply);
        t.Naming.NamerFactory = (_, _, _) => namer;
        await t.Naming.SaveSettingsAsync(anna, home, AiProvider.Claude, null, "sk-test");
        return (anna, home, new AiKeywordGrouper(t.Db, t.Naming, t.Items), namer);
    }

    private static async Task<Guid> AddAsync(TestDb t, string userId, Guid home, params string[] keywords)
    {
        var id = Guid.NewGuid();
        await t.Items.CreateAsync(userId, home, new ItemCreate(id, null, "Thing", null));
        await t.Items.SetKeywordsAsync(userId, id, [.. keywords, ItemKeywords.New]);
        return id;
    }

    private static IReadOnlyList<string> Described(ItemView item) => item.Keywords.Where(k => k != ItemKeywords.New).ToList();

    // Answers every question with the same reply and remembers what it was asked.
    private sealed class Asker(string reply) : IAiNamer
    {
        public string? Asked { get; private set; }

        public Task<AiDescription> DescribeAsync(byte[] jpeg, IReadOnlyList<string> known, AiLanguage language, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<string> AskAsync(string instructions, string text, CancellationToken ct)
        {
            Asked = text;
            return Task.FromResult(reply);
        }
    }
}
