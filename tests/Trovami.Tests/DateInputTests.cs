using System.Globalization;
using Trovami.Core.Localization;

namespace Trovami.Tests;

public class DateInputTests
{
    private static readonly DateOnly Today = new(2026, 10, 14);

    [Theory]
    [InlineData("de-CH", "14.10.2026", 2026, 10, 14)]
    [InlineData("de-CH", "1.5.2009", 2009, 5, 1)]
    [InlineData("de-CH", "14.10.26", 2026, 10, 14)]
    [InlineData("de-CH", "1.5.09", 2009, 5, 1)]
    [InlineData("de-CH", "31.12.99", 1999, 12, 31)]
    [InlineData("de-CH", "14.10.", 2026, 10, 14)]
    [InlineData("de-CH", "3.4", 2026, 4, 3)]
    [InlineData("de-CH", "2009-05-01", 2009, 5, 1)]
    [InlineData("en-US", "10/14/2026", 2026, 10, 14)]
    [InlineData("en-US", "5/1", 2026, 5, 1)]
    [InlineData("en-GB", "14/10/2026", 2026, 10, 14)]
    [InlineData("de-CH", "29012016", 2016, 1, 29)]
    [InlineData("de-CH", "290116", 2016, 1, 29)]
    [InlineData("de-CH", "2901", 2026, 1, 29)]
    [InlineData("de-CH", "20160129", 2016, 1, 29)]
    [InlineData("en-US", "01292016", 2016, 1, 29)]
    [InlineData("en-US", "0129", 2026, 1, 29)]
    [InlineData("sv-SE", "20160129", 2016, 1, 29)]
    [InlineData("sv-SE", "160129", 2016, 1, 29)]
    [InlineData("sv-SE", "0129", 2026, 1, 29)]
    [InlineData("sv-SE", "1.29", 2026, 1, 29)]
    public void Parses(string culture, string text, int y, int m, int d)
    {
        Assert.True(DateInput.TryParse(text, CultureInfo.GetCultureInfo(culture), Today, out var date), text);
        Assert.Equal(new DateOnly(y, m, d), date);
    }

    [Theory]
    [InlineData("")]
    [InlineData("14")]
    [InlineData("29132016")]
    [InlineData("291")]
    [InlineData("2901201")]
    [InlineData("31.2.2026")]
    [InlineData("14.13.2026")]
    [InlineData("1.2.3.4")]
    [InlineData("abc")]
    public void Rejects(string text) => Assert.False(DateInput.TryParse(text, CultureInfo.GetCultureInfo("de-CH"), Today, out _), text);

    [Theory]
    [InlineData("de-CH", "03:00", 3, 0)]
    [InlineData("de-CH", "7", 7, 0)]
    [InlineData("de-CH", "23:30", 23, 30)]
    [InlineData("en-US", "3:15 PM", 15, 15)]
    public void ParsesTimes(string culture, string text, int h, int m)
    {
        Assert.True(DateInput.TryParseTime(text, CultureInfo.GetCultureInfo(culture), out var time), text);
        Assert.Equal(new TimeOnly(h, m), time);
    }
}
