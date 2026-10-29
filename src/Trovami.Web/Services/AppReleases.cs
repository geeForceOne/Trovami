using System.Text.Json.Nodes;

namespace Trovami.Web.Services;

public sealed record AppRelease(string Version, string? ApkUrl, string? Notes, string PageUrl);

// The newest GitHub release and its APK, for the Android app's update check. Asked at most once
// an hour; the repository comes from Trovami__ReleasesRepository (owner/name).
public sealed class AppReleases(IConfiguration config, ILogger<AppReleases> logger)
{
    private static readonly HttpClient Http = CreateClient();
    private static readonly TimeSpan CacheFor = TimeSpan.FromHours(1);

    private readonly SemaphoreSlim _lock = new(1, 1);
    private (DateTime At, AppRelease? Release)? _cached;

    private string Repository => config["Trovami:ReleasesRepository"] ?? "geeForceOne/Trovami";

    public async Task<AppRelease?> LatestAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_cached is { } c && DateTime.UtcNow - c.At < CacheFor)
                return c.Release;
            AppRelease? release = null;
            try
            {
                var json = JsonNode.Parse(await Http.GetStringAsync($"https://api.github.com/repos/{Repository}/releases/latest", ct));
                var tag = json?["tag_name"]?.GetValue<string>();
                if (tag is not null)
                {
                    var apk = json!["assets"]?.AsArray()
                        .Select(a => a?["browser_download_url"]?.GetValue<string>())
                        .FirstOrDefault(u => u is not null && u.EndsWith(".apk", StringComparison.OrdinalIgnoreCase));
                    release = new AppRelease(tag.TrimStart('v'), apk, json["body"]?.GetValue<string>(), json["html_url"]?.GetValue<string>() ?? "");
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                logger.LogInformation("Couldn't check the latest release: {Reason}", ex.Message);
            }
            _cached = (DateTime.UtcNow, release);
            return release;
        }
        finally
        {
            _lock.Release();
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        // GitHub's API refuses requests without a User-Agent.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Trovami");
        return client;
    }
}
