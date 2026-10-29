using System.Security.Claims;
using Trovami.Core.Backup;
using Trovami.Core.Data;
using Trovami.Core.Models;
using Trovami.Core.Photos;
using Trovami.Core.Services;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Trovami.Web.Services;

public static class Endpoints
{
    public static void MapTrovamiEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // A form post (with antiforgery) rather than a link, so a stray GET can't sign anyone out.
        endpoints.MapPost("/account/logout", async (SignInManager<AppUser> signInManager) =>
        {
            await signInManager.SignOutAsync();
            return TypedResults.LocalRedirect("~/account/login");
        });

        // Changing the password on the (interactive) Profile page rotates the security stamp, and an
        // interactive page can't set cookies, so it comes through here to get a fresh sign-in cookie.
        // Harmless as a GET: it only re-issues the caller's own cookie.
        endpoints.MapGet("/account/refresh-signin", async (HttpContext http, SignInManager<AppUser> signInManager,
            UserManager<AppUser> userManager, [FromQuery] string? returnUrl) =>
        {
            var user = await userManager.GetUserAsync(http.User);
            if (user is null)
                return Results.LocalRedirect("~/account/login");
            await signInManager.RefreshSignInAsync(user);
            return Results.LocalRedirect(IsLocal(returnUrl) ? returnUrl! : "~/profile");
        }).RequireAuthorization();

        // Photos of items and locations, for members of their household. The URL carries the
        // photo's version, so a browser may keep it for good.
        endpoints.MapGet("/photos/{kind}/{id:guid}/{version}", async (string kind, Guid id, string version, [FromQuery] bool? thumb,
            HttpContext http, IDbContextFactory<AppDbContext> dbFactory, PhotoStore photos) =>
        {
            var userId = http.User.FindFirstValue(ClaimTypes.NameIdentifier);
            await using var db = await dbFactory.CreateDbContextAsync(http.RequestAborted);
            var householdId = kind switch
            {
                "item" => await db.Items.Where(i => i.Id == id && i.PhotoVersion == version).Select(i => (Guid?)i.HouseholdId).FirstOrDefaultAsync(http.RequestAborted),
                "location" => await db.Locations.Where(l => l.Id == id && l.PhotoVersion == version).Select(l => (Guid?)l.HouseholdId).FirstOrDefaultAsync(http.RequestAborted),
                _ => null,
            };
            if (householdId is not { } h || userId is null || !await Access.IsMemberAsync(db, userId, h, http.RequestAborted))
                return Results.NotFound();
            if (photos.PathOf(h, id, thumb == true ? PhotoSize.Thumb : PhotoSize.Full) is not { } path)
                return Results.NotFound();
            http.Response.Headers.CacheControl = "private, max-age=31536000, immutable";
            return Results.File(path, "image/jpeg");
        }).RequireAuthorization();

        // For a gethomepage.dev "customapi" widget: item, location and Inbox counts over all of the
        // person's households, for the token in the X-API-Key header.
        endpoints.MapGet("/api/homepage/stats", async (HttpContext http, HomepageStatsService stats) =>
        {
            // A bare 401 for Homepage, not the app's "not found" page that status code pages would render.
            if (http.Features.Get<IStatusCodePagesFeature>() is { } pages)
                pages.Enabled = false;
            var result = await stats.GetAsync(http.Request.Headers["X-API-Key"].FirstOrDefault(), http.RequestAborted);
            return result is null
                ? Results.Unauthorized()
                : Results.Ok(new { items = result.Items, locations = result.Locations, inbox = result.Inbox, households = result.Households });
        }).AllowAnonymous().DisableAntiforgery();

        // Downloads a stored backup; admins only.
        endpoints.MapGet("/admin/backups/file/{name}", (string name, BackupService backups) =>
            backups.PathOf(name) is { } path
                ? Results.File(path, "application/zip", name)
                : Results.NotFound())
            .RequireAuthorization(p => p.RequireRole(Roles.Admin));

        // "Download backup now": made on request, sent, then removed. A password comes in the form
        // body, never in the URL.
        endpoints.MapPost("/admin/backups/download", async (HttpContext http, [FromForm] string? password, BackupService backups) =>
        {
            var (name, temp) = await backups.CreateDownloadAsync(string.IsNullOrEmpty(password) ? null : password, http.RequestAborted);
            http.Response.OnCompleted(() =>
            {
                try { File.Delete(temp); } catch (IOException) { }
                return Task.CompletedTask;
            });
            return Results.File(temp, name.EndsWith(".aes") ? "application/octet-stream" : "application/zip", name);
        }).RequireAuthorization(p => p.RequireRole(Roles.Admin));
    }

    private static bool IsLocal(string? url) =>
        !string.IsNullOrEmpty(url) && url.StartsWith('/') && !url.StartsWith("//") && !url.StartsWith("/\\");
}
