using Trovami.Core;
using Trovami.Core.Models;
using Trovami.Core.Photos;
using Trovami.Core.Services;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Identity;

namespace Trovami.Web.Services;

public sealed record PairRequest(string Code, string? DeviceName, string? AppVersion);

public sealed record SignInRequest(string Email, string Password, string? DeviceName, string? AppVersion);

public sealed record SignInResponse(string Token, string Name, string? Email);

public sealed record PullRequest(long Cursor, IReadOnlyList<Guid>? Households);

// The Android app's API. Versioned (/api/v1), so older app versions keep working after an
// update: v1 only ever gains fields. Every call except signing in carries the device token as
// "Authorization: Bearer trd_…"; the app's version goes in X-Trovami-Version.
public static class PhoneApi
{
    public const string VersionHeader = "X-Trovami-Version";
    private const string DeviceKey = "trovami.device";

    public static void MapPhoneApi(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/v1").DisableAntiforgery().AllowAnonymous()
            .AddEndpointFilter(async (context, next) =>
            {
                // Plain status codes for the app, not the "not found" page.
                if (context.HttpContext.Features.Get<IStatusCodePagesFeature>() is { } pages)
                    pages.Enabled = false;
                return await next(context);
            });

        api.MapPost("/pair", async (PairRequest request, DeviceService devices, UserAdminService users, CancellationToken ct) =>
        {
            var paired = await devices.PairAsync(request.Code ?? "", request.DeviceName ?? "Phone", request.AppVersion, ct);
            if (paired is not { } p)
                return Results.Problem("That code is wrong or has expired. Show a new one under Profile → Connect a phone.", statusCode: 400);
            var user = await users.FindAsync(p.UserId);
            return Results.Ok(new SignInResponse(p.Token, user?.ShownName ?? "", user?.Email));
        });

        api.MapPost("/signin", async (SignInRequest request, HttpContext http, DeviceService devices, UserManager<AppUser> userManager,
            SignInManager<AppUser> signInManager, UserActivityService activity, CancellationToken ct) =>
        {
            var user = await UserAdminService.FindByLoginAsync(userManager, request.Email ?? "");
            var result = user is null
                ? SignInResult.Failed
                : await signInManager.CheckPasswordSignInAsync(user, request.Password ?? "", lockoutOnFailure: true);
            if (!result.Succeeded)
                return Results.Problem(result.IsLockedOut
                    ? "This account is locked for a few minutes after too many failed attempts. Try again later."
                    : "That email and password don't match.", statusCode: 401);
            await activity.RecordLoginAsync(user!.Id, ct);
            var token = await devices.AddAsync(user.Id, request.DeviceName ?? "Phone", request.AppVersion, ct);
            return Results.Ok(new SignInResponse(token, user.ShownName, user.Email));
        });

        var device = api.MapGroup("").AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var header = http.Request.Headers.Authorization.ToString();
            var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..].Trim() : null;
            var devices = http.RequestServices.GetRequiredService<DeviceService>();
            var identity = await devices.AuthenticateAsync(token, http.Request.Headers[VersionHeader].FirstOrDefault(), http.RequestAborted);
            if (identity is null)
                return Results.Unauthorized();
            http.Items[DeviceKey] = identity;
            await http.RequestServices.GetRequiredService<UserActivityService>().RecordActivityAsync(identity.UserId, http.RequestAborted);
            return await next(context);
        });

        device.MapPost("/signout", async (HttpContext http, DeviceService devices, CancellationToken ct) =>
        {
            await devices.RevokeAsync(Device(http).UserId, Device(http).DeviceId, ct);
            return Results.NoContent();
        });

        device.MapGet("/me", async (HttpContext http, UserAdminService users) =>
        {
            var user = await users.FindAsync(Device(http).UserId);
            return user is null ? Results.Unauthorized() : Results.Ok(new { name = user.ShownName, email = user.Email });
        });

        device.MapPost("/sync/pull", async (PullRequest request, HttpContext http, SyncService sync, CancellationToken ct) =>
            Results.Ok(await sync.PullAsync(Device(http).UserId, request.Cursor, request.Households ?? [], ct)));

        device.MapPost("/sync/push", async (SyncPush request, HttpContext http, SyncService sync, CancellationToken ct) =>
            Results.Ok(await sync.PushAsync(Device(http).UserId, request, ct)));

        // The photo of an item pushed before, as the request body (JPEG). Sending it again replaces it.
        device.MapPut("/items/{id:guid}/photo", async (Guid id, HttpContext http, ItemService items, CancellationToken ct) =>
        {
            if (http.Request.ContentLength > PhotoStore.MaxUploadBytes)
                return Results.Problem("The photo is too large.", statusCode: 413);
            try
            {
                await items.SetPhotoAsync(Device(http).UserId, id, http.Request.Body, ct);
                return Results.NoContent();
            }
            catch (NotFoundException ex)
            {
                return Results.Problem(ex.Message, statusCode: 404);
            }
            catch (InputException ex)
            {
                return Results.Problem(ex.Message, statusCode: 400);
            }
        });

        // ----- Find mode: online only, so nothing here is kept on the phone. -----

        // Items matching a search, like the web app's Find (locations the phone finds in its own copy).
        device.MapGet("/households/{householdId:guid}/items/search", (Guid householdId, string? q, HttpContext http, ItemService items, CancellationToken ct) =>
            Guarded(async () => (await items.SearchAsync(Device(http).UserId, householdId, q ?? "", ct)).Items.Select(PhoneItem.From)));

        // Everything in a location and below it, A to Z.
        device.MapGet("/households/{householdId:guid}/locations/{locationId:guid}/items", (Guid householdId, Guid locationId, HttpContext http,
            ItemService items, CancellationToken ct) =>
            Guarded(async () => (await items.BelowAsync(Device(http).UserId, householdId, locationId, ct))
                .OrderBy(i => i.ShownName, StringComparer.CurrentCultureIgnoreCase).ThenByDescending(i => i.CreatedAt)
                .Select(PhoneItem.From)));

        // The Inbox, newest first.
        device.MapGet("/households/{householdId:guid}/inbox", (Guid householdId, HttpContext http, ItemService items, CancellationToken ct) =>
            Guarded(async () => (await items.InboxAsync(Device(http).UserId, householdId, ct)).Select(PhoneItem.From)));

        // What to suggest while a keyword is typed, with the keywords' colors.
        device.MapGet("/households/{householdId:guid}/keywords", (Guid householdId, HttpContext http, ItemService items, CancellationToken ct) =>
            Guarded(async () => (await items.KeywordSuggestionsAsync(Device(http).UserId, householdId, ct))
                .Select(k => new PhoneKeyword(k.Keyword, k.Color))));

        device.MapGet("/items/{id:guid}", (Guid id, HttpContext http, ItemService items, CancellationToken ct) =>
            Guarded(async () => PhoneItem.From(await items.GetAsync(Device(http).UserId, id, ct))));

        // A name typed on the phone is a person's name, like one typed on the web.
        device.MapPut("/items/{id:guid}/name", (Guid id, ItemNameRequest request, HttpContext http, ItemService items, CancellationToken ct) =>
            Guarded(async () =>
            {
                await items.RenameAsync(Device(http).UserId, id, request.Name, ct);
                return PhoneItem.From(await items.GetAsync(Device(http).UserId, id, ct));
            }));

        device.MapPut("/items/{id:guid}/keywords", (Guid id, ItemKeywordsRequest request, HttpContext http, ItemService items, CancellationToken ct) =>
            Guarded(async () =>
            {
                await items.SetKeywordsAsync(Device(http).UserId, id, request.Keywords ?? [], ct);
                return PhoneItem.From(await items.GetAsync(Device(http).UserId, id, ct));
            }));

        // LocationId null: to the Inbox.
        device.MapPut("/items/{id:guid}/location", (Guid id, ItemLocationRequest request, HttpContext http, ItemService items, CancellationToken ct) =>
            Guarded(async () =>
            {
                await items.MoveAsync(Device(http).UserId, [id], request.LocationId, ct);
                return PhoneItem.From(await items.GetAsync(Device(http).UserId, id, ct));
            }));

        // Deleting an item that's already gone succeeds too.
        device.MapDelete("/items/{id:guid}", (Guid id, HttpContext http, ItemService items, CancellationToken ct) =>
            Guarded(async () =>
            {
                await items.DeleteAsync(Device(http).UserId, [id], ct);
                return new { deleted = id };
            }));

        // The item's photo (or its thumbnail); the version in the query only makes the URL change with the photo.
        device.MapGet("/items/{id:guid}/photo", async (Guid id, bool? thumb, HttpContext http, ItemService items, PhotoStore photos, CancellationToken ct) =>
        {
            try
            {
                var item = await items.GetAsync(Device(http).UserId, id, ct);
                if (item.PhotoVersion is null || photos.PathOf(item.HouseholdId, id, thumb == true ? PhotoSize.Thumb : PhotoSize.Full) is not { } path)
                    return Results.NotFound();
                http.Response.Headers.CacheControl = "private, max-age=31536000, immutable";
                return Results.File(path, "image/jpeg");
            }
            catch (NotFoundException)
            {
                return Results.NotFound();
            }
        });

        // The newest release, for the app's update check.
        device.MapGet("/app/latest", async (AppReleases releases, CancellationToken ct) =>
            await releases.LatestAsync(ct) is { } latest ? Results.Ok(latest) : Results.NoContent());
    }

    private static DeviceIdentity Device(HttpContext http) => (DeviceIdentity)http.Items[DeviceKey]!;

    // The services' own messages as problem details, which the app shows as they are.
    private static async Task<IResult> Guarded<T>(Func<Task<T>> call)
    {
        try
        {
            return Results.Ok(await call());
        }
        catch (NotFoundException ex)
        {
            return Results.Problem(ex.Message, statusCode: 404);
        }
        catch (InputException ex)
        {
            return Results.Problem(ex.Message, statusCode: 400);
        }
    }
}

public sealed record ItemNameRequest(string? Name);

public sealed record ItemKeywordsRequest(IReadOnlyList<string>? Keywords);

public sealed record ItemLocationRequest(Guid? LocationId);

public sealed record PhoneKeyword(string Keyword, string? Color);

// An item as find mode shows it. Name is null for an unnamed item; ShownName is what to show then.
public sealed record PhoneItem(
    Guid Id, Guid HouseholdId, string? Name, string ShownName, bool Naming, string? Notes, int Quantity,
    Guid? LocationId, string Path, string? PhotoVersion, IReadOnlyList<string> Keywords)
{
    public static PhoneItem From(ItemView i) =>
        new(i.Id, i.HouseholdId, i.Name, i.ShownName, i.Naming, i.Notes, i.Quantity, i.LocationId, i.Path, i.PhotoVersion, i.Keywords);
}
