using System.Security.Cryptography;
using Trovami.Core.Data;
using Trovami.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Trovami.Core.Services;

public sealed record DeviceView(int Id, string Name, string? AppVersion, DateTime CreatedAt, DateTime? LastSeenAt);

// The signed-in device a token belongs to.
public sealed record DeviceIdentity(int DeviceId, string UserId);

// Phones signed in to an account. A phone gets a long-lived token, by scanning the pairing QR code
// from Profile or by signing in with email and password; only the token's hash is stored, and
// Profile can revoke it.
public sealed class DeviceService(IDbContextFactory<AppDbContext> dbFactory)
{
    public const string TokenPrefix = "trd_";
    private const string PairingAlphabet = "23456789ABCDEFGHJKMNPQRSTUVWXYZ";

    // Seen at most this often per device, like UserActivityService.
    private static readonly TimeSpan SeenResolution = TimeSpan.FromMinutes(5);

    // A one-time code for the "Connect a phone" QR code, replacing the user's earlier ones.
    public async Task<string> CreatePairingCodeAsync(string userId, CancellationToken ct = default)
    {
        var code = new string(Enumerable.Range(0, 10).Select(_ => PairingAlphabet[RandomNumberGenerator.GetInt32(PairingAlphabet.Length)]).ToArray());
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.PairingCodes.Where(p => p.UserId == userId).ExecuteDeleteAsync(ct);
        db.PairingCodes.Add(new PairingCode { UserId = userId, CodeHash = SettingsService.Hash(code), ExpiresAt = DateTime.UtcNow + PairingCode.ValidFor });
        await db.SaveChangesAsync(ct);
        return code;
    }

    // Trades a pairing code for a device token. Null when the code is wrong, used or expired.
    public async Task<(string Token, string UserId)?> PairAsync(string code, string deviceName, string? appVersion, CancellationToken ct = default)
    {
        var hash = SettingsService.Hash(code.Trim().ToUpperInvariant());
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var pairing = await db.PairingCodes.FirstOrDefaultAsync(p => p.CodeHash == hash, ct);
        if (pairing is null)
            return null;
        db.PairingCodes.Remove(pairing);
        await db.SaveChangesAsync(ct);
        if (pairing.ExpiresAt < DateTime.UtcNow)
            return null;
        return (await AddAsync(pairing.UserId, deviceName, appVersion, ct), pairing.UserId);
    }

    // After a password sign-in on the phone.
    public async Task<string> AddAsync(string userId, string deviceName, string? appVersion, CancellationToken ct = default)
    {
        var token = TokenPrefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var name = string.IsNullOrWhiteSpace(deviceName) ? "Phone" : deviceName.Trim();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.Devices.Add(new Device
        {
            UserId = userId,
            Name = name.Length > Device.MaxNameLength ? name[..Device.MaxNameLength] : name,
            TokenHash = SettingsService.Hash(token),
            AppVersion = Truncate(appVersion, 32),
            LastSeenAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(ct);
        return token;
    }

    // The device a bearer token belongs to; also notes when it was last seen and its app version.
    public async Task<DeviceIdentity?> AuthenticateAsync(string? token, string? appVersion, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token) || !token.StartsWith(TokenPrefix, StringComparison.Ordinal))
            return null;
        var hash = SettingsService.Hash(token.Trim());
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.TokenHash == hash, ct);
        if (device is null)
            return null;
        var version = Truncate(appVersion, 32) ?? device.AppVersion;
        if (device.LastSeenAt is not { } seen || DateTime.UtcNow - seen > SeenResolution || version != device.AppVersion)
            await db.Devices.Where(d => d.Id == device.Id).ExecuteUpdateAsync(s => s
                .SetProperty(d => d.LastSeenAt, DateTime.UtcNow).SetProperty(d => d.AppVersion, version), ct);
        return new DeviceIdentity(device.Id, device.UserId);
    }

    public async Task<IReadOnlyList<DeviceView>> ListAsync(string userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Devices.AsNoTracking().Where(d => d.UserId == userId).OrderByDescending(d => d.LastSeenAt)
            .Select(d => new DeviceView(d.Id, d.Name, d.AppVersion, d.CreatedAt, d.LastSeenAt)).ToListAsync(ct);
    }

    public async Task RevokeAsync(string userId, int deviceId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.Devices.Where(d => d.Id == deviceId && d.UserId == userId).ExecuteDeleteAsync(ct);
    }

    // A phone signing itself out.
    public async Task RevokeTokenAsync(string token, CancellationToken ct = default)
    {
        var hash = SettingsService.Hash(token.Trim());
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.Devices.Where(d => d.TokenHash == hash).ExecuteDeleteAsync(ct);
    }

    // The scheduler's job.
    public async Task<int> RemoveExpiredPairingCodesAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = DateTime.UtcNow;
        return await db.PairingCodes.Where(p => p.ExpiresAt < now).ExecuteDeleteAsync(ct);
    }

    private static string? Truncate(string? s, int max) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim().Length > max ? s.Trim()[..max] : s.Trim();
}
