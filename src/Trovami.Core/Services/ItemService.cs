using Trovami.Core.Ai;
using Trovami.Core.Data;
using Trovami.Core.Models;
using Trovami.Core.Photos;
using Microsoft.EntityFrameworkCore;

namespace Trovami.Core.Services;

public sealed record ItemView(
    Guid Id, string ShortCode, Guid HouseholdId, string? Name, NameSource NameSource, AiNaming AiNaming, string? Notes, int Quantity,
    Guid? LocationId, string Path, string? PhotoVersion, bool PhotoExpected, DateTime CreatedAt, IReadOnlyList<string> Keywords)
{
    public const string UnnamedText = "Unnamed item";

    public string ShownName => Name ?? UnnamedText;

    // Waiting for the AI to name it from its photo.
    public bool Naming => Name is null && NameSource != NameSource.Person && AiNaming == AiNaming.Pending;
}

// What the phone (or the web app's add form) sends for a new item. Id is chosen by the client.
public sealed record ItemCreate(Guid Id, Guid? LocationId, string? Name, string? Notes, int Quantity = 1, bool PhotoExpected = false);

public sealed record KeywordCount(string Keyword, int Items);

public sealed record SearchResult(IReadOnlyList<ItemView> Items, IReadOnlyList<LocationNode> Locations, LocationTree Tree);

public sealed class ItemService(IDbContextFactory<AppDbContext> dbFactory, PhotoStore photos, AiNamingQueue naming)
{
    public const int MaxSearchResults = 200;

    public async Task<IReadOnlyList<ItemView>> AtLocationAsync(string userId, Guid householdId, Guid? locationId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await Access.RequireMemberAsync(db, userId, householdId, ct);
        var tree = await LocationService.LoadTreeAsync(db, householdId, ct);
        var rows = await db.Items.AsNoTracking()
            .Where(i => i.HouseholdId == householdId && i.LocationId == locationId)
            .ToListAsync(ct);
        return rows.Select(i => View(i, tree))
            .OrderBy(i => i.Name is null ? 0 : 1).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenByDescending(i => i.CreatedAt).ToList();
    }

    // Everything in a location and in all the locations below it, in no particular order.
    public async Task<IReadOnlyList<ItemView>> BelowAsync(string userId, Guid householdId, Guid locationId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await Access.RequireMemberAsync(db, userId, householdId, ct);
        var tree = await LocationService.LoadTreeAsync(db, householdId, ct);
        var ids = tree.Subtree(locationId).Select(n => (Guid?)n.Id).ToList();
        var rows = await db.Items.AsNoTracking()
            .Where(i => i.HouseholdId == householdId && ids.Contains(i.LocationId))
            .ToListAsync(ct);
        return rows.Select(i => View(i, tree)).ToList();
    }

    // The Inbox: items without a location, newest first.
    public async Task<IReadOnlyList<ItemView>> InboxAsync(string userId, Guid householdId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await Access.RequireMemberAsync(db, userId, householdId, ct);
        var tree = new LocationTree([]);
        var rows = await db.Items.AsNoTracking()
            .Where(i => i.HouseholdId == householdId && i.LocationId == null)
            .OrderByDescending(i => i.CreatedAt).ToListAsync(ct);
        return rows.Select(i => View(i, tree)).ToList();
    }

    public async Task<int> InboxCountAsync(string userId, Guid householdId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await Access.RequireMemberAsync(db, userId, householdId, ct);
        return await db.Items.CountAsync(i => i.HouseholdId == householdId && i.LocationId == null, ct);
    }

    public async Task<ItemView> GetAsync(string userId, Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var item = await LoadAsync(db, userId, id, ct);
        return View(item, await LocationService.LoadTreeAsync(db, item.HouseholdId, ct));
    }

    // By the short code in the web app's address.
    public async Task<ItemView> GetByCodeAsync(string userId, string code, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var clean = ShortCodes.Normalize(code);
        var id = await db.Items.Where(i => i.ShortCode == clean).Select(i => (Guid?)i.Id).FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException("That item no longer exists.");
        var item = await LoadAsync(db, userId, id, ct);
        return View(item, await LocationService.LoadTreeAsync(db, item.HouseholdId, ct));
    }

    // Creating the same id twice returns false and changes nothing, so the phone can repeat an
    // upload that may or may not have arrived.
    public async Task<bool> CreateAsync(string userId, Guid householdId, ItemCreate create, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await Access.RequireMemberAsync(db, userId, householdId, ct);
        if (await db.Items.AnyAsync(i => i.Id == create.Id, ct))
        {
            if (await db.Items.AnyAsync(i => i.Id == create.Id && i.HouseholdId == householdId, ct))
                return false;
            throw new InputException("That item id is already taken.");
        }

        // A location deleted in the meantime (on the web while the phone was offline): the Inbox.
        var locationId = create.LocationId is { } l && await db.Locations.AnyAsync(x => x.Id == l && x.HouseholdId == householdId, ct)
            ? l : (Guid?)null;
        var name = CleanName(create.Name);
        db.Items.Add(new Item
        {
            Id = create.Id,
            ShortCode = await ShortCodes.NewUniqueAsync(c => db.Items.AnyAsync(i => i.ShortCode == c, ct)),
            HouseholdId = householdId,
            LocationId = locationId,
            Name = name,
            NameSource = name is null ? NameSource.None : NameSource.Person,
            // Waits for the photo; named (or just given keywords) as soon as it arrives.
            AiNaming = create.PhotoExpected ? AiNaming.Pending : AiNaming.NotNeeded,
            Notes = CleanNotes(create.Notes),
            Quantity = CheckQuantity(create.Quantity),
            PhotoExpected = create.PhotoExpected,
            CreatedById = userId,
        });
        await db.SaveChangesAsync(ct);
        return true;
    }

    // A name typed by a person is final: the AI never overwrites it. Clearing the name makes it an
    // "Unnamed item" again, and the AI doesn't step in then either (the person chose no name).
    // Keywords edited by a person are final the same way; null leaves them as they are.
    public async Task UpdateAsync(string userId, Guid id, string? name, string? notes, int quantity,
        IReadOnlyList<string>? keywords = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var item = await LoadAsync(db, userId, id, ct);
        var clean = CleanName(name);
        if (clean != item.Name)
        {
            item.Name = clean;
            item.NameSource = NameSource.Person;
        }
        if (keywords is not null && ItemKeywords.Join(keywords) is var joined && joined != item.Keywords)
            item.Keywords = joined;
        if (item.AiNaming == AiNaming.Pending && !AiNamingService.NeedsAi(item))
            item.AiNaming = AiNaming.NotNeeded;
        item.Notes = CleanNotes(notes);
        item.Quantity = CheckQuantity(quantity);
        item.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task RenameAsync(string userId, Guid id, string? name, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var item = await LoadAsync(db, userId, id, ct);
        item.Name = CleanName(name);
        item.NameSource = NameSource.Person;
        if (item.AiNaming == AiNaming.Pending && !AiNamingService.NeedsAi(item))
            item.AiNaming = AiNaming.NotNeeded;
        item.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    // Moves several items at once (the Inbox's multi-select); locationId null: to the Inbox.
    public async Task MoveAsync(string userId, IReadOnlyCollection<Guid> ids, Guid? locationId, CancellationToken ct = default)
    {
        if (ids.Count == 0)
            return;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var items = await db.Items.Where(i => ids.Contains(i.Id)).ToListAsync(ct);
        foreach (var householdId in items.Select(i => i.HouseholdId).Distinct())
        {
            await Access.RequireMemberAsync(db, userId, householdId, ct);
            if (locationId is { } l && !await db.Locations.AnyAsync(x => x.Id == l && x.HouseholdId == householdId, ct))
                throw new NotFoundException("That location no longer exists.");
        }
        var now = DateTime.UtcNow;
        foreach (var item in items)
        {
            item.LocationId = locationId;
            item.UpdatedAt = now;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(string userId, IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var items = await db.Items.Where(i => ids.Contains(i.Id)).ToListAsync(ct);
        foreach (var householdId in items.Select(i => i.HouseholdId).Distinct())
            await Access.RequireMemberAsync(db, userId, householdId, ct);
        db.Items.RemoveRange(items);
        await db.SaveChangesAsync(ct);
        foreach (var item in items)
            photos.Delete(item.HouseholdId, item.Id);
    }

    // Stores the item's photo. Without a name (and none typed by a person) or keywords, the AI
    // looks at it next.
    public async Task SetPhotoAsync(string userId, Guid id, Stream photo, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var item = await LoadAsync(db, userId, id, ct);
        item.PhotoVersion = await photos.SaveAsync(item.HouseholdId, id, photo, ct);
        item.PhotoExpected = false;
        if (AiNamingService.NeedsAi(item))
            item.AiNaming = AiNaming.Pending;
        item.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        if (item.AiNaming == AiNaming.Pending)
            naming.Enqueue(item.Id);
    }

    public async Task RemovePhotoAsync(string userId, Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var item = await LoadAsync(db, userId, id, ct);
        item.PhotoVersion = null;
        item.PhotoExpected = false;
        if (item.AiNaming == AiNaming.Pending)
            item.AiNaming = AiNaming.NotNeeded;
        item.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        photos.Delete(item.HouseholdId, id);
    }

    // Asks the AI again, e.g. after the owner entered a key.
    public async Task RetryNamingAsync(string userId, Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var item = await LoadAsync(db, userId, id, ct);
        if (item.PhotoVersion is null)
            throw new InputException("Add a photo first; the AI names items from their photo.");
        item.Name = null;
        item.NameSource = NameSource.None;
        item.AiNaming = AiNaming.Pending;
        item.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        naming.Enqueue(item.Id);
    }

    // One search over the household: item names, notes and keywords, location names, labels and notes.
    // Every word has to match somewhere (in the item or along its path), in any order.
    public async Task<SearchResult> SearchAsync(string userId, Guid householdId, string query, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await Access.RequireMemberAsync(db, userId, householdId, ct);
        var tree = await LocationService.LoadTreeAsync(db, householdId, ct);
        var words = Search.Words(query);
        if (words.Count == 0)
            return new SearchResult([], [], tree);

        // A location matches on its own name, label and notes; an item on its own text or that of
        // any location it's in ("cellar drill" finds the drill in the cellar).
        var locationText = tree.All.ToDictionary(n => n.Id, n => Search.Normalize($"{n.Name} {n.Label} {n.Notes}"));
        string PathText(Guid? id) => id is { } i
            ? string.Join(' ', tree.Ancestry(i).Select(n => locationText[n.Id]))
            : "inbox";

        var locations = tree.All.Where(n => words.All(w => locationText[n.Id].Contains(w) || PathText(n.ParentId).Contains(w)))
            .Where(n => words.Any(w => locationText[n.Id].Contains(w)))
            .OrderBy(n => tree.PathOf(n.Id), StringComparer.CurrentCultureIgnoreCase)
            .Take(MaxSearchResults).ToList();

        var all = await db.Items.AsNoTracking().Where(i => i.HouseholdId == householdId).ToListAsync(ct);
        var items = all
            .Select(i => (Item: i, Own: Search.Normalize($"{i.Name ?? ItemView.UnnamedText} {i.Notes} {i.Keywords}"), Path: PathText(i.LocationId)))
            .Where(x => words.All(w => x.Own.Contains(w) || x.Path.Contains(w)) && words.Any(w => x.Own.Contains(w)))
            .Select(x => View(x.Item, tree))
            .OrderBy(i => i.ShownName, StringComparer.CurrentCultureIgnoreCase)
            .Take(MaxSearchResults).ToList();
        return new SearchResult(items, locations, tree);
    }

    // Every keyword in the household with how many items have it, A to Z.
    public async Task<IReadOnlyList<KeywordCount>> KeywordsAsync(string userId, Guid householdId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await Access.RequireMemberAsync(db, userId, householdId, ct);
        var stored = await db.Items.AsNoTracking()
            .Where(i => i.HouseholdId == householdId && i.Keywords != null && i.Keywords != "")
            .Select(i => i.Keywords).ToListAsync(ct);
        return stored.SelectMany(ItemKeywords.Split)
            .GroupBy(k => k)
            .Select(g => new KeywordCount(g.Key, g.Count()))
            .OrderBy(k => k.Keyword, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public async Task<IReadOnlyList<ItemView>> WithKeywordAsync(string userId, Guid householdId, string keyword, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await Access.RequireMemberAsync(db, userId, householdId, ct);
        var tree = await LocationService.LoadTreeAsync(db, householdId, ct);
        return (await HavingKeywordAsync(db, householdId, keyword, ct)).Select(i => View(i, tree)).ToList();
    }

    // Renames a keyword on every item that has it; renaming it to one that exists merges the two.
    // An empty newKeyword removes it. Returns how many items changed. Counts as a person's edit, so
    // the AI leaves those items' keywords alone afterwards.
    public async Task<int> ReplaceKeywordAsync(string userId, Guid householdId, string keyword, string? newKeyword, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await Access.RequireMemberAsync(db, userId, householdId, ct);
        var word = ItemKeywords.Clean([keyword]).FirstOrDefault() ?? "";
        var replacement = ItemKeywords.Clean([newKeyword ?? ""]).FirstOrDefault();
        var items = await HavingKeywordAsync(db, householdId, word, ct);
        var now = DateTime.UtcNow;
        foreach (var item in items)
        {
            var words = ItemKeywords.Split(item.Keywords).Select(k => k == word ? replacement : k).OfType<string>();
            item.Keywords = ItemKeywords.Join(words);
            item.UpdatedAt = now;
        }
        await db.SaveChangesAsync(ct);
        return items.Count;
    }

    private static async Task<List<Item>> HavingKeywordAsync(AppDbContext db, Guid householdId, string keyword, CancellationToken ct)
    {
        var word = ItemKeywords.Clean([keyword]).FirstOrDefault() ?? "";
        // LIKE narrows it down in the database; the exact match is checked here.
        var candidates = await db.Items
            .Where(i => i.HouseholdId == householdId && i.Keywords != null && EF.Functions.Like(i.Keywords, "%" + word + "%"))
            .ToListAsync(ct);
        return candidates.Where(i => ItemKeywords.Split(i.Keywords).Contains(word)).ToList();
    }

    private static async Task<Item> LoadAsync(AppDbContext db, string userId, Guid id, CancellationToken ct)
    {
        var item = await db.Items.FirstOrDefaultAsync(i => i.Id == id, ct)
            ?? throw new NotFoundException("That item no longer exists.");
        await Access.RequireMemberAsync(db, userId, item.HouseholdId, ct);
        return item;
    }

    internal static ItemView View(Item i, LocationTree tree) =>
        new(i.Id, i.ShortCode, i.HouseholdId, i.Name, i.NameSource, i.AiNaming, i.Notes, i.Quantity, i.LocationId, tree.PathOf(i.LocationId),
            i.PhotoVersion, i.PhotoExpected, i.CreatedAt, ItemKeywords.Split(i.Keywords));

    private static string? CleanName(string? name)
    {
        var clean = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        if (clean is { Length: > Item.MaxNameLength })
            throw new InputException($"Keep the name under {Item.MaxNameLength} characters.");
        return clean;
    }

    private static string? CleanNotes(string? notes)
    {
        var clean = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        if (clean is { Length: > Item.MaxNotesLength })
            throw new InputException($"Keep the notes under {Item.MaxNotesLength} characters.");
        return clean;
    }

    private static int CheckQuantity(int quantity) =>
        quantity is >= 1 and <= Item.MaxQuantity ? quantity : throw new InputException("The quantity must be at least 1.");
}

// An item's keywords: lower case, trimmed, no doubles, stored as "a, b, c".
public static class ItemKeywords
{
    public static IReadOnlyList<string> Clean(IEnumerable<string> words) => words
        .Select(w => string.Join(' ', w.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim('"', '.', ' ').ToLowerInvariant())
        .Where(w => w.Length > 0)
        .Select(w => w.Length > Item.MaxKeywordLength ? w[..Item.MaxKeywordLength].Trim() : w)
        .Distinct()
        .Take(Item.MaxKeywords)
        .ToList();

    public static IReadOnlyList<string> Split(string? stored) =>
        string.IsNullOrEmpty(stored) ? [] : stored.Split(", ", StringSplitOptions.RemoveEmptyEntries);

    public static string Join(IEnumerable<string> words) => string.Join(", ", Clean(words));
}

// Search text: lower case, accents removed, so "schlussel" finds "Schlüssel" and "cafe" finds "Café".
public static class Search
{
    public static string Normalize(string text)
    {
        var decomposed = text.ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD);
        var chars = decomposed.Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark);
        return new string(chars.ToArray()).Replace("ß", "ss");
    }

    public static IReadOnlyList<string> Words(string? query) =>
        Normalize(query ?? "").Split([' ', ',', '\t'], StringSplitOptions.RemoveEmptyEntries).Distinct().ToList();
}
