using System.Collections.Concurrent;

namespace Trovami.Web.Services;

// Allows a few account requests per client address per hour; kept in memory, which is enough to
// stop a script from filling the admins' inboxes.
public sealed class RequestLimiter
{
    private const int MaxPerHour = 5;
    private readonly ConcurrentDictionary<string, Queue<DateTime>> _recent = new();

    public bool TryAcquire(string? address)
    {
        var key = string.IsNullOrEmpty(address) ? "unknown" : address;
        var now = DateTime.UtcNow;
        var times = _recent.GetOrAdd(key, _ => new Queue<DateTime>());
        lock (times)
        {
            while (times.Count > 0 && now - times.Peek() > TimeSpan.FromHours(1))
                times.Dequeue();
            if (times.Count >= MaxPerHour)
                return false;
            times.Enqueue(now);
            return true;
        }
    }
}
