using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using Trovami.Core;
using Trovami.Core.Ai;
using Trovami.Core.Models;
using Trovami.Core.Photos;
using Trovami.Core.Services;

namespace Trovami.Tests;

public class LanguageTests
{
    private static readonly AiLanguage German = AiLanguages.For("de");

    [Fact]
    public async Task ANewItemGetsTheHouseholdsWordForNew()
    {
        using var t = new TestDb();
        var anna = await t.AddUserAsync("anna");
        var home = await t.Households.CreateAsync(anna, "Home");
        await t.Households.SetAiLanguageAsync(anna, home, "de");

        var item = Guid.NewGuid();
        await t.Items.CreateAsync(anna, home, new ItemCreate(item, null, "Lampe", null));

        Assert.Equal(["neu"], (await t.Items.GetAsync(anna, item)).Keywords);
    }

    [Fact]
    public async Task PickingALanguageRenamesTheTwoStatusWordsOnly()
    {
        using var t = new TestDb();
        var anna = await t.AddUserAsync("anna");
        var home = await t.Households.CreateAsync(anna, "Home");
        var item = Guid.NewGuid();
        await t.Items.CreateAsync(anna, home, new ItemCreate(item, null, "Lamp", null));
        await t.Items.SetKeywordsAsync(anna, item, [ItemKeywords.New, AiNamer.UnsureKeyword, "brass"]);
        await t.Items.SetKeywordColorAsync(anna, home, ItemKeywords.New, "red");

        await t.Households.SetAiLanguageAsync(anna, home, "de");

        Assert.Equal(["neu", "unsicher", "brass"], (await t.Items.GetAsync(anna, item)).Keywords);
        Assert.Equal("de", (await t.Households.ListAsync(anna)).Single().AiLanguage);
        var keywords = await t.Items.KeywordsAsync(anna, home);
        Assert.Equal("red", keywords.Single(k => k.Keyword == "neu").Color);
        Assert.DoesNotContain(keywords, k => k.Keyword == ItemKeywords.New);
    }

    [Fact]
    public async Task PickingALanguageDoesNotStopTheAiFromAddingKeywords()
    {
        using var t = new TestDb();
        var anna = await t.AddUserAsync("anna");
        var home = await t.Households.CreateAsync(anna, "Home");
        var item = Guid.NewGuid();
        await t.Items.CreateAsync(anna, home, new ItemCreate(item, null, "Lamp", null));

        await t.Households.SetAiLanguageAsync(anna, home, "de");

        await using var db = t.Db.CreateDbContext();
        Assert.False(db.Items.Single(i => i.Id == item).KeywordsFinal);
    }

    [Fact]
    public async Task AnUnknownLanguageIsRefused()
    {
        using var t = new TestDb();
        var anna = await t.AddUserAsync("anna");
        var home = await t.Households.CreateAsync(anna, "Home");

        await Assert.ThrowsAsync<InputException>(() => t.Households.SetAiLanguageAsync(anna, home, "xx"));
    }

    [Fact]
    public async Task TheHouseholdsWordForNewComesOffByItself()
    {
        using var t = new TestDb();
        var anna = await t.AddUserAsync("anna");
        var home = await t.Households.CreateAsync(anna, "Home");
        await t.Households.SetAiLanguageAsync(anna, home, "de");
        var item = Guid.NewGuid();
        await t.Items.CreateAsync(anna, home, new ItemCreate(item, null, "Lampe", null));
        await using (var db = t.Db.CreateDbContext())
        {
            db.Items.Single(i => i.Id == item).CreatedAt = DateTime.UtcNow.AddDays(-Household.DefaultNewKeywordDays - 1);
            await db.SaveChangesAsync();
        }

        Assert.Equal(1, await t.Items.RemoveExpiredNewKeywordAsync());

        Assert.Empty((await t.Items.GetAsync(anna, item)).Keywords);
    }

    [Fact]
    public void TheAiIsToldTheLanguageAndItsWordForUnsure()
    {
        var text = AiNamer.Instructions(German);

        Assert.Contains("in German", text);
        Assert.Contains("\"unsicher\"", text);
        Assert.DoesNotContain("US English", text);
        Assert.Contains("in US English", AiNamer.Instructions(AiLanguages.English));
    }

    [Fact]
    public void RepliesAreReadInTheLanguage()
    {
        var reply = AiNamer.ParseReply("Unsicher Lavalampe\nKeywords: neu, blau", German);

        Assert.Equal("Lavalampe", reply.Name);
        Assert.Equal(["unsicher", "blau"], reply.Keywords);
        Assert.Equal(["unsicher"], AiNamer.ParseReply("UNKNOWN", German).Keywords);
    }

    [Fact]
    public void TheAiWritesSsInsteadOfEszett()
    {
        var reply = AiNamer.ParseReply("Wei\u00DFe Stra\u00DFenlampe\nKeywords: wei\u00DF, gro\u00DF", German);

        Assert.Equal("Weisse Strassenlampe", reply.Name);
        Assert.Equal(["weiss", "gross"], reply.Keywords);
        Assert.Contains("never the letter \u00DF", AiNamer.Instructions(German));
        Assert.Contains("never the letter \u00DF", AiTranslationService.Instructions(German, "names"));
        Assert.Equal("Fuss", AiTranslationService.ParseReply("{\"1\":\"Fu\u00DF\"}")["1"]);
    }

    [Fact]
    public async Task TranslatingAgainReplacesAnEszettAlreadyThere()
    {
        using var t = new TestDb();
        var (anna, home, translation) = await SetUpAsync(t, new Dictionary<string, string> { ["wei\u00DF"] = "wei\u00DF" });
        var item = Guid.NewGuid();
        await t.Items.CreateAsync(anna, home, new ItemCreate(item, null, null, null));
        await t.Items.SetKeywordsAsync(anna, item, ["wei\u00DF"]);

        await translation.StartAsync(anna, home);
        await translation.TranslateAsync(home, default);

        Assert.Equal(["weiss"], (await t.Items.GetAsync(anna, item)).Keywords);
    }

    [Fact]
    public async Task WhatAPersonTypesKeepsItsEszett()
    {
        using var t = new TestDb();
        var anna = await t.AddUserAsync("anna");
        var home = await t.Households.CreateAsync(anna, "Home");
        await t.Households.SetAiLanguageAsync(anna, home, "de");
        var item = Guid.NewGuid();

        await t.Items.CreateAsync(anna, home, new ItemCreate(item, null, "Stra\u00DFenkarte", null));

        Assert.Equal("Stra\u00DFenkarte", (await t.Items.GetAsync(anna, item)).Name);
    }

    [Fact]
    public void StatusWordsOfEveryLanguageStayOutOfTheKeywordLimit()
    {
        var many = Enumerable.Range(0, Item.MaxKeywords + 3).Select(i => "word" + i);

        var kept = ItemKeywords.Clean([.. many, "neu", "unsicher"]);

        Assert.Equal(Item.MaxKeywords + 2, kept.Count);
        Assert.Contains("neu", kept);
        Assert.Contains("unsicher", kept);
    }

    [Fact]
    public async Task TranslatingChangesNamesNotesAndKeywordsButNotLocations()
    {
        using var t = new TestDb();
        var (anna, home, translation) = await SetUpAsync(t, new Dictionary<string, string>
        {
            ["Drill"] = "Bohrmaschine", ["In the box"] = "In der Kiste", ["blue"] = "blau", ["tools"] = "werkzeug",
        });
        var location = await t.Locations.CreateAsync(anna, home, null, "Cellar", LocationKind.Cellar);
        await t.Locations.UpdateAsync(anna, location, new LocationUpdate("Cellar", LocationKind.Cellar, "Downstairs", null, "A1", ["tools"]));
        var item = Guid.NewGuid();
        await t.Items.CreateAsync(anna, home, new ItemCreate(item, null, "Drill", "In the box"));
        await t.Items.SetKeywordsAsync(anna, item, ["neu", "blue"]);
        await t.Items.SetKeywordColorAsync(anna, home, "blue", "red");
        await t.Items.SetKeywordPinnedAsync(anna, home, "blue", true);

        await translation.StartAsync(anna, home);
        await translation.TranslateAsync(home, default);

        var view = await t.Items.GetAsync(anna, item);
        Assert.Equal("Bohrmaschine", view.Name);
        Assert.Equal(NameSource.Person, view.NameSource);
        Assert.Equal("In der Kiste", view.Notes);
        Assert.Equal(["neu", "blau"], view.Keywords);
        var keyword = (await t.Items.KeywordsAsync(anna, home)).Single(k => k.Keyword == "blau");
        Assert.Equal(("red", true), (keyword.Color, keyword.Pinned));
        var tree = await t.Locations.TreeAsync(anna, home);
        var cellar = tree.Find(location)!;
        Assert.Equal(("Cellar", "Downstairs", "A1"), (cellar.Name, cellar.Notes, cellar.Label));
        Assert.Equal(["werkzeug"], cellar.Keywords);
        var status = translation.StatusOf(home)!;
        Assert.Equal((false, 0), (status.Running, status.Failed));
        Assert.Equal(status.Total, status.Done);
    }

    [Fact]
    public async Task TwoKeywordsWithTheSameTranslationBecomeOne()
    {
        using var t = new TestDb();
        var (anna, home, translation) = await SetUpAsync(t, new Dictionary<string, string> { ["couch"] = "sofa", ["settee"] = "sofa" });
        var item = Guid.NewGuid();
        await t.Items.CreateAsync(anna, home, new ItemCreate(item, null, "Thing", null));
        await t.Items.SetKeywordsAsync(anna, item, ["couch", "settee"]);

        await translation.StartAsync(anna, home);
        await translation.TranslateAsync(home, default);

        Assert.Equal(["sofa"], (await t.Items.GetAsync(anna, item)).Keywords);
    }

    [Fact]
    public async Task WhatCantBeTranslatedStaysAsItWas()
    {
        using var t = new TestDb();
        var (anna, home, translation) = await SetUpAsync(t, new Dictionary<string, string> { ["Drill"] = "Bohrmaschine" });
        var (done, missed) = (Guid.NewGuid(), Guid.NewGuid());
        await t.Items.CreateAsync(anna, home, new ItemCreate(done, null, "Drill", null));
        await t.Items.CreateAsync(anna, home, new ItemCreate(missed, null, "Saw", null));

        await translation.StartAsync(anna, home);
        await translation.TranslateAsync(home, default);

        Assert.Equal("Bohrmaschine", (await t.Items.GetAsync(anna, done)).Name);
        Assert.Equal("Saw", (await t.Items.GetAsync(anna, missed)).Name);
        Assert.Equal(1, translation.StatusOf(home)!.Failed);
    }

    [Fact]
    public async Task AFailingAiLeavesEverythingAndSaysWhy()
    {
        using var t = new TestDb();
        var (anna, home, translation) = await SetUpAsync(t, new Dictionary<string, string>());
        t.Naming.NamerFactory = (_, _, _) => new Translator(null);
        var item = Guid.NewGuid();
        await t.Items.CreateAsync(anna, home, new ItemCreate(item, null, "Drill", null));

        await translation.StartAsync(anna, home);
        await translation.TranslateAsync(home, default);

        Assert.Equal("Drill", (await t.Items.GetAsync(anna, item)).Name);
        var status = translation.StatusOf(home)!;
        Assert.Equal((false, 1, "No key."), (status.Running, status.Failed, status.Error));
    }

    [Fact]
    public async Task OnlyTheOwnerCanTranslateOrPickALanguage()
    {
        using var t = new TestDb();
        var (anna, home, translation) = await SetUpAsync(t, new Dictionary<string, string>());
        var ben = await t.AddUserAsync("ben");
        await t.Households.AddNewAccountAsync(anna, home, ben);

        await Assert.ThrowsAsync<InputException>(() => translation.StartAsync(ben, home));
        await Assert.ThrowsAsync<InputException>(() => t.Households.SetAiLanguageAsync(ben, home, "fr"));
    }

    [Fact]
    public async Task ASecondTranslationCantStartWhileOneRuns()
    {
        using var t = new TestDb();
        var (anna, home, translation) = await SetUpAsync(t, new Dictionary<string, string>());

        await translation.StartAsync(anna, home);

        await Assert.ThrowsAsync<InputException>(() => translation.StartAsync(anna, home));
    }

    [Theory]
    [InlineData("{\"1\":\"a\"}", 1)]
    [InlineData("```json\n{\"1\":\"a\",\"2\":\"b\"}\n```", 2)]
    [InlineData("{\"1\":\"a\",\"2\":", 0)]
    [InlineData("Sorry", 0)]
    [InlineData("{\"1\":5}", 0)]
    public void TranslationRepliesAreReadCarefully(string reply, int count) =>
        Assert.Equal(count, AiTranslationService.ParseReply(reply).Count);

    [Fact]
    public void BatchesStayWithinTheirLimits()
    {
        var batches = AiTranslationService.Batches(Enumerable.Repeat(new string('x', 3000), 7), s => s).ToList();
        Assert.Equal([2, 2, 2, 1], batches.Select(b => b.Count));

        var longOne = AiTranslationService.Batches([new string('x', 20000)], s => s).ToList();
        Assert.Single(longOne);
        Assert.Equal(2, AiTranslationService.Batches(Enumerable.Repeat("x", 61), s => s).Count());
    }

    [Fact]
    public void CroppingKeepsTheChosenPart()
    {
        var (full, thumb) = PhotoStore.Crop(PhotoTests.Jpeg(800, 600), 0.25, 0.5, 0.5, 0.25);

        using var f = SKBitmap.Decode(full);
        using var small = SKBitmap.Decode(thumb);
        Assert.Equal((400, 150), (f.Width, f.Height));
        Assert.Equal(PhotoStore.ThumbSide, small.Width);
    }

    [Fact]
    public void CroppingStaysInsideThePhotoAndRefusesATinyPart()
    {
        var (full, _) = PhotoStore.Crop(PhotoTests.Jpeg(800, 600), 0.5, 0.5, 2, 2);
        using var f = SKBitmap.Decode(full);
        Assert.Equal((400, 300), (f.Width, f.Height));

        Assert.Throws<InputException>(() => PhotoStore.Crop(PhotoTests.Jpeg(800, 600), 0.5, 0.5, 0.001, 0.5));
    }

    [Fact]
    public async Task CroppingAnItemsPhotoGivesItANewVersion()
    {
        using var t = new TestDb();
        var anna = await t.AddUserAsync("anna");
        var home = await t.Households.CreateAsync(anna, "Home");
        var item = Guid.NewGuid();
        await t.Items.CreateAsync(anna, home, new ItemCreate(item, null, "Lamp", null));
        await Assert.ThrowsAsync<InputException>(() => t.Items.CropPhotoAsync(anna, item, 0, 0, 1, 1));
        await t.Items.SetPhotoAsync(anna, item, new MemoryStream(PhotoTests.Jpeg(800, 600)));
        var before = (await t.Items.GetAsync(anna, item)).PhotoVersion;

        await t.Items.CropPhotoAsync(anna, item, 0, 0, 0.5, 0.5);

        Assert.NotEqual(before, (await t.Items.GetAsync(anna, item)).PhotoVersion);
        using var photo = SKBitmap.Decode(await t.Photos.ReadAsync(home, item));
        Assert.Equal((400, 300), (photo.Width, photo.Height));
    }

    private static async Task<(string Anna, Guid Home, AiTranslationService Translation)> SetUpAsync(TestDb t, IReadOnlyDictionary<string, string> translations)
    {
        var anna = await t.AddUserAsync("anna");
        var home = await t.Households.CreateAsync(anna, "Home");
        t.Naming.NamerFactory = (_, _, _) => new Translator(translations);
        await t.Naming.SaveSettingsAsync(anna, home, AiProvider.Claude, null, "sk-test");
        await t.Households.SetAiLanguageAsync(anna, home, "de");
        return (anna, home, new AiTranslationService(t.Db, t.Naming, NullLogger<AiTranslationService>.Instance));
    }

    // Answers with the translations it knows, and leaves out the rest; without any, it fails.
    private sealed class Translator(IReadOnlyDictionary<string, string>? known) : IAiNamer
    {
        public Task<AiDescription> DescribeAsync(byte[] jpeg, IReadOnlyList<string> seen, AiLanguage language, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<string> AskAsync(string instructions, string text, CancellationToken ct)
        {
            if (known is null)
                throw new AiException("No key.");
            var asked = JsonSerializer.Deserialize<Dictionary<string, string>>(text)!;
            return Task.FromResult(JsonSerializer.Serialize(asked.Where(p => known.ContainsKey(p.Value)).ToDictionary(p => p.Key, p => known[p.Value])));
        }
    }
}
