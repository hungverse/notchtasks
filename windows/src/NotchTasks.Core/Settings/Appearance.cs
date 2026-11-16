namespace NotchTasks.Core.Settings;

/// Accent names as the settings file stores them, with the hex values of Apple's system colors (the Electron build's palette).
public static class Appearance
{
    /// Used for "auto" when the app cannot read the Windows accent.
    public const string FallbackAccent = "#0A84FF";

    public static IReadOnlyCollection<string> AccentNames { get; } =
        new[] { "auto", "blue", "green", "orange", "pink", "purple", "white", "custom" };

    private static readonly IReadOnlyDictionary<string, string> Palette = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["blue"] = "#0A84FF",
        ["green"] = "#30D158",
        ["orange"] = "#FF9F0A",
        ["pink"] = "#FF375F",
        ["purple"] = "#BF5AF2",
        ["white"] = "#FFFFFF",
    };

    /// "auto" follows the system accent (or FallbackAccent), "custom" uses the custom hex, a palette name gives its hex.
    /// Returns null for a name that is not in the palette.
    public static string? ResolveAccent(string accent, string custom, string? systemAccentHex)
    {
        if (accent == "auto") return systemAccentHex ?? FallbackAccent;
        if (accent == "custom") return custom;
        return Palette.TryGetValue(accent, out var hex) ? hex : null;
    }
}
