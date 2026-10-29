using Trovami.Core;
using Trovami.Core.Models;
using Trovami.Core.Services;
using Microsoft.AspNetCore.Identity;

namespace Trovami.Web.Services;

// What happened to a household invite, in words for the owner. Link is set when an account
// invite couldn't be emailed and has to be passed on by hand.
public sealed record HouseholdInviteResult(string Message, string? Link);

// Inviting someone into a household by email: an address with an account gets a household invite
// to accept, a new address gets an account (unless the admin turned that off) and joins right away.
public sealed class HouseholdInviteService(
    IServiceScopeFactory scopes, HouseholdService households, UserAdminService users, AccountMailService mail, AccountRequestService site)
{
    // name: only needed for a new account.
    public async Task<HouseholdInviteResult> InviteAsync(string ownerId, Guid householdId, string email, string? name, string requestBaseUrl, CancellationToken ct = default)
    {
        email = email.Trim();
        var household = (await households.ListAsync(ownerId, ct)).FirstOrDefault(h => h.Id == householdId && h.IsOwner)
            ?? throw new InputException("Only the household's owner can invite people.");
        var inviter = (await users.FindAsync(ownerId))?.ShownName ?? "Someone";

        AppUser? existing;
        await using (var scope = scopes.CreateAsyncScope())
            existing = await UserAdminService.FindByLoginAsync(scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>(), email);

        if (existing is not null)
        {
            if (!await households.InviteAsync(ownerId, householdId, existing.Id, ct))
                return new($"{existing.ShownName} is already in \"{household.Name}\" or already invited.", null);
            await mail.NotifyHouseholdInviteAsync(existing.Id, household.Name, inviter, requestBaseUrl);
            return new($"{existing.ShownName} is invited. They accept the invite in {AppInfo.Name}.", null);
        }

        if (!await site.InvitesCreateAccountsAsync(ct))
            throw new InputException($"{email} has no {AppInfo.Name} account, and on this server only the administrator creates accounts. Ask them to create one first.");
        if (string.IsNullOrWhiteSpace(name))
            throw new InputException($"{email} has no {AppInfo.Name} account yet. Enter their name too, and they get an account with the invite.");

        var (userId, errors) = await users.CreateAsync(email, name, isAdmin: false);
        if (userId is null)
            throw new InputException(string.Join(" ", errors));
        await households.AddNewAccountAsync(ownerId, householdId, userId, ct);
        var sent = await mail.SendHouseholdAccountInviteAsync(userId, household.Name, inviter, requestBaseUrl, ct);
        return sent.Emailed
            ? new($"{name.Trim()} got an account and is in \"{household.Name}\". The invite went to {sent.SentTo}.", null)
            : new($"{name.Trim()} got an account and is in \"{household.Name}\", but the invite couldn't be emailed ({sent.Reason}) Send them this link:", sent.Link);
    }
}
