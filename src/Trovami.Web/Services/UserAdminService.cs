using System.ComponentModel.DataAnnotations;
using Trovami.Core.Services;
using Trovami.Core.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Trovami.Web.Services;

// HasPassword is false while an invite is pending: the account exists but nobody can sign in yet.
// ShownName is what the UI calls the person (see AppUser.ShownName).
public sealed record UserRow(
    string Id, string UserName, string? Email, string? DisplayName, string ShownName,
    bool IsAdmin, bool HasPassword, DateTime CreatedAt);

// User management. Mostly admin-only (there's no open sign-up: users exist only when an admin
// creates them here, directly or by approving a request); FindAsync, SetEmailAsync,
// SetDisplayNameAsync and ChangePasswordAsync also back each person's own Profile page. Each call
// uses its own scope so Identity's DbContext never goes stale over a long-lived Blazor circuit.
public sealed class UserAdminService(IServiceScopeFactory scopes, HouseholdService households)
{
    public async Task<IReadOnlyList<UserRow>> ListAsync()
    {
        await using var scope = scopes.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var admins = (await users.GetUsersInRoleAsync(Roles.Admin)).Select(u => u.Id).ToHashSet();
        var all = await users.Users.AsNoTracking().ToListAsync();
        return all.Select(u => ToRow(u, admins.Contains(u.Id)))
            .OrderBy(r => r.ShownName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public async Task<UserRow?> FindAsync(string userId)
    {
        await using var scope = scopes.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var u = await users.FindByIdAsync(userId);
        if (u is null)
            return null;
        return ToRow(u, await users.IsInRoleAsync(u, Roles.Admin));
    }

    private static UserRow ToRow(AppUser u, bool isAdmin) =>
        new(u.Id, u.UserName ?? "", u.Email, u.DisplayName, u.ShownName, isAdmin, u.PasswordHash is not null, u.CreatedAt);

    // The account a sign-in or reset request means, by email. An email that somehow matches
    // several accounts finds none rather than picking one, since uniqueness is checked in code.
    public static async Task<AppUser?> FindByLoginAsync(UserManager<AppUser> users, string login)
    {
        login = login.Trim();
        if (login.Length == 0)
            return null;
        if (login.Contains('@'))
        {
            var normalized = users.NormalizeEmail(login);
            var matches = await users.Users.Where(u => u.NormalizedEmail == normalized).OrderBy(u => u.Id).Take(2).ToListAsync();
            if (matches.Count == 1)
                return matches[0];
        }
        return await users.FindByNameAsync(login);
    }

    // Rotates the security stamp, so the caller must re-issue the sign-in cookie afterward
    // (see /account/refresh-signin) or the person is signed out at the next revalidation.
    public async Task<IReadOnlyList<string>> ChangePasswordAsync(string userId, string currentPassword, string newPassword)
    {
        await using var scope = scopes.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = await users.FindByIdAsync(userId);
        if (user is null)
            return ["That user no longer exists."];
        var result = await users.ChangePasswordAsync(user, currentPassword, newPassword);
        return result.Succeeded
            ? []
            : result.Errors.Select(e => e.Code == "PasswordMismatch" ? "The current password is wrong." : e.Description).ToList();
    }

    // Creates the user without a password; they choose one through the invite link. They sign in
    // with the email, which also becomes the internal UserName.
    public async Task<(string? UserId, IReadOnlyList<string> Errors)> CreateAsync(string email, string? displayName, bool isAdmin)
    {
        await using var scope = scopes.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        email = email.Trim();
        if (await CheckEmailAsync(users, email, exceptUserId: null) is { } emailError)
            return (null, [emailError]);
        if (CheckDisplayName(displayName) is { } nameError)
            return (null, [nameError]);

        var user = new AppUser { UserName = email, Email = email, DisplayName = NullIfBlank(displayName) };
        var result = await users.CreateAsync(user);
        if (!result.Succeeded)
            return (null, result.Errors.Select(e => e.Description).ToList());
        if (isAdmin)
            await users.AddToRoleAsync(user, Roles.Admin);
        return (user.Id, []);
    }

    public async Task<IReadOnlyList<string>> SetEmailAsync(string userId, string email)
    {
        await using var scope = scopes.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = await users.FindByIdAsync(userId);
        if (user is null)
            return ["That user no longer exists."];

        email = email.Trim();
        if (await CheckEmailAsync(users, email, userId) is { } emailError)
            return [emailError];

        // Users sign in with their email, which is their UserName too; keep the two together.
        if (string.Equals(user.UserName, user.Email, StringComparison.OrdinalIgnoreCase))
            user.UserName = email;

        // Set directly rather than through UserManager.SetEmailAsync, which rotates the security
        // stamp and would sign the person out everywhere and void a pending invite.
        user.Email = email;
        user.EmailConfirmed = false;
        var result = await users.UpdateAsync(user);
        return result.Succeeded ? [] : result.Errors.Select(e => e.Code == "DuplicateUserName"
            ? "Another user already signs in with that email address." : e.Description).ToList();
    }

    public async Task<IReadOnlyList<string>> SetDisplayNameAsync(string userId, string? displayName)
    {
        if (CheckDisplayName(displayName) is { } nameError)
            return [nameError];
        await using var scope = scopes.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = await users.FindByIdAsync(userId);
        if (user is null)
            return ["That user no longer exists."];

        user.DisplayName = NullIfBlank(displayName);
        var result = await users.UpdateAsync(user);
        return result.Succeeded ? [] : result.Errors.Select(e => e.Description).ToList();
    }

    // Everyone has a display name; it's what Trovami calls them.
    public string? CheckDisplayName(string? displayName) =>
        string.IsNullOrWhiteSpace(displayName) ? "Enter a display name."
        : displayName.Trim().Length > AppUser.MaxDisplayNameLength ? $"Keep the display name under {AppUser.MaxDisplayNameLength} characters."
        : null;

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    public async Task<string?> CheckEmailAsync(string email, string? exceptUserId)
    {
        await using var scope = scopes.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        return await CheckEmailAsync(users, email.Trim(), exceptUserId);
    }

    // Identity's own unique-email rule is off (see Program.cs), so uniqueness is checked here.
    private async Task<string?> CheckEmailAsync(UserManager<AppUser> users, string email, string? exceptUserId)
    {
        if (string.IsNullOrEmpty(email))
            return "Enter an email address.";
        if (!new EmailAddressAttribute().IsValid(email))
            return "That doesn't look like an email address.";
        var normalized = users.NormalizeEmail(email);
        return await users.Users.AnyAsync(u => u.NormalizedEmail == normalized && u.Id != exceptUserId)
            ? "Another user already uses that email address."
            : null;
    }

    public async Task SetAdminAsync(string userId, bool isAdmin)
    {
        await using var scope = scopes.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = await users.FindByIdAsync(userId);
        if (user is null) return;
        if (isAdmin)
            await users.AddToRoleAsync(user, Roles.Admin);
        else
            await users.RemoveFromRoleAsync(user, Roles.Admin);
    }

    // Deleting a user hands their households over to another member, or deletes those nobody else
    // is in; items they added stay in their households.
    public async Task DeleteAsync(string userId)
    {
        await households.HandOverOrDeleteOwnedAsync(userId);
        await using var scope = scopes.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = await users.FindByIdAsync(userId);
        if (user is not null)
            await users.DeleteAsync(user);
    }
}
