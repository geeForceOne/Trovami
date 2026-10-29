using Trovami.Core.Backup;
using Microsoft.Extensions.Logging;

namespace Trovami.Core.Services;

// Everything Trovami does on its own, run once per tick by the host (every minute): the automatic
// backup and cleaning up expired invites and pairing codes. Each job works out what's due from
// what's stored, so a tick after downtime catches up, and a job that fails doesn't stop the others.
// (AI naming has its own queue; see AiNamingService.)
public sealed class Scheduler(BackupService backups, HouseholdService households, DeviceService devices, ILogger<Scheduler> logger)
{
    public async Task RunOnceAsync(CancellationToken ct = default)
    {
        await RunAsync("automatic backup", async () => await backups.RunDueAsync(ct) ? 1 : 0);
        await RunAsync("expired household invites", () => households.RemoveExpiredInvitesAsync(ct));
        await RunAsync("expired pairing codes", () => devices.RemoveExpiredPairingCodesAsync(ct));
    }

    private async Task RunAsync(string job, Func<Task<int>> work)
    {
        try
        {
            await work();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Scheduled job '{Job}' failed", job);
        }
    }
}
