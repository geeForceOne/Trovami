using Trovami.Core.Data;
using Trovami.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Trovami.Core.Services;

public sealed record SyncHousehold(Guid Id, string Name, bool IsOwner);

public sealed record SyncLocation(Guid Id, Guid HouseholdId, Guid? ParentId, string Name, LocationKind Kind, string? Color, string? Label, bool Deleted);

public sealed record SyncTag(string Code, Guid HouseholdId, Guid LocationId, bool Deleted);

// What changed since the phone's cursor. Households always come in full: one that's missing is
// one the person left (or that was deleted), and the phone drops everything of it.
public sealed record SyncPull(long Cursor, IReadOnlyList<SyncHousehold> Households, IReadOnlyList<SyncLocation> Locations, IReadOnlyList<SyncTag> Tags);

// What the phone made offline, sent in this order: new locations (made while linking a tag), tag
// links, then items. Photos follow separately, one upload per item.
public sealed record SyncPush(IReadOnlyList<PushLocation>? Locations, IReadOnlyList<PushTag>? Tags, IReadOnlyList<PushItem>? Items);

public sealed record PushLocation(Guid Id, Guid HouseholdId, Guid? ParentId, string Name, LocationKind Kind);

public sealed record PushTag(string Code, Guid LocationId);

public sealed record PushItem(Guid Id, Guid HouseholdId, Guid? LocationId, string? Name, string? Notes, int Quantity, bool HasPhoto);

// Per entry: whether it's stored now (also when it already was), or why it was refused. A refused
// entry won't succeed on a retry, so the phone drops it and says so.
public sealed record PushResult(string Id, bool Ok, string? Error);

public sealed record SyncPushResult(IReadOnlyList<PushResult> Locations, IReadOnlyList<PushResult> Tags, IReadOnlyList<PushResult> Items);

public sealed class SyncService(IDbContextFactory<AppDbContext> dbFactory, LocationService locations, TagService tags, ItemService items)
{
    // knownHouseholds: the households the phone already has. Any other household of the person
    // (one they just joined) comes in full, whatever the cursor.
    public async Task<SyncPull> PullAsync(string userId, long since, IReadOnlyCollection<Guid> knownHouseholds, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var cursor = await db.CurrentVersionAsync(ct);
        var households = await db.HouseholdMembers.AsNoTracking().Where(m => m.UserId == userId)
            .Select(m => new SyncHousehold(m.HouseholdId, m.Household!.Name, m.Household.OwnerId == userId))
            .ToListAsync(ct);
        var ids = households.Select(h => h.Id).ToList();
        var known = ids.Where(knownHouseholds.Contains).ToList();
        var fresh = ids.Except(known).ToList();

        var locationRows = await db.Locations.IgnoreQueryFilters().AsNoTracking()
            .Where(l => (known.Contains(l.HouseholdId) && l.Version > since && l.Version <= cursor)
                        || (fresh.Contains(l.HouseholdId) && l.DeletedAt == null))
            .Select(l => new SyncLocation(l.Id, l.HouseholdId, l.ParentId, l.Name, l.Kind, l.Color, l.Label, l.DeletedAt != null))
            .ToListAsync(ct);
        var tagRows = await db.Tags.IgnoreQueryFilters().AsNoTracking()
            .Where(t => (known.Contains(t.HouseholdId) && t.Version > since && t.Version <= cursor)
                        || (fresh.Contains(t.HouseholdId) && t.DeletedAt == null))
            .Select(t => new SyncTag(t.Code, t.HouseholdId, t.LocationId, t.DeletedAt != null))
            .ToListAsync(ct);
        return new SyncPull(cursor, households, locationRows, tagRows);
    }

    public async Task<SyncPushResult> PushAsync(string userId, SyncPush push, CancellationToken ct = default)
    {
        var locationResults = new List<PushResult>();
        foreach (var l in push.Locations ?? [])
            locationResults.Add(await TryAsync(l.Id.ToString(), () =>
                locations.CreateAsync(userId, l.HouseholdId, l.ParentId, l.Name, l.Kind, l.Id, ct)));

        var tagResults = new List<PushResult>();
        foreach (var t in push.Tags ?? [])
            tagResults.Add(await TryAsync(t.Code, () => tags.LinkAsync(userId, t.Code, t.LocationId, TagKind.Nfc, ct)));

        var itemResults = new List<PushResult>();
        foreach (var i in push.Items ?? [])
            itemResults.Add(await TryAsync(i.Id.ToString(), () =>
                items.CreateAsync(userId, i.HouseholdId, new ItemCreate(i.Id, i.LocationId, i.Name, i.Notes, i.Quantity, i.HasPhoto), ct)));

        return new SyncPushResult(locationResults, tagResults, itemResults);
    }

    private static async Task<PushResult> TryAsync(string id, Func<Task> work)
    {
        try
        {
            await work();
            return new PushResult(id, true, null);
        }
        catch (Exception ex) when (ex is InputException or NotFoundException)
        {
            return new PushResult(id, false, ex.Message);
        }
    }
}
