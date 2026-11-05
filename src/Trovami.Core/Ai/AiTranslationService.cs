using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Trovami.Core.Data;
using Trovami.Core.Models;
using Trovami.Core.Services;

namespace Trovami.Core.Ai;

// What translating would change. Total 0: nothing to do.
public sealed record TranslationCounts(int Names, int Notes, int Keywords, bool HasAi)
{
    public int Total => Names + Notes + Keywords;
}

// How a household's translation is going, or how the last one went. Kept in memory: after a restart
// a running one is gone, and starting it again leaves alone what's already translated.
public sealed record TranslationStatus(bool Running, int Done, int Total, int Failed, string LanguageName, string? Error);

// Puts a household's item names, item notes and keywords into its language, as an owner's action. Text
// goes to the AI in batches, without photos. What can't be translated stays as it was. A name or note
// that a person changes meanwhile keeps the person's text.
public sealed class AiTranslationService(
    IDbContextFactory<AppDbContext> dbFactory, AiNamingService naming, ILogger<AiTranslationService> logger)
{
    private const int MaxBatchTexts = 60;
    private const int MaxBatchChars = 8000;

    private readonly Channel<Guid> _queue = Channel.CreateUnbounded<Guid>();
    private readonly ConcurrentDictionary<Guid, TranslationStatus> _status = new();

    public TranslationStatus? StatusOf(Guid householdId) => _status.GetValueOrDefault(householdId);

    public async Task<TranslationCounts> CountsAsync(string userId, Guid householdId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var household = await Access.RequireOwnerAsync(db, userId, householdId, ct);
        var names = await db.Items.CountAsync(i => i.HouseholdId == householdId && i.Name != null, ct);
        var notes = await db.Items.CountAsync(i => i.HouseholdId == householdId && i.Notes != null, ct);
        var keywords = (await KeywordsToTranslateAsync(db, householdId, ct)).Count;
        return new TranslationCounts(names, notes, keywords, naming.NamerFor(household) is not null);
    }

    public async Task StartAsync(string userId, Guid householdId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var household = await Access.RequireOwnerAsync(db, userId, householdId, ct);
        if (naming.NamerFor(household) is null)
            throw new InputException("Set up the household's AI first.");
        var name = AiLanguages.For(household.AiLanguage).Name;
        var started = new TranslationStatus(true, 0, 0, 0, name, null);
        var isFree = _status.TryGetValue(householdId, out var current)
            ? current.Running ? false : _status.TryUpdate(householdId, started, current)
            : _status.TryAdd(householdId, started);
        if (!isFree)
            throw new InputException("This household is already being translated.");
        _queue.Writer.TryWrite(householdId);
    }

    // The host's loop.
    public async Task RunAsync(CancellationToken ct)
    {
        await foreach (var householdId in _queue.Reader.ReadAllAsync(ct))
        {
            try
            {
                await TranslateAsync(householdId, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Translating household {HouseholdId} failed", householdId);
                if (_status.TryGetValue(householdId, out var status))
                    _status[householdId] = status with { Running = false, Error = "The translation stopped because of an error." };
            }
        }
    }

    internal async Task TranslateAsync(Guid householdId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var household = await db.Households.AsNoTracking().FirstOrDefaultAsync(h => h.Id == householdId, ct);
        var namer = household is null ? null : naming.NamerFor(household);
        if (household is null || namer is null)
        {
            if (_status.TryGetValue(householdId, out var unable))
                _status[householdId] = unable with { Running = false, Error = "The household has no AI anymore." };
            return;
        }
        var language = AiLanguages.For(household.AiLanguage);
        var items = await db.Items.AsNoTracking().Where(i => i.HouseholdId == householdId && (i.Name != null || i.Notes != null))
            .Select(i => new { i.Id, i.Name, i.Notes }).ToListAsync(ct);
        var names = items.Where(i => i.Name is not null).Select(i => (i.Id, Text: i.Name!)).ToList();
        var notes = items.Where(i => i.Notes is not null).Select(i => (i.Id, Text: i.Notes!)).ToList();
        var words = await KeywordsToTranslateAsync(db, householdId, ct);

        var run = new Run(this, householdId, namer, language, names.Count + notes.Count + words.Count);
        var now = DateTime.UtcNow;

        await run.TranslateAsync(names, "names of things stored in a home", async (id, old, text) =>
        {
            if (text.Length > Item.MaxNameLength || text.Contains('\n'))
                return false;
            // NameSource stays: a typed name remains typed, so the AI doesn't step in.
            await db.Items.Where(i => i.Id == id && i.Name == old)
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.Name, text).SetProperty(i => i.UpdatedAt, now), ct);
            return true;
        }, ct);

        await run.TranslateAsync(notes, "people's notes about things stored in a home; keep line breaks", async (id, old, text) =>
        {
            if (text.Length > Item.MaxNotesLength)
                return false;
            await db.Items.Where(i => i.Id == id && i.Notes == old)
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.Notes, text).SetProperty(i => i.UpdatedAt, now), ct);
            return true;
        }, ct);

        // Each keyword once, and all changed together at the end, so the same word stays the same
        // everywhere and its color and pin go along.
        var renamed = new Dictionary<string, string>();
        await run.TranslateAsync(words.Select(w => (Id: w, Text: w)).ToList(), "lowercase search keywords for things stored in a home; keep them lowercase and short", (word, _, text) =>
        {
            var clean = ItemKeywords.Clean([text]).FirstOrDefault();
            if (clean is null || ItemKeywords.IsStatus(clean))
                return Task.FromResult(false);
            renamed[word] = clean;
            return Task.FromResult(true);
        }, ct);
        if (renamed.Count > 0)
        {
            await using var save = await dbFactory.CreateDbContextAsync(ct);
            await ItemService.RenameKeywordsAsync(save, householdId, renamed, onLocations: true, ct);
            await save.SaveChangesAsync(ct);
        }
        run.Finish();
    }

    // Everything describing things, on items, locations and pinned or colored keywords; not "new" and "unsure".
    private static async Task<List<string>> KeywordsToTranslateAsync(AppDbContext db, Guid householdId, CancellationToken ct)
    {
        var onItems = await db.Items.AsNoTracking().Where(i => i.HouseholdId == householdId && i.Keywords != null && i.Keywords != "")
            .Select(i => i.Keywords).ToListAsync(ct);
        var onLocations = await db.Locations.AsNoTracking().Where(l => l.HouseholdId == householdId && l.Keywords != null)
            .Select(l => l.Keywords).ToListAsync(ct);
        var withSettings = await db.KeywordSettings.AsNoTracking().Where(s => s.HouseholdId == householdId)
            .Select(s => s.Word).ToListAsync(ct);
        return onItems.Concat(onLocations).SelectMany(ItemKeywords.Split).Concat(withSettings)
            .Where(k => !ItemKeywords.IsStatus(k)).Distinct().OrderBy(k => k, StringComparer.Ordinal).ToList();
    }

    internal static string Instructions(AiLanguage language, string what) =>
        $"You translate a home inventory into {language.Name}. These texts are {what}. " +
        "The message is a JSON object of numbered texts. Reply with only a JSON object with the same keys, each text translated, nothing else. " +
        $"A text that's already in {language.Name} stays exactly as it is. Keep brand and model names, numbers and sizes as they are." +
        (language.WritingRule is { } rule ? $" {rule}" : "");

    // The translated texts by key; nothing when the reply isn't such an object (cut off, for one).
    internal static IReadOnlyDictionary<string, string> ParseReply(string reply)
    {
        var (start, end) = (reply.IndexOf('{'), reply.LastIndexOf('}'));
        if (start < 0 || end <= start)
            return new Dictionary<string, string>();
        try
        {
            return (JsonNode.Parse(reply[start..(end + 1)]) as JsonObject ?? [])
                .Where(p => p.Value is JsonValue v && v.TryGetValue<string>(out _))
                .ToDictionary(p => p.Key, p => AiLanguages.WithoutEszett(p.Value!.GetValue<string>()).Trim());
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>();
        }
    }

    // Texts in order, cut so that each batch stays well within what one answer can hold.
    internal static IEnumerable<List<T>> Batches<T>(IEnumerable<T> texts, Func<T, string> textOf)
    {
        var batch = new List<T>();
        var chars = 0;
        foreach (var text in texts)
        {
            var length = textOf(text).Length;
            if (batch.Count > 0 && (batch.Count >= MaxBatchTexts || chars + length > MaxBatchChars))
            {
                yield return batch;
                batch = [];
                chars = 0;
            }
            batch.Add(text);
            chars += length;
        }
        if (batch.Count > 0)
            yield return batch;
    }

    // One household's translation: its progress and how it asks the AI.
    private sealed class Run(AiTranslationService service, Guid householdId, IAiNamer namer, AiLanguage language, int total)
    {
        private int _done;
        private int _failed;
        private string? _error;

        public async Task TranslateAsync<TId>(List<(TId Id, string Text)> texts, string what, Func<TId, string, string, Task<bool>> apply, CancellationToken ct)
        {
            Report(true);
            foreach (var batch in Batches(texts, t => t.Text))
            {
                var numbered = new JsonObject();
                for (var i = 0; i < batch.Count; i++)
                    numbered[(i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)] = batch[i].Text;
                IReadOnlyDictionary<string, string> answers = new Dictionary<string, string>();
                try
                {
                    answers = ParseReply(await namer.AskAsync(Instructions(language, what), numbered.ToJsonString(), ct));
                }
                catch (AiException ex)
                {
                    service.Log(householdId, ex.Message);
                    _error = ex.Message;
                }
                for (var i = 0; i < batch.Count; i++)
                {
                    var key = (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    var answer = answers.GetValueOrDefault(key);
                    if (string.IsNullOrWhiteSpace(answer))
                        _failed++;
                    else if (answer != batch[i].Text && !await apply(batch[i].Id, batch[i].Text, answer))
                        _failed++;
                    _done++;
                }
                Report(true);
            }
        }

        public void Finish() => Report(false);

        private void Report(bool running) =>
            service._status[householdId] = new TranslationStatus(running, _done, total, _failed, language.Name, _error);
    }

    private void Log(Guid householdId, string reason) =>
        logger.LogWarning("Translating household {HouseholdId}: {Reason}", householdId, reason);
}
