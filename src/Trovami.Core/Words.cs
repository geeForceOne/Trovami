using System.Globalization;

namespace Trovami.Core;

// Small wording helpers shared by emails and pages.
public static class Words
{
    // one/many contain {0} for the count, e.g. "{0} item" / "{0} items". The count is written in
    // the current region format.
    public static string Plural(int count, string one, string many) =>
        string.Format(CultureInfo.CurrentCulture, count == 1 ? one : many, count);

    // "7 days", "3 hours", "10 minutes".
    public static string Describe(TimeSpan span) =>
        span.TotalDays >= 1 ? Plural((int)Math.Round(span.TotalDays), "{0} day", "{0} days")
        : span.TotalHours >= 1 ? Plural((int)Math.Round(span.TotalHours), "{0} hour", "{0} hours")
        : Plural((int)Math.Round(span.TotalMinutes), "{0} minute", "{0} minutes");
}
