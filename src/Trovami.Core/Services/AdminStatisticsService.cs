using Trovami.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Trovami.Core.Services;

// Counts only, never names or contents: what the admin sees about each person on the Users page.
public sealed record UserUsage(string UserId, DateTime? LastActiveAt, int Households, int ItemsAdded, int Devices);

public sealed class AdminStatisticsService(IDbContextFactory<AppDbContext> dbFactory)
{
    public async Task<IReadOnlyDictionary<string, UserUsage>> GetUsageAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var users = await db.Users.AsNoTracking().Select(u => new { u.Id, u.LastActiveAt }).ToListAsync(ct);
        var households = await db.HouseholdMembers.GroupBy(m => m.UserId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var items = await db.Items.Where(i => i.CreatedById != null).GroupBy(i => i.CreatedById!).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var devices = await db.Devices.GroupBy(d => d.UserId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        return users.ToDictionary(u => u.Id, u => new UserUsage(u.Id, u.LastActiveAt,
            households.GetValueOrDefault(u.Id), items.GetValueOrDefault(u.Id), devices.GetValueOrDefault(u.Id)));
    }
}
