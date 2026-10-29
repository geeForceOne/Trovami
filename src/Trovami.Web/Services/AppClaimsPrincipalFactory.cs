using System.Security.Claims;
using Trovami.Core.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Trovami.Web.Services;

// Adds the name to show in the UI (display name, else the email's first part) to the sign-in
// cookie, so the top bar doesn't look the user up on every page. A change only reaches the cookie
// when it's re-issued: the Account page goes through /account/refresh-signin after saving.
public sealed class AppClaimsPrincipalFactory(
    UserManager<AppUser> userManager, RoleManager<IdentityRole> roleManager, IOptions<IdentityOptions> options)
    : UserClaimsPrincipalFactory<AppUser, IdentityRole>(userManager, roleManager, options)
{
    public const string ShownNameClaim = "trovami:shown_name";

    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(AppUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        identity.AddClaim(new Claim(ShownNameClaim, user.ShownName));
        return identity;
    }

    // For cookies issued before this claim existed, fall back to the sign-in name.
    public static string ShownName(ClaimsPrincipal user) =>
        user.FindFirstValue(ShownNameClaim) is { Length: > 0 } name ? name : user.Identity?.Name ?? "";
}
