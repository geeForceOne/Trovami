using Trovami.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Trovami.Core.Services;

// The "what's new" banner: after the app is updated, each person sees it once, until they close
// it. Someone who never closes it keeps seeing it, for whatever version is running by then.
// Written with ExecuteUpdate, so it never touches Identity's concurrency stamp.
public sealed class WhatsNewService(IDbContextFactory<AppDbContext> dbFactory)
{
    // The version to announce to this person, or null for no banner. A person without a stored
    // version (new account, or the first run with the banner) starts at the running version, so
    // nobody is told about a release they've been using all along. Local "dev" builds never
    // announce anything.
    public async Task<string?> PendingAsync(string userId, CancellationToken ct = default)
    {
        if (!Version.TryParse(AppInfo.Version, out var running))
            return null;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var seen = await db.Users.Where(u => u.Id == userId).Select(u => u.SeenVersion).FirstOrDefaultAsync(ct);
        if (seen is null)
        {
            await StoreAsync(db, userId, ct);
            return null;
        }
        return Version.TryParse(seen, out var seenVersion) && seenVersion >= running ? null : AppInfo.Version;
    }

    public async Task DismissAsync(string userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await StoreAsync(db, userId, ct);
    }

    private static Task StoreAsync(AppDbContext db, string userId, CancellationToken ct) =>
        db.Users.Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.SeenVersion, AppInfo.Version), ct);
}
