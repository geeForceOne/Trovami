using System.Globalization;

namespace Trovami.Core.Localization;

// Reading dates and times as people type them, in their region format. Browsers show their own
// date fields in the browser's language whatever the page says, so Trovami uses text fields.
public static class DateInput
{
    // In the region's order (day, month, year for de-CH; month, day, year for en-US):
    // "14.10.2026", "1.5.2009", "14.10.26", "14.10." (this year), "2026-10-14" (always year first).
    // Digits only work too: "29012016", "290116", "2901" (this year), and "20160129" as a fallback.
    public static bool TryParse(string? text, CultureInfo culture, DateOnly today, out DateOnly date)
    {
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length is 4 or 6 or 8 && trimmed.All(char.IsAsciiDigit))
            return TryParseParts(SplitDigits(trimmed, Order(culture)), culture, today, out date)
                || trimmed.Length == 8 && TryParseParts([trimmed[..4], trimmed[4..6], trimmed[6..]], culture, today, out date);
        var parts = System.Text.RegularExpressions.Regex.Split(trimmed, "[^0-9]+").Where(p => p.Length > 0).ToArray();
        return TryParseParts(parts, culture, today, out date);
    }

    // "29012016" in the region's order: two digits each for day and month, the rest for the year
    // (four, two or none).
    private static string[] SplitDigits(string digits, char[] order)
    {
        var yearLength = digits.Length - 4;
        var parts = new List<string>();
        var at = 0;
        foreach (var part in order)
        {
            var length = part == 'y' ? yearLength : 2;
            if (length == 0) continue;
            parts.Add(digits.Substring(at, length));
            at += length;
        }
        return parts.ToArray();
    }

    private static bool TryParseParts(string[] parts, CultureInfo culture, DateOnly today, out DateOnly date)
    {
        date = default;
        if (parts.Length is < 2 or > 3 || parts.Any(p => p.Length > 4))
            return false;

        int day, month, year;
        if (parts[0].Length == 4)
        {
            if (parts.Length != 3) return false;
            (year, month, day) = (int.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2]));
        }
        else
        {
            // Two parts are day and month in the region's order, the year is this year's.
            var order = parts.Length == 2 ? Order(culture).Where(c => c != 'y').ToArray() : Order(culture);
            var values = new Dictionary<char, int>();
            for (var i = 0; i < parts.Length; i++)
                values[order[i]] = int.Parse(parts[i]);
            if (!values.TryGetValue('d', out day) || !values.TryGetValue('M', out month))
                return false;
            if (!values.TryGetValue('y', out year))
                year = today.Year;
            else if (parts[Array.IndexOf(order, 'y')].Length <= 2)
                year += year > today.Year % 100 + 20 ? 1900 : 2000;
        }

        if (year is < 1900 or > 2999 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month))
            return false;
        date = new DateOnly(year, month, day);
        return true;
    }

    // "03:00", "3:00", "3", "3 PM" (where the region uses AM/PM).
    public static bool TryParseTime(string? text, CultureInfo culture, out TimeOnly time)
    {
        text = (text ?? "").Trim();
        if (TimeOnly.TryParse(text, culture, DateTimeStyles.None, out time) || TimeOnly.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out time))
            return true;
        if (int.TryParse(text, out var hour) && hour is >= 0 and < 24)
        {
            time = new TimeOnly(hour, 0);
            return true;
        }
        return false;
    }

    // The order of day, month and year in the region's short date pattern, e.g. ['d','M','y'].
    private static char[] Order(CultureInfo culture)
    {
        var pattern = culture.DateTimeFormat.ShortDatePattern;
        return new[] { 'd', 'M', 'y' }.OrderBy(c => pattern.IndexOf(c) is var i && i < 0 ? int.MaxValue : i).ToArray();
    }
}
