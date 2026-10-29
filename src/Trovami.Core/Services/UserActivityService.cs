using System.Collections.Concurrent;
using Trovami.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Trovami.Core.Services;

// Records when each person last used the app (signing in counts), for the admin's Users page.
// Written with ExecuteUpdate, so it never touches Identity's concurrency stamp and can't clash
// with an account change saved at the same moment.
public sealed class UserActivityService(IDbContextFactory<AppDbContext> dbFactory)
{
    // Activity is stored at most this often per person: it's shown as "2 h ago", not to the second,
    // and a database write on every click would be wasted.
    public static readonly TimeSpan ActiveResolution = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, DateTime> _lastStored = new();

    public async Task RecordLoginAsync(string userId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.Users.Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.LastActiveAt, now), ct);
        _lastStored[userId] = now;
    }

    // Cheap to call on every interaction: only reaches the database once per ActiveResolution.
    public async Task RecordActivityAsync(string userId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        if (_lastStored.TryGetValue(userId, out var last) && now - last < ActiveResolution)
            return;
        _lastStored[userId] = now;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.Users.Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.LastActiveAt, now), ct);
    }
}
