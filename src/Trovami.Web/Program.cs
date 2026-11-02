using System.Globalization;
using System.Text.Json.Serialization;
using Trovami.Core;
using Trovami.Core.Ai;
using Trovami.Core.Backup;
using Trovami.Core.Data;
using Trovami.Core.Localization;
using Trovami.Core.Models;
using Trovami.Web.Components;
using Trovami.Web.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Localization;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Everything persistent lives under one data directory (a Docker volume in production): the
// SQLite database, the Data Protection key ring, the photos and the backups. Each path can also be
// overridden on its own through Storage__DatabasePath, Storage__KeysPath, Storage__BackupsPath,
// Storage__PhotosPath.
var storage = builder.Configuration.GetSection("Storage");
var paths = StoragePaths.From(storage["DataDirectory"] ?? "data", storage["DatabasePath"], storage["KeysPath"], storage["BackupsPath"],
    storage["PhotosPath"], builder.Environment.ContentRootPath);
paths.CreateDirectories();

// A restore prepared on the Backups page is swapped in now, before anything opens the files.
using (var startupLog = LoggerFactory.Create(b => b.AddConsole()))
    BackupService.ApplyPendingRestore(paths, startupLog.CreateLogger("Restore"));

builder.Services.AddTrovamiCore(paths);

builder.Services.AddDataProtection()
    // Not the product name: changing it would make the stored SMTP password and AI keys unreadable.
    .SetApplicationName("Trovami")
    .PersistKeysToFileSystem(new DirectoryInfo(paths.KeysPath));

// Identity: email + password now. External logins plug in later as additional authentication
// handlers on top of the same Identity cookies. The phone uses device tokens instead (PhoneApi).
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();
builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = IdentityConstants.ApplicationScheme;
        options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
    })
    .AddIdentityCookies();
builder.Services.ConfigureApplicationCookie(options =>
{
    // Its own name: cookies ignore the port, so with the default name another ASP.NET Identity app
    // on the same host (LaLira, Recall) would overwrite this one and sign people out.
    options.Cookie.Name = ".Trovami.Auth";
    options.LoginPath = "/account/login";
    options.AccessDeniedPath = "/";
    options.ExpireTimeSpan = TimeSpan.FromDays(30);
    options.SlidingExpiration = true;
    // The phone API answers 401 instead of redirecting to the sign-in page.
    options.Events.OnRedirectToLogin = context =>
    {
        if (context.Request.Path.StartsWithSegments("/api"))
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        else
            context.Response.Redirect(context.RedirectUri);
        return Task.CompletedTask;
    };
});
builder.Services.ConfigureExternalCookie(options => options.Cookie.Name = ".Trovami.External");
builder.Services.AddAuthorization();
builder.Services.AddAntiforgery(options => options.Cookie.Name = ".Trovami.Antiforgery");

builder.Services.AddIdentityCore<AppUser>(options =>
    {
        // Uniqueness of emails is checked in UserAdminService instead.
        options.User.RequireUniqueEmail = false;
        // The email is the UserName, and an email may contain characters beyond the default set.
        options.User.AllowedUserNameCharacters = "";
        options.Password.RequiredLength = 8;
        options.Password.RequireNonAlphanumeric = false;
        options.Password.RequireUppercase = false;
        options.Password.RequireLowercase = false;
        options.Password.RequireDigit = false;
        options.Lockout.MaxFailedAccessAttempts = 10;
    })
    .AddRoles<IdentityRole>()
    .AddClaimsPrincipalFactory<AppClaimsPrincipalFactory>()
    .AddEntityFrameworkStores<AppDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders()
    .AddErrorDescriber<IdentityErrors>()
    .AddTokenProvider<InviteTokenProvider>(AccountMailService.InviteProvider);
// Password reset links are short-lived; invite links get their own, longer lifespan.
builder.Services.Configure<DataProtectionTokenProviderOptions>(o => o.TokenLifespan = AccountMailService.ResetLifespan);

// The UI is in English; numbers and dates follow the user's region format (or the browser's
// language when signed out).
builder.Services.Configure<RequestLocalizationOptions>(o =>
{
    o.SupportedCultures = Regions.RegionFormats.Select(CultureInfo.GetCultureInfo).ToList();
    o.SupportedUICultures = [CultureInfo.GetCultureInfo("en-US")];
    o.DefaultRequestCulture = new RequestCulture(Regions.DefaultRegion, "en-US");
    o.FallBackToParentCultures = true;
    o.RequestCultureProviders = [new UserCultureProvider(), new AcceptLanguageHeaderRequestCultureProvider()];
});

// The phone API sends enums by name ("Drawer"), not as numbers.
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddSingleton<UserAdminService>();
builder.Services.AddSingleton<AppReleases>();
builder.Services.AddScoped<CircuitHandler, ActivityCircuitHandler>();
builder.Services.AddSingleton<AccountMailService>();
builder.Services.AddSingleton<HouseholdInviteService>();
builder.Services.AddSingleton<RequestLimiter>();
builder.Services.AddHostedService<SchedulerHost>();
builder.Services.AddHostedService<AiNamingHost>();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents(o => o.DetailedErrors = builder.Environment.IsDevelopment())
    // Room for photo and backup uploads, which arrive over the circuit in chunks.
    .AddHubOptions(o => o.MaximumReceiveMessageSize = 1024 * 1024);

var app = builder.Build();

// Behind a reverse proxy that ends HTTPS, so redirects and links keep https. Only the scheme: the
// proxy's address isn't known in advance, so X-Forwarded-For from anyone would let a caller fake
// the IP that the account request limit counts.
var forwarded = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedProto };
forwarded.KnownIPNetworks.Clear();
forwarded.KnownProxies.Clear();
app.UseForwardedHeaders(forwarded);

await using (var scope = app.Services.CreateAsyncScope())
{
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
    await using var db = await factory.CreateDbContextAsync();

    // Before an existing database gets a newer schema (an update of the app), keep a copy of it as
    // it is. Without that copy the update doesn't run: the data stays untouched, and the log says why.
    if ((await db.Database.GetAppliedMigrationsAsync()).Any() && (await db.Database.GetPendingMigrationsAsync()).Any())
    {
        var log = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        try
        {
            var safety = await scope.ServiceProvider.GetRequiredService<BackupService>().CreateBeforeUpdateAsync();
            log.LogWarning("The database schema is being updated; a backup of the previous state is in {Backup}", safety.Name);
        }
        catch (Exception ex)
        {
            log.LogCritical(ex, "Couldn't make the backup that comes before a database update, so the update was not applied and the app stops. Fix the cause (for example free disk space in {Folder}) and start it again.", paths.BackupsPath);
            Environment.ExitCode = 1;
            return;
        }
    }
    await db.Database.MigrateAsync();

    var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
    if (!await roles.RoleExistsAsync(Roles.Admin))
        await roles.CreateAsync(new IdentityRole(Roles.Admin));
}

// A developer's own AI key from user secrets (dotnet user-secrets set "DevAi:Key" "…"), so local
// test instances name items without a key entered per household. Never read outside Development.
if (app.Environment.IsDevelopment() && app.Configuration["DevAi:Key"] is { Length: > 0 } devKey)
{
    var devProvider = Enum.TryParse<AiProvider>(app.Configuration["DevAi:Provider"], ignoreCase: true, out var p) && p != AiProvider.None
        ? p : AiProvider.Claude;
    app.Services.GetRequiredService<AiNamingService>().DevFallback = new DevAiKey(devProvider, devKey);
    app.Logger.LogInformation("Development: households without an AI key use the developer's {Provider} key from user secrets.", devProvider);
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.UseAuthentication();
// After authentication, so the signed-in person's own formats apply.
app.UseRequestLocalization();
app.UseAuthorization();
// After authentication, so tokens are validated against the signed-in user they were issued for.
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();
app.MapTrovamiEndpoints();
app.MapPhoneApi();

app.Run();
