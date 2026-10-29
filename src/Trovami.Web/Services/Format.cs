using System.Globalization;
using Trovami.Core.Localization;
using Trovami.Core.Models;

namespace Trovami.Web.Services;

// Numbers and dates the way the person's region format writes them (the request's culture).
// The words are always US English.
public static class Format
{
    private static CultureInfo C => CultureInfo.CurrentCulture;

    public static string Number(int value) => value.ToString("N0", C);

    // "3 items", with the number in the region format.
    public static string Count(int count, string one, string many) => $"{Number(count)} {(count == 1 ? one : many)}";

    public static string Date(DateOnly date) => date.ToString("d", C);

    public static string Date(DateTime utc, string timeZone) => Regions.LocalNow(utc, timeZone).ToString("d", C);

    public static string DateTime(DateTime utc, string timeZone) => Regions.LocalNow(utc, timeZone).ToString("g", C);

    public static string Time(TimeOnly time) => time.ToString("t", C);

    public static string Ago(DateTime utc)
    {
        var span = System.DateTime.UtcNow - utc;
        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalHours < 1) return Count((int)span.TotalMinutes, "minute ago", "minutes ago");
        if (span.TotalDays < 1) return Count((int)span.TotalHours, "hour ago", "hours ago");
        if (span.TotalDays < 60) return Count((int)span.TotalDays, "day ago", "days ago");
        return utc.ToString("d", C);
    }

    public static string Size(long bytes) =>
        bytes >= 1024 * 1024 ? (bytes / 1024d / 1024d).ToString("0.0", C) + " MB" : Math.Max(1, bytes / 1024).ToString(C) + " KB";

    // The style that gives an element a location's color as --loc (app.css: --c-<key>).
    public static string? ColorVar(string? color) =>
        LocationColors.IsKnown(color) ? $"--loc:var(--c-{color})" : null;

    public static string KindLabel(LocationKind kind) => kind.ToString();

    // The icon (Icon.razor) for each kind of location.
    public static string KindIcon(LocationKind kind) => kind switch
    {
        LocationKind.House => "house",
        LocationKind.Apartment => "apartment",
        LocationKind.Floor => "floor",
        LocationKind.Room => "room",
        LocationKind.Cellar => "cellar",
        LocationKind.Garage => "garage",
        LocationKind.Attic => "attic",
        LocationKind.Closet => "closet",
        LocationKind.Cabinet => "cabinet",
        LocationKind.Shelf => "shelf",
        LocationKind.Rack => "rack",
        LocationKind.Drawer => "drawer",
        LocationKind.Box => "box",
        LocationKind.Bag => "bag",
        _ => "pin",
    };

    // Photo URLs (see Endpoints): versioned, so browsers can keep them.
    public static string ItemPhoto(Guid id, string version, bool thumb = true) =>
        $"photos/item/{id}/{version}{(thumb ? "?thumb=true" : "")}";

    public static string LocationPhoto(Guid id, string version, bool thumb = true) =>
        $"photos/location/{id}/{version}{(thumb ? "?thumb=true" : "")}";
}
