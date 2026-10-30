using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Trovami.Core.Data;
using Trovami.Core.Localization;
using Trovami.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Trovami.Core.Services;

public sealed record SettingsView(Theme Theme, string RegionFormat, string TimeZone, bool HasHomepageToken, DateTime? HomepageTokenCreatedAt, string LabelSize);

// Each person's preferences. Read on every request (for the page's formats and theme), so they're
// kept in memory and refreshed on save.
public sealed class SettingsService(IDbContextFactory<AppDbContext> dbFactory)
{
    public const string HomepageTokenPrefix = "trv_";

    // QR label sizes: small, medium, large and a Dymo LabelWriter roll.
    public static readonly IReadOnlyList<string> LabelSizes = ["s", "m", "l", "dymo"];

    private readonly ConcurrentDictionary<string, SettingsView> _cache = new();

    public async Task<SettingsView> GetAsync(string userId, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(userId, out var cached))
            return cached;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var s = await db.UserSettings.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == userId, ct) ?? new UserSettings { UserId = userId };
        return _cache[userId] = View(s);
    }

    // Whether the person has settings yet; the first visit fills them from the browser.
    public async Task<bool> ExistsAsync(string userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.UserSettings.AnyAsync(x => x.UserId == userId, ct);
    }

    // First visit: region format and time zone as the browser reports them.
    public async Task InitializeFromBrowserAsync(string userId, string? browserLanguage, string? browserTimeZone, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.UserSettings.AnyAsync(x => x.UserId == userId, ct))
            return;
        db.UserSettings.Add(new UserSettings
        {
            UserId = userId,
            RegionFormat = Regions.FromBrowser(browserLanguage),
            TimeZone = Regions.IsKnownTimeZone(browserTimeZone) ? browserTimeZone! : Regions.DefaultTimeZone,
        });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Another tab got there first.
        }
        _cache.TryRemove(userId, out _);
    }

    public async Task SaveFormatsAsync(string userId, string regionFormat, string timeZone, CancellationToken ct = default)
    {
        if (!Regions.RegionFormats.Contains(regionFormat))
            throw new InputException("Pick a region format from the list.");
        if (!Regions.IsKnownTimeZone(timeZone))
            throw new InputException("Pick a time zone from the list.");

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var s = await LoadOrAddAsync(db, userId, ct);
        s.RegionFormat = regionFormat;
        s.TimeZone = timeZone;
        await db.SaveChangesAsync(ct);
        _cache.TryRemove(userId, out _);
    }

    public async Task SetThemeAsync(string userId, Theme theme, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var s = await LoadOrAddAsync(db, userId, ct);
        s.Theme = theme;
        await db.SaveChangesAsync(ct);
        _cache.TryRemove(userId, out _);
    }

    public async Task SetLabelSizeAsync(string userId, string size, CancellationToken ct = default)
    {
        if (!LabelSizes.Contains(size))
            throw new InputException("Pick a label size from the list.");
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var s = await LoadOrAddAsync(db, userId, ct);
        s.LabelSize = size;
        await db.SaveChangesAsync(ct);
        _cache.TryRemove(userId, out _);
    }

    // A new homepage widget token, replacing any earlier one. Only its hash is stored, so this is
    // the one time it can be shown.
    public async Task<string> CreateHomepageTokenAsync(string userId, CancellationToken ct = default)
    {
        var token = HomepageTokenPrefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var s = await LoadOrAddAsync(db, userId, ct);
        s.HomepageTokenHash = Hash(token);
        s.HomepageTokenCreatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        _cache.TryRemove(userId, out _);
        return token;
    }

    public async Task RevokeHomepageTokenAsync(string userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.UserSettings.Where(x => x.UserId == userId)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.HomepageTokenHash, (string?)null).SetProperty(x => x.HomepageTokenCreatedAt, (DateTime?)null), ct);
        _cache.TryRemove(userId, out _);
    }

    // The user a homepage token belongs to, or null.
    public async Task<string?> FindUserByHomepageTokenAsync(string? token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token) || !token.StartsWith(HomepageTokenPrefix, StringComparison.Ordinal))
            return null;
        var hash = Hash(token.Trim());
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.UserSettings.AsNoTracking().Where(x => x.HomepageTokenHash == hash).Select(x => x.UserId).FirstOrDefaultAsync(ct);
    }

    public void Forget(string userId) => _cache.TryRemove(userId, out _);

    private static async Task<UserSettings> LoadOrAddAsync(AppDbContext db, string userId, CancellationToken ct)
    {
        var s = await db.UserSettings.FirstOrDefaultAsync(x => x.UserId == userId, ct);
        if (s is null)
        {
            s = new UserSettings { UserId = userId };
            db.UserSettings.Add(s);
        }
        return s;
    }

    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static SettingsView View(UserSettings s) =>
        new(s.Theme, s.RegionFormat, s.TimeZone, s.HomepageTokenHash is not null, s.HomepageTokenCreatedAt,
            LabelSizes.Contains(s.LabelSize) ? s.LabelSize : "m"); // rows from before the setting have ""
}
