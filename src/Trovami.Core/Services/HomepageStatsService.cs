using Trovami.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Trovami.Core.Services;

public sealed record HomepageStats(int Items, int Locations, int Inbox, int Households);

// Counts for a gethomepage.dev widget, over all of the person's households.
public sealed class HomepageStatsService(IDbContextFactory<AppDbContext> dbFactory, SettingsService settings)
{
    public async Task<HomepageStats?> GetAsync(string? token, CancellationToken ct = default)
    {
        if (await settings.FindUserByHomepageTokenAsync(token, ct) is not { } userId)
            return null;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var households = await db.HouseholdMembers.Where(m => m.UserId == userId).Select(m => m.HouseholdId).ToListAsync(ct);
        return new HomepageStats(
            await db.Items.CountAsync(i => households.Contains(i.HouseholdId), ct),
            await db.Locations.CountAsync(l => households.Contains(l.HouseholdId), ct),
            await db.Items.CountAsync(i => households.Contains(i.HouseholdId) && i.LocationId == null, ct),
            households.Count);
    }
}
