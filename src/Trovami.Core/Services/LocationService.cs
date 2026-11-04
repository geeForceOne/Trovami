using Trovami.Core.Data;
using Trovami.Core.Models;
using Trovami.Core.Photos;
using Microsoft.EntityFrameworkCore;

namespace Trovami.Core.Services;

// One location as the tree shows it. Items counts only what's directly in it.
public sealed record LocationNode(
    Guid Id, string ShortCode, Guid? ParentId, string Name, LocationKind Kind, string? Color, string? Label, string? Notes,
    string? PhotoVersion, int Items, int Tags, IReadOnlyList<string> Keywords);

public sealed record LocationUpdate(string Name, LocationKind Kind, string? Notes, string? Color, string? Label, IReadOnlyList<string> Keywords);

// The tree of a household's locations. Moving a location moves everything under it, since items
// and sub-locations only point at their parent.
public sealed class LocationService(IDbContextFactory<AppDbContext> dbFactory, PhotoStore photos)
{
    // Every location of the household, flat; LocationTree turns it into paths and children.
    public async Task<LocationTree> TreeAsync(string userId, Guid householdId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await Access.RequireMemberAsync(db, userId, householdId, ct);
        return await LoadTreeAsync(db, householdId, ct);
    }

    internal static async Task<LocationTree> LoadTreeAsync(AppDbContext db, Guid householdId, CancellationToken ct)
    {
        var items = await db.Items.Where(i => i.HouseholdId == householdId && i.LocationId != null)
            .GroupBy(i => i.LocationId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key!.Value, x => x.Count, ct);
        var tags = await db.Tags.Where(t => t.HouseholdId == householdId)
            .GroupBy(t => t.LocationId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var rows = await db.Locations.AsNoTracking().Where(l => l.HouseholdId == householdId).ToListAsync(ct);
        return new LocationTree(rows.Select(l => new LocationNode(
            l.Id, l.ShortCode, l.ParentId, l.Name, l.Kind, l.Color, l.Label, l.Notes, l.PhotoVersion,
            items.GetValueOrDefault(l.Id), tags.GetValueOrDefault(l.Id), ItemKeywords.Split(l.Keywords))));
    }

    // id: chosen by the client (the phone), or a new one. Creating the same id twice returns the
    // existing location, so a repeated upload is harmless.
    public async Task<Guid> CreateAsync(string userId, Guid householdId, Guid? parentId, string name, LocationKind kind, Guid? id = null, CancellationToken ct = default)
    {
        var clean = CheckName(name);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await Access.RequireMemberAsync(db, userId, householdId, ct);
        if (id is { } given && await db.Locations.IgnoreQueryFilters().AnyAsync(l => l.Id == given, ct))
        {
            if (await db.Locations.IgnoreQueryFilters().AnyAsync(l => l.Id == given && l.HouseholdId == householdId, ct))
                return given;
            throw new InputException("That location id is already taken.");
        }
        if (parentId is { } p && !await db.Locations.AnyAsync(l => l.Id == p && l.HouseholdId == householdId, ct))
            throw new NotFoundException("The location to add it to no longer exists.");
        // Deleted locations keep their code, so links to them don't point somewhere else later.
        var code = await ShortCodes.NewUniqueAsync(c => db.Locations.IgnoreQueryFilters().AnyAsync(l => l.ShortCode == c, ct));
        var location = new Location { Id = id ?? Guid.NewGuid(), ShortCode = code, HouseholdId = householdId, ParentId = parentId, Name = clean, Kind = kind };
        db.Locations.Add(location);
        await db.SaveChangesAsync(ct);
        return location.Id;
    }

    public async Task UpdateAsync(string userId, Guid id, LocationUpdate update, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var location = await LoadAsync(db, userId, id, ct);
        location.Name = CheckName(update.Name);
        location.Kind = update.Kind;
        location.Notes = Clean(update.Notes, Location.MaxNotesLength, "notes");
        location.Color = LocationColors.IsKnown(update.Color) ? update.Color : null;
        location.Label = Clean(update.Label, Location.MaxLabelLength, "label");
        location.Keywords = ItemKeywords.Merge(null, update.Keywords);
        await db.SaveChangesAsync(ct);
    }

    // Only the keywords, for the editor that saves them as they change.
    public async Task SetKeywordsAsync(string userId, Guid id, IReadOnlyList<string> keywords, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var location = await LoadAsync(db, userId, id, ct);
        location.Keywords = ItemKeywords.Merge(null, keywords);
        await db.SaveChangesAsync(ct);
    }

    public async Task RenameAsync(string userId, Guid id, string name, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var location = await LoadAsync(db, userId, id, ct);
        location.Name = CheckName(name);
        await db.SaveChangesAsync(ct);
    }

    // newParentId null: to the top level. Can't go into itself or anything under it.
    public async Task MoveAsync(string userId, Guid id, Guid? newParentId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var location = await LoadAsync(db, userId, id, ct);
        if (location.ParentId == newParentId)
            return;
        if (newParentId is { } target)
        {
            var tree = await LoadTreeAsync(db, location.HouseholdId, ct);
            if (!tree.Contains(target))
                throw new NotFoundException("The location to move it to no longer exists.");
            if (target == id || tree.IsUnder(target, id))
                throw new InputException("A location can't go into itself or into something inside it.");
        }
        location.ParentId = newParentId;
        await db.SaveChangesAsync(ct);
    }

    // An empty location is deleted right away. One with contents only when moveContents is set:
    // its sub-locations and items go to its parent (items at the top level go to the Inbox).
    // Its tags are unlinked either way.
    public async Task DeleteAsync(string userId, Guid id, bool moveContents, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var location = await LoadAsync(db, userId, id, ct);
        var children = await db.Locations.Where(l => l.ParentId == id).ToListAsync(ct);
        var items = await db.Items.Where(i => i.LocationId == id).ToListAsync(ct);
        if ((children.Count > 0 || items.Count > 0) && !moveContents)
            throw new InputException("This location isn't empty. Move what's in it first, or move it all up one level.");

        var added = items.Count > 0 ? (await LoadTreeAsync(db, location.HouseholdId, ct)).KeywordsFor(location.ParentId) : [];
        var now = DateTime.UtcNow;
        foreach (var child in children)
            child.ParentId = location.ParentId;
        foreach (var item in items)
        {
            item.LocationId = location.ParentId;
            item.Keywords = ItemKeywords.Merge(item.Keywords, added);
            item.UpdatedAt = now;
        }
        foreach (var tag in await db.Tags.Where(t => t.LocationId == id).ToListAsync(ct))
            tag.DeletedAt = now;
        location.DeletedAt = now;
        location.PhotoVersion = null;
        await db.SaveChangesAsync(ct);
        photos.Delete(location.HouseholdId, id);
    }

    public async Task SetPhotoAsync(string userId, Guid id, Stream photo, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var location = await LoadAsync(db, userId, id, ct);
        location.PhotoVersion = await photos.SaveAsync(location.HouseholdId, id, photo, ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task RemovePhotoAsync(string userId, Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var location = await LoadAsync(db, userId, id, ct);
        location.PhotoVersion = null;
        await db.SaveChangesAsync(ct);
        photos.Delete(location.HouseholdId, id);
    }

    private static async Task<Location> LoadAsync(AppDbContext db, string userId, Guid id, CancellationToken ct)
    {
        var location = await db.Locations.FirstOrDefaultAsync(l => l.Id == id, ct)
            ?? throw new NotFoundException("That location no longer exists.");
        await Access.RequireMemberAsync(db, userId, location.HouseholdId, ct);
        return location;
    }

    internal static string CheckName(string? name)
    {
        var clean = (name ?? "").Trim();
        if (clean.Length == 0)
            throw new InputException("Enter a name for the location.");
        if (clean.Length > Location.MaxNameLength)
            throw new InputException($"Keep the name under {Location.MaxNameLength} characters.");
        return clean;
    }

    private static string? Clean(string? text, int max, string what)
    {
        var clean = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        if (clean is { Length: var n } && n > max)
            throw new InputException($"Keep the {what} under {max} characters.");
        return clean;
    }
}

// A household's locations with their parent links resolved: children, paths, and what's under
// what. Locations whose parent is missing count as top level, so a broken link never hides one.
public sealed class LocationTree
{
    public const string Separator = " › ";

    private readonly Dictionary<Guid, LocationNode> _byId;
    private readonly Dictionary<string, LocationNode> _byCode;
    private readonly ILookup<Guid?, LocationNode> _children;

    public LocationTree(IEnumerable<LocationNode> nodes)
    {
        _byId = nodes.ToDictionary(n => n.Id);
        _byCode = _byId.Values.Where(n => n.ShortCode.Length > 0).ToDictionary(n => n.ShortCode);
        _children = _byId.Values
            .OrderBy(n => n.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToLookup(n => n.ParentId is { } p && _byId.ContainsKey(p) ? p : (Guid?)null);
    }

    public int Count => _byId.Count;

    public IEnumerable<LocationNode> All => _byId.Values;

    public bool Contains(Guid id) => _byId.ContainsKey(id);

    public LocationNode? Find(Guid? id) => id is { } i && _byId.TryGetValue(i, out var n) ? n : null;

    public LocationNode? FindByCode(string? code) =>
        ShortCodes.Normalize(code) is { } c && _byCode.TryGetValue(c, out var n) ? n : null;

    // Sorted by name; null: the top level.
    public IEnumerable<LocationNode> ChildrenOf(Guid? id) => _children[id];

    // From the top down to the location itself.
    public IReadOnlyList<LocationNode> Ancestry(Guid id)
    {
        var chain = new List<LocationNode>();
        var seen = new HashSet<Guid>();
        for (var node = Find(id); node is not null && seen.Add(node.Id); node = Find(node.ParentId))
            chain.Add(node);
        chain.Reverse();
        return chain;
    }

    // "House › Cellar › Rack 2 › Blue box"; the Inbox for null.
    public string PathOf(Guid? id) =>
        id is { } i && Contains(i) ? string.Join(Separator, Ancestry(i).Select(n => n.Name)) : "Inbox";

    // Whether `id` is somewhere below `ancestor`.
    public bool IsUnder(Guid id, Guid ancestor) =>
        Ancestry(id).Any(n => n.Id == ancestor && n.Id != id);

    // The location and everything below it.
    public IEnumerable<LocationNode> Subtree(Guid id)
    {
        if (Find(id) is not { } root)
            yield break;
        var stack = new Stack<LocationNode>([root]);
        var seen = new HashSet<Guid>();
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (!seen.Add(node.Id))
                continue;
            yield return node;
            foreach (var child in ChildrenOf(node.Id))
                stack.Push(child);
        }
    }

    // What an item arriving at the location gets: the keywords of the location and all above it.
    public IReadOnlyList<string> KeywordsFor(Guid? id) =>
        id is { } i ? Ancestry(i).SelectMany(n => n.Keywords).Distinct().ToList() : [];

    // How many items are in the location and everything below it.
    public int ItemsBelow(Guid id) => Subtree(id).Sum(n => n.Items);
}
