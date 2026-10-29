using Trovami.Core.Services;

namespace Trovami.Tests;

public class SearchTests
{
    [Theory]
    [InlineData("Schlüssel", "schlussel")]
    [InlineData("Café", "cafe")]
    [InlineData("Straße", "strasse")]
    public void IgnoresCaseAndAccents(string text, string normalized) =>
        Assert.Equal(normalized, Search.Normalize(text));

    [Fact]
    public void SplitsIntoDistinctWords() =>
        Assert.Equal(["cellar", "drill"], Search.Words("  Cellar drill, cellar "));
}
