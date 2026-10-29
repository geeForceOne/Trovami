namespace Trovami.Core.Models;

// The colors a location can have, e.g. to match a box's lid; app.css defines each as --c-<key>
// for the dark and the light theme.
public static class LocationColors
{
    public static readonly IReadOnlyList<(string Key, string Label)> All =
    [
        ("red", "Red"),
        ("orange", "Orange"),
        ("yellow", "Yellow"),
        ("green", "Green"),
        ("teal", "Teal"),
        ("blue", "Blue"),
        ("purple", "Purple"),
        ("pink", "Pink"),
        ("brown", "Brown"),
        ("gray", "Gray"),
        ("black", "Black"),
        ("white", "White"),
    ];

    public static bool IsKnown(string? key) => All.Any(c => c.Key == key);

    public static string LabelOf(string key) => All.FirstOrDefault(c => c.Key == key).Label ?? key;
}
