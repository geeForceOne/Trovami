using Trovami.Core.Data;
using Trovami.Core.Models;
using Trovami.Core.Photos;
using Microsoft.EntityFrameworkCore;

namespace Trovami.Core.Services;

public sealed record HouseholdView(Guid Id, string Name, bool IsOwner, string OwnerName, int Members, int NewKeywordDays);

public sealed record MemberView(string UserId, string Name, string? Email, bool IsOwner, DateTime JoinedAt);

public sealed record InviteView(int Id, Guid HouseholdId, string HouseholdName, string UserName, string? Email, string InvitedBy, DateTime CreatedAt);

// Households and who is in them. Every other service checks membership through Access.
public sealed class HouseholdService(IDbContextFactory<AppDbContext> dbFactory, PhotoStore photos)
{
    public async Task<IReadOnlyList<HouseholdView>> ListAsync(string userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.HouseholdMembers.AsNoTracking()
            .Where(m => m.UserId == userId)
            .Select(m => new
            {
                m.Household!.Id, m.Household.Name, m.Household.OwnerId, m.Household.NewKeywordDays,
                Owner = db.Users.Where(u => u.Id == m.Household.OwnerId).Select(u => new { u.DisplayName, u.Email, u.UserName }).First(),
                Members = m.Household.Members.Count,
            })
            .ToListAsync(ct);
        return rows
            .Select(r => new HouseholdView(r.Id, r.Name, r.OwnerId == userId, AppUser.ShownNameFor(r.Owner.DisplayName, r.Owner.Email, r.Owner.UserName), r.Members, r.NewKeywordDays))
            .OrderBy(h => h.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    // The household the web app shows: the one picked last, else the first. Null when the person
    // isn't in any yet.
    public async Task<HouseholdView?> CurrentAsync(string userId, CancellationToken ct = default)
    {
        var all = await ListAsync(userId, ct);
        if (all.Count == 0)
            return null;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var current = await db.Users.Where(u => u.Id == userId).Select(u => u.CurrentHouseholdId).FirstOrDefaultAsync(ct);
        return all.FirstOrDefault(h => h.Id == current) ?? all[0];
    }

    public async Task SetCurrentAsync(string userId, Guid householdId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await Access.RequireMemberAsync(db, userId, householdId, ct);
        await db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s.SetProperty(u => u.CurrentHouseholdId, householdId), ct);
    }

    public async Task<Guid> CreateAsync(string userId, string name, CancellationToken ct = default)
    {
        var clean = CheckName(name);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var household = new Household { Name = clean, OwnerId = userId };
        household.Members.Add(new HouseholdMember { UserId = userId });
        db.Households.Add(household);
        await db.SaveChangesAsync(ct);
        await db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s.SetProperty(u => u.CurrentHouseholdId, household.Id), ct);
        return household.Id;
    }

    // After how many days "new" comes off an item; 0: only by hand.
    public async Task SetNewKeywordDaysAsync(string userId, Guid householdId, int days, CancellationToken ct = default)
    {
        if (days is < 0 or > Household.MaxNewKeywordDays)
            throw new InputException($"Enter up to {Household.MaxNewKeywordDays} days, or leave it empty to keep \"new\" until it's removed by hand.");
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var household = await Access.RequireOwnerAsync(db, userId, householdId, ct);
        household.NewKeywordDays = days;
        await db.SaveChangesAsync(ct);
    }

    public async Task RenameAsync(string userId, Guid householdId, string name, CancellationToken ct = default)
    {
        var clean = CheckName(name);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var household = await Access.RequireOwnerAsync(db, userId, householdId, ct);
        household.Name = clean;
        await db.SaveChangesAsync(ct);
    }

    // Removes the household with everything in it, photos included.
    public async Task DeleteAsync(string userId, Guid householdId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await Access.RequireOwnerAsync(db, userId, householdId, ct);
        await DeleteContentsAsync(db, householdId, ct);
        await db.Households.Where(h => h.Id == householdId).ExecuteDeleteAsync(ct);
        photos.DeleteHousehold(householdId);
    }

    // Children before parents: locations, items and tags point at locations with RESTRICT, which
    // SQLite checks row by row during a cascade.
    internal static async Task DeleteContentsAsync(AppDbContext db, Guid householdId, CancellationToken ct)
    {
        await db.Tags.IgnoreQueryFilters().Where(t => t.HouseholdId == householdId).ExecuteDeleteAsync(ct);
        await db.Items.Where(i => i.HouseholdId == householdId).ExecuteDeleteAsync(ct);
        await db.Locations.IgnoreQueryFilters().Where(l => l.HouseholdId == householdId)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.ParentId, (Guid?)null), ct);
        await db.Locations.IgnoreQueryFilters().Where(l => l.HouseholdId == householdId).ExecuteDeleteAsync(ct);
    }

    public async Task<IReadOnlyList<MemberView>> MembersAsync(string userId, Guid householdId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var household = await Access.RequireMemberAsync(db, userId, householdId, ct);
        var rows = await db.HouseholdMembers.AsNoTracking().Where(m => m.HouseholdId == householdId)
            .Join(db.Users, m => m.UserId, u => u.Id, (m, u) => new { u.Id, u.DisplayName, u.Email, u.UserName, m.JoinedAt })
            .ToListAsync(ct);
        return rows
            .Select(r => new MemberView(r.Id, AppUser.ShownNameFor(r.DisplayName, r.Email, r.UserName), r.Email, r.Id == household.OwnerId, r.JoinedAt))
            .OrderByDescending(m => m.IsOwner).ThenBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    // Open invites of a household, for its owner.
    public async Task<IReadOnlyList<InviteView>> InvitesAsync(string userId, Guid householdId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await Access.RequireOwnerAsync(db, userId, householdId, ct);
        return await InviteViews(db, db.HouseholdInvites.Where(i => i.HouseholdId == householdId)).ToListAsync(ct);
    }

    // Invites waiting for this person to accept or decline.
    public async Task<IReadOnlyList<InviteView>> InvitesForAsync(string userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await InviteViews(db, db.HouseholdInvites.Where(i => i.UserId == userId)).ToListAsync(ct);
    }

    private static IQueryable<InviteView> InviteViews(AppDbContext db, IQueryable<HouseholdInvite> invites) =>
        from i in invites.AsNoTracking()
        join u in db.Users on i.UserId equals u.Id
        join inviter in db.Users on i.InvitedById equals inviter.Id
        orderby i.CreatedAt
        select new InviteView(i.Id, i.HouseholdId, i.Household!.Name,
            u.DisplayName ?? u.Email ?? u.UserName ?? "?", u.Email, inviter.DisplayName ?? inviter.Email ?? "?", i.CreatedAt);

    // The owner invites someone who already has an account. Returns false when they're already a
    // member or already invited.
    public async Task<bool> InviteAsync(string ownerId, Guid householdId, string inviteeId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await Access.RequireOwnerAsync(db, ownerId, householdId, ct);
        if (await db.HouseholdMembers.AnyAsync(m => m.HouseholdId == householdId && m.UserId == inviteeId, ct)
            || await db.HouseholdInvites.AnyAsync(i => i.HouseholdId == householdId && i.UserId == inviteeId, ct))
            return false;
        db.HouseholdInvites.Add(new HouseholdInvite { HouseholdId = householdId, UserId = inviteeId, InvitedById = ownerId });
        await db.SaveChangesAsync(ct);
        return true;
    }

    // A new account made by the owner's invite joins right away: the account invite is the
    // household invite.
    public async Task AddNewAccountAsync(string ownerId, Guid householdId, string newUserId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await Access.RequireOwnerAsync(db, ownerId, householdId, ct);
        db.HouseholdMembers.Add(new HouseholdMember { HouseholdId = householdId, UserId = newUserId });
        await db.SaveChangesAsync(ct);
        await db.Users.Where(u => u.Id == newUserId).ExecuteUpdateAsync(s => s.SetProperty(u => u.CurrentHouseholdId, householdId), ct);
    }

    public async Task AcceptAsync(string userId, int inviteId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var invite = await db.HouseholdInvites.FirstOrDefaultAsync(i => i.Id == inviteId && i.UserId == userId, ct)
            ?? throw new NotFoundException("That invite no longer exists.");
        if (!await db.HouseholdMembers.AnyAsync(m => m.HouseholdId == invite.HouseholdId && m.UserId == userId, ct))
            db.HouseholdMembers.Add(new HouseholdMember { HouseholdId = invite.HouseholdId, UserId = userId });
        db.HouseholdInvites.Remove(invite);
        await db.SaveChangesAsync(ct);
        await db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s.SetProperty(u => u.CurrentHouseholdId, invite.HouseholdId), ct);
    }

    public async Task DeclineAsync(string userId, int inviteId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.HouseholdInvites.Where(i => i.Id == inviteId && i.UserId == userId).ExecuteDeleteAsync(ct);
    }

    public async Task CancelInviteAsync(string ownerId, int inviteId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var invite = await db.HouseholdInvites.AsNoTracking().FirstOrDefaultAsync(i => i.Id == inviteId, ct);
        if (invite is null)
            return;
        await Access.RequireOwnerAsync(db, ownerId, invite.HouseholdId, ct);
        await db.HouseholdInvites.Where(i => i.Id == inviteId).ExecuteDeleteAsync(ct);
    }

    // The owner removes a member, or a member leaves. The owner can't leave their own household;
    // they delete it instead.
    public async Task RemoveMemberAsync(string userId, Guid householdId, string memberId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var household = memberId == userId
            ? await Access.RequireMemberAsync(db, userId, householdId, ct)
            : await Access.RequireOwnerAsync(db, userId, householdId, ct);
        if (memberId == household.OwnerId)
            throw new InputException("The owner can't leave the household. Delete it instead.");
        await db.HouseholdMembers.Where(m => m.HouseholdId == householdId && m.UserId == memberId).ExecuteDeleteAsync(ct);
    }

    // Before a user is deleted: each household they own goes to its longest-standing other member,
    // or is deleted (with everything in it) when nobody else is in it. Returns how many were deleted.
    public async Task<int> HandOverOrDeleteOwnedAsync(string userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var owned = await db.Households.Where(h => h.OwnerId == userId).ToListAsync(ct);
        var deleted = 0;
        foreach (var household in owned)
        {
            var heir = await db.HouseholdMembers.Where(m => m.HouseholdId == household.Id && m.UserId != userId)
                .OrderBy(m => m.JoinedAt).Select(m => m.UserId).FirstOrDefaultAsync(ct);
            if (heir is not null)
            {
                household.OwnerId = heir;
                await db.SaveChangesAsync(ct);
                continue;
            }
            await DeleteContentsAsync(db, household.Id, ct);
            await db.Households.Where(h => h.Id == household.Id).ExecuteDeleteAsync(ct);
            photos.DeleteHousehold(household.Id);
            deleted++;
        }
        return deleted;
    }

    // What deleting the user would do to their households, for the confirmation: (handed over, deleted).
    public async Task<(int HandedOver, int Deleted)> OwnedSummaryAsync(string userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var owned = await db.Households.Where(h => h.OwnerId == userId)
            .Select(h => h.Members.Any(m => m.UserId != userId)).ToListAsync(ct);
        return (owned.Count(x => x), owned.Count(x => !x));
    }

    // The scheduler's job: invites nobody answered are dropped after a while.
    public async Task<int> RemoveExpiredInvitesAsync(CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow - HouseholdInvite.KeepFor;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.HouseholdInvites.Where(i => i.CreatedAt < cutoff).ExecuteDeleteAsync(ct);
    }

    private static string CheckName(string? name)
    {
        var clean = (name ?? "").Trim();
        if (clean.Length == 0)
            throw new InputException("Enter a name for the household.");
        if (clean.Length > Household.MaxNameLength)
            throw new InputException($"Keep the name under {Household.MaxNameLength} characters.");
        return clean;
    }
}

// Membership checks shared by the services. A household the person isn't in looks the same as one
// that doesn't exist.
public static class Access
{
    public static async Task<Household> RequireMemberAsync(AppDbContext db, string userId, Guid householdId, CancellationToken ct)
    {
        var household = await db.Households.FirstOrDefaultAsync(h => h.Id == householdId && h.Members.Any(m => m.UserId == userId), ct);
        return household ?? throw new NotFoundException("That household no longer exists, or you're not in it anymore.");
    }

    public static async Task<Household> RequireOwnerAsync(AppDbContext db, string userId, Guid householdId, CancellationToken ct)
    {
        var household = await RequireMemberAsync(db, userId, householdId, ct);
        return household.OwnerId == userId ? household : throw new InputException("Only the household's owner can do that.");
    }

    public static Task<bool> IsMemberAsync(AppDbContext db, string userId, Guid householdId, CancellationToken ct) =>
        db.HouseholdMembers.AnyAsync(m => m.HouseholdId == householdId && m.UserId == userId, ct);
}
