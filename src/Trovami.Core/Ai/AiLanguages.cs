using Trovami.Core.Services;

namespace Trovami.Core.Ai;

// A language the AI can write in, with that language's words for the keywords the app sets itself.
// WritingRule: how the household wants it written, added to what the AI is told.
public sealed record AiLanguage(string Code, string Name, string New, string Unsure, string? WritingRule = null);

// The fixed list a household's owner picks from. Trovami's own pages stay in US English.
public static class AiLanguages
{
    public const string DefaultCode = "en";

    public static readonly AiLanguage English = new(DefaultCode, "US English", ItemKeywords.New, AiNamer.UnsureKeyword);

    public static readonly IReadOnlyList<AiLanguage> All =
    [
        English,
        new("nl", "Dutch", "nieuw", "onzeker"),
        new("fr", "French", "nouveau", "incertain"),
        new("de", "German", "neu", "unsicher", "Always write ss, never the letter \u00DF: weiss, not wei\u00DF."),
        new("it", "Italian", "nuovo", "incerto"),
        new("pt", "Portuguese", "novo", "duvidoso"),
        new("es", "Spanish", "nuevo", "dudoso"),
    ];

    // Everything the AI writes goes through this: it doesn't always keep to the writing rule.
    public static string WithoutEszett(string text) => text.Replace("\u00DF", "ss").Replace("\u1E9E", "SS");

    // Unknown or missing codes count as English.
    public static AiLanguage For(string? code) => All.FirstOrDefault(l => l.Code == code) ?? English;

    private static readonly HashSet<string> NewWords = All.Select(l => l.New).ToHashSet();
    private static readonly HashSet<string> StatusWords = All.SelectMany(l => new[] { l.New, l.Unsure }).ToHashSet();

    // In any language, so nothing has to look up the household just to treat these two differently.
    public static bool IsNew(string word) => NewWords.Contains(word);

    public static bool IsStatus(string word) => StatusWords.Contains(word);
}
