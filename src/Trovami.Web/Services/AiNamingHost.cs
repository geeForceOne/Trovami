using Trovami.Core.Ai;

namespace Trovami.Web.Services;

// Runs Core's AI naming queue for as long as the app runs.
public sealed class AiNamingHost(AiNamingService naming, ILogger<AiNamingHost> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
            await naming.RunAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "AI naming stopped");
        }
    }
}
