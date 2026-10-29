using System.Security.Cryptography;
using Trovami.Core.Data;
using Trovami.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Trovami.Core.Services;

public sealed record TagView(string Code, TagKind Kind, Guid LocationId, DateTime CreatedAt);

// A label on the print sheet: the code for the QR image, and what's printed under it.
public sealed record PrintLabel(string Code, string Uri, string Name, string? Label, string Path);

// NFC tags and QR labels. Both carry trovami://t/<code>; the code is random, so a tag says nothing
// about what it's linked to.
public sealed class TagService(IDbContextFactory<AppDbContext> dbFactory)
{
    public const string UriPrefix = "trovami://t/";

    // Easy to tell apart on paper: no 0/O, 1/I/L.
    private const string Alphabet = "23456789ABCDEFGHJKMNPQRSTUVWXYZ";

    public static string NewCode() =>
        new(Enumerable.Range(0, Tag.CodeLength).Select(_ => Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]).ToArray());

    public static string UriOf(string code) => UriPrefix + code;

    // The code in a scanned trovami://t/<code>, or null when it isn't one of Trovami's.
    public static string? CodeFromUri(string? uri)
    {
        if (uri is null || !uri.StartsWith(UriPrefix, StringComparison.OrdinalIgnoreCase))
            return null;
        var code = uri[UriPrefix.Length..].Trim().TrimEnd('/').ToUpperInvariant();
        return IsValidCode(code) ? code : null;
    }

    public static bool IsValidCode(string? code) =>
        code is { Length: >= 6 and <= 32 } && code.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    public async Task<IReadOnlyList<TagView>> ForLocationAsync(string userId, Guid locationId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var location = await db.Locations.AsNoTracking().FirstOrDefaultAsync(l => l.Id == locationId, ct)
            ?? throw new NotFoundException("That location no longer exists.");
        await Access.RequireMemberAsync(db, userId, location.HouseholdId, ct);
        return await db.Tags.AsNoTracking().Where(t => t.LocationId == locationId)
            .OrderBy(t => t.CreatedAt).Select(t => new TagView(t.Code, t.Kind, t.LocationId, t.CreatedAt)).ToListAsync(ct);
    }

    // Links a tag to a location: a new code, or one that's already linked somewhere (re-linking).
    // A code from another household is refused.
    public async Task LinkAsync(string userId, string code, Guid locationId, TagKind kind, CancellationToken ct = default)
    {
        code = code.Trim().ToUpperInvariant();
        if (!IsValidCode(code))
            throw new InputException("That isn't a Trovami tag.");
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var location = await db.Locations.FirstOrDefaultAsync(l => l.Id == locationId, ct)
            ?? throw new NotFoundException("That location no longer exists.");
        await Access.RequireMemberAsync(db, userId, location.HouseholdId, ct);

        var tag = await db.Tags.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.Code == code, ct);
        if (tag is null)
            db.Tags.Add(new Tag { Code = code, HouseholdId = location.HouseholdId, LocationId = locationId, Kind = kind });
        else if (tag.HouseholdId != location.HouseholdId)
            throw new InputException("That tag belongs to another household.");
        else
        {
            tag.LocationId = locationId;
            tag.DeletedAt = null;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task UnlinkAsync(string userId, string code, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var tag = await db.Tags.FirstOrDefaultAsync(t => t.Code == code, ct);
        if (tag is null)
            return;
        await Access.RequireMemberAsync(db, userId, tag.HouseholdId, ct);
        tag.DeletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    // The location a scanned code points to, if it's in one of the person's households.
    public async Task<(Guid HouseholdId, Guid LocationId)?> ResolveAsync(string userId, string code, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var tag = await db.Tags.AsNoTracking().FirstOrDefaultAsync(t => t.Code == code.Trim().ToUpperInvariant(), ct);
        if (tag is null || !await Access.IsMemberAsync(db, userId, tag.HouseholdId, ct))
            return null;
        return (tag.HouseholdId, tag.LocationId);
    }

    // QR labels for the print sheet: each chosen location gets a new QR code, unless it already
    // has one (printing again reuses it, so old labels keep working).
    public async Task<IReadOnlyList<PrintLabel>> LabelsForPrintAsync(string userId, Guid householdId, IReadOnlyCollection<Guid> locationIds, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await Access.RequireMemberAsync(db, userId, householdId, ct);
        var tree = await LocationService.LoadTreeAsync(db, householdId, ct);
        var existing = await db.Tags.Where(t => t.HouseholdId == householdId && t.Kind == TagKind.Qr && locationIds.Contains(t.LocationId))
            .ToListAsync(ct);
        var labels = new List<PrintLabel>();
        foreach (var id in locationIds.Where(tree.Contains))
        {
            var code = existing.Where(t => t.LocationId == id).OrderBy(t => t.CreatedAt).Select(t => t.Code).FirstOrDefault();
            if (code is null)
            {
                code = NewCode();
                db.Tags.Add(new Tag { Code = code, HouseholdId = householdId, LocationId = id, Kind = TagKind.Qr });
            }
            var node = tree.Find(id)!;
            labels.Add(new PrintLabel(code, UriOf(code), node.Name, node.Label, node.ParentId is null ? "" : tree.PathOf(node.ParentId)));
        }
        await db.SaveChangesAsync(ct);
        return labels;
    }
}
