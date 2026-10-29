using System.Reflection;

namespace Trovami.Core;

// The product name as people see it: page titles, headers, emails. Change the name here only.
// Internal identifiers (namespaces, the Data Protection application name and protector purposes)
// are not the product name: changing the Data Protection ones would make the saved SMTP password
// unreadable.
public static class AppInfo
{
    public const string Name = "Trovami";

    // The running version, e.g. "1.2.0", stamped in at build time from the release tag; "dev" for
    // local builds. Read from the app (entry) assembly, where the host project sets it.
    public static string Version { get; } = ReadVersion();

    // The day the app was built, as "2026-10-14" (UTC), stamped in by the host project; null when
    // the build didn't record it.
    public static string? BuildDate { get; } = Assembly.GetEntryAssembly()?
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "BuildDate")?.Value;

    private static string ReadVersion()
    {
        var info = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return string.IsNullOrWhiteSpace(info) ? "dev" : info.Split('+')[0];
    }
}
