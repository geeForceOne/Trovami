using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Trovami.Core.Data;
using Trovami.Core.Services;

namespace Trovami.Core.Ai;

// Keywords the AI thinks should become one, and the name they'd share. Related: close relatives under
// a broader word (screws and nails as fasteners), which loses detail; otherwise the same word in other
// forms or languages, or a synonym.
public sealed record KeywordGroup(string Name, bool Related, IReadOnlyList<string> Words);

// The owner's on-demand tidy-up of a household's keywords: the AI proposes groups from the keywords
// alone (no items, no photos), a person reviews them and merges what they keep.
public sealed class AiKeywordGrouper(IDbContextFactory<AppDbContext> dbFactory, AiNamingService naming, ItemService items)
{
    // More would make one answer too long to come back whole; the most used go first.
    public const int MaxKeywords = 1500;

    public async Task<IReadOnlyList<KeywordGroup>> SuggestAsync(string userId, Guid householdId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var household = await Access.RequireOwnerAsync(db, userId, householdId, ct);
        var namer = naming.NamerFor(household) ?? throw new InputException("Set up the household's AI first.");
        var counts = await CountsAsync(userId, householdId, ct);
        if (counts.Count < 2)
            return [];
        var language = AiLanguages.For(household.AiLanguage);
        var message = new JsonObject();
        foreach (var (word, count) in counts.Take(MaxKeywords))
            message[word] = count;
        var reply = await namer.AskAsync(Instructions(language), message.ToJsonString(), ct);
        return ParseReply(reply, counts);
    }

    // Puts every word of a group under its name, on items and locations, taking colors and pins along.
    // A name that's none of the words is new: the first word is renamed to it, the rest merge into it.
    // Returns how many item keywords changed.
    public async Task<int> MergeAsync(string userId, Guid householdId, IReadOnlyList<string> words, string name, CancellationToken ct = default)
    {
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
            await Access.RequireOwnerAsync(db, userId, householdId, ct);
        var target = CleanOne(name);
        if (target is null || ItemKeywords.IsStatus(target))
            throw new InputException("Give the group a name to merge into.");
        var changed = 0;
        foreach (var word in words.Select(CleanOne).OfType<string>().Distinct().Where(w => w != target && !ItemKeywords.IsStatus(w)))
            changed += await items.ReplaceKeywordAsync(userId, householdId, word, target, ct);
        return changed;
    }

    // Every keyword on items and locations with its item count, most used first; not "new" and "unsure".
    private async Task<List<(string Word, int Count)>> CountsAsync(string userId, Guid householdId, CancellationToken ct) =>
        (await items.KeywordSuggestionsAsync(userId, householdId, ct))
            .Where(k => !ItemKeywords.IsStatus(k.Keyword))
            .OrderByDescending(k => k.Items).ThenBy(k => k.Keyword, StringComparer.Ordinal)
            .Select(k => (k.Keyword, k.Items)).ToList();

    internal static string Instructions(AiLanguage language) =>
        "You tidy up the keywords of a home inventory: the words people search for to find their things. " +
        "The message is a JSON object of the household's keywords, each with how many items have it. " +
        "Find keywords that should become one keyword: the same word in another form (spelling, singular or plural), " +
        "a synonym, the same word in another language, or close relatives that one broader word covers well, " +
        "such as screws and nails under fasteners. " +
        "Don't group words that only belong to the same area but that people search for separately, " +
        "such as red and blue, winter and summer, or drill and saw. Brand names stay apart from everything else. " +
        $"Give each group the keyword that should replace its words, lowercase and in {language.Name}: for words with the same meaning " +
        $"usually the most used one, written in {language.Name}; for close relatives a broader word, which may be new. " +
        "Set related to true when the words are close relatives rather than the same meaning. " +
        "Leave out every keyword that has nothing to group with. Each keyword goes in one group at most, written exactly as in the message. " +
        "Reply with only JSON, nothing else: {\"groups\":[{\"name\":\"fasteners\",\"related\":true,\"words\":[\"screws\",\"nails\"]}]}" +
        (language.WritingRule is { } rule ? $" {rule}" : "");

    // Groups of at least two keywords the household has, each keyword in one group only, most used
    // words first; nothing when the reply isn't such JSON.
    internal static IReadOnlyList<KeywordGroup> ParseReply(string reply, IReadOnlyList<(string Word, int Count)> counts)
    {
        var (start, end) = (reply.IndexOf('{'), reply.LastIndexOf('}'));
        if (start < 0 || end <= start)
            return [];
        JsonArray groups;
        try
        {
            groups = JsonNode.Parse(reply[start..(end + 1)])?["groups"] as JsonArray ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
        var known = counts.ToDictionary(c => c.Word, c => c.Count);
        var taken = new HashSet<string>();
        var result = new List<KeywordGroup>();
        foreach (var group in groups.OfType<JsonObject>())
        {
            var words = Strings(group["words"]).Select(w => CleanOne(AiLanguages.WithoutEszett(w))).OfType<string>().Distinct()
                .Where(w => known.ContainsKey(w) && !ItemKeywords.IsStatus(w) && taken.Add(w))
                .OrderByDescending(w => known[w]).ThenBy(w => w, StringComparer.Ordinal).ToList();
            if (words.Count < 2)
            {
                taken.ExceptWith(words);
                continue;
            }
            var name = CleanOne(AiLanguages.WithoutEszett(String(group["name"]) ?? ""));
            if (name is null || ItemKeywords.IsStatus(name))
                name = words[0];
            var related = group["related"] is JsonValue v && v.TryGetValue<bool>(out var r) && r;
            result.Add(new KeywordGroup(name, related, words));
        }
        return result;
    }

    // One keyword as items store it; Clean on a whole group would stop at an item's keyword limit.
    private static string? CleanOne(string word) => ItemKeywords.Clean([word]).FirstOrDefault();

    private static string? String(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static IEnumerable<string> Strings(JsonNode? node) =>
        node is JsonArray array ? array.Select(String).OfType<string>() : [];
}
