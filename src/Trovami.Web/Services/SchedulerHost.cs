using Trovami.Core.Services;

namespace Trovami.Web.Services;

// Drives Core's Scheduler: once shortly after start (which catches up whatever was missed while
// the app was down), then at the start of every minute.
public sealed class SchedulerHost(Scheduler scheduler, ILogger<SchedulerHost> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            logger.LogInformation("Scheduler started");
            while (!stoppingToken.IsCancellationRequested)
            {
                await scheduler.RunOnceAsync(stoppingToken);
                var now = DateTime.UtcNow;
                var next = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, DateTimeKind.Utc).AddMinutes(1).AddSeconds(2);
                await Task.Delay(next - now, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
