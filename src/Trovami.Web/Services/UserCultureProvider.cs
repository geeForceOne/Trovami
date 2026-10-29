using System.Security.Claims;
using Trovami.Core.Services;
using Microsoft.AspNetCore.Localization;

namespace Trovami.Web.Services;

// A signed-in person's region format (numbers and dates) comes from their settings; signed-out
// pages fall through to the browser's Accept-Language. The UI itself is always English. The
// interactive circuit keeps the culture of the request that opened it, so a change reloads the page.
public sealed class UserCultureProvider : RequestCultureProvider
{
    public override async Task<ProviderCultureResult?> DetermineProviderCultureResult(HttpContext http)
    {
        if (http.User.FindFirstValue(ClaimTypes.NameIdentifier) is not { Length: > 0 } userId)
            return null;
        var settings = http.RequestServices.GetRequiredService<SettingsService>();
        var s = await settings.GetAsync(userId, http.RequestAborted);
        return new ProviderCultureResult(s.RegionFormat, "en-US");
    }
}
