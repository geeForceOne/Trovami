using System.Security.Claims;
using Trovami.Core.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;

namespace Trovami.Web.Services;

// Marks the signed-in person as active whenever their browser talks to the app (clicks, typing,
// navigation). UserActivityService throttles the database writes; the write itself runs in the
// background so it never delays the interaction.
public sealed class ActivityCircuitHandler(
    AuthenticationStateProvider auth, UserActivityService activity, ILogger<ActivityCircuitHandler> logger) : CircuitHandler
{
    public override Func<CircuitInboundActivityContext, Task> CreateInboundActivityHandler(
        Func<CircuitInboundActivityContext, Task> next) => async context =>
    {
        _ = RecordAsync();
        await next(context);
    };

    private async Task RecordAsync()
    {
        try
        {
            var state = await auth.GetAuthenticationStateAsync();
            if (state.User.FindFirstValue(ClaimTypes.NameIdentifier) is { Length: > 0 } userId)
                await activity.RecordActivityAsync(userId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Couldn't record user activity");
        }
    }
}
