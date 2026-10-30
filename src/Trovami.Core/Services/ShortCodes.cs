using System.Security.Cryptography;

namespace Trovami.Core.Services;

// The short codes in the web app's addresses (/l/K7F3QZ, /i/M2X9TA). Ids stay GUIDs, since the
// phone makes them offline; a code is given by the server when the location or item arrives, and
// only ever shows up in a URL.
public static class ShortCodes
{
    public const int Length = 6;

    // Easy to tell apart on paper: no 0/O, 1/I/L. Tag codes use it too.
    public const string Alphabet = "23456789ABCDEFGHJKMNPQRSTUVWXYZ";

    public static string New(int length = Length) =>
        new(Enumerable.Range(0, length).Select(_ => Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]).ToArray());

    // Codes are upper case; one typed or pasted in lower case still works.
    public static string? Normalize(string? code) =>
        code is { Length: Length } && code.ToUpperInvariant() is var upper && upper.All(Alphabet.Contains) ? upper : null;

    // One that isn't taken yet. With about 890 million codes a clash is rare, so a few tries are plenty;
    // the unique index catches the rest.
    internal static async Task<string> NewUniqueAsync(Func<string, Task<bool>> taken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var code = New();
            if (attempt >= 9 || !await taken(code))
                return code;
        }
    }
}
