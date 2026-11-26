using System.Text.Json.Nodes;

namespace NotchTasks.Core.Settings;

/// One of the one-click themes (Customize.swift Theme): the look, the toast style and the motion, with the values the settings
/// keep. The sounds and the other settings are not part of a theme, so changing theme keeps them.
public sealed record Theme(
    string Id,
    string TitleKey,
    string Background,
    string GradientA,
    string GradientB,
    string Accent,
    string CustomAccent,
    string ToastStyle,
    string Motion)
{
    /// The settings this theme sets and their values, to be saved in one write (SettingsStore.SetMany).
    public IReadOnlyDictionary<string, JsonNode?> Values => new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
    {
        ["background"] = JsonValue.Create(Background),
        ["gradientA"] = JsonValue.Create(GradientA),
        ["gradientB"] = JsonValue.Create(GradientB),
        ["accent"] = JsonValue.Create(Accent),
        ["customAccent"] = JsonValue.Create(CustomAccent),
        ["toastStyle"] = JsonValue.Create(ToastStyle),
        ["motion"] = JsonValue.Create(Motion),
    };
}

/// The six themes of the Mac, in its order (Customize.swift Theme.all). The values are the Mac's, so a theme looks the same on both.
public static class Themes
{
    public static IReadOnlyList<Theme> All { get; } =
    [
        new("classic", "Classic", "black", "#1E1B4B", "#000000", "auto", "#FF9F0A", "island", "bouncy"),
        new("neon", "Neon", "glow", "#0F172A", "#000000", "custom", "#00F5D4", "card", "bouncy"),
        new("glass", "Glass", "glass", "#334155", "#0F172A", "white", "#FFFFFF", "banner", "smooth"),
        new("minimal", "Minimal", "black", "#111111", "#000000", "white", "#FFFFFF", "compact", "snappy"),
        new("sunset", "Sunset", "gradient", "#FF5F6D", "#5B247A", "custom", "#FFC371", "card", "bouncy"),
        new("ocean", "Ocean", "gradient", "#0EA5E9", "#1E1B4B", "custom", "#7DD3FC", "island", "smooth"),
    ];

    /// True when the store's look is the theme's: the same background, toast style, accent and motion, and the same custom
    /// colour when the accent is custom (the Mac's ThemeTile selection).
    public static bool IsSelected(Theme theme, SettingsStore store) =>
        store.Background == theme.Background
        && store.ToastStyle == theme.ToastStyle
        && store.Accent == theme.Accent
        && store.Motion == theme.Motion
        && (theme.Accent != "custom" || store.CustomAccent == theme.CustomAccent);
}
