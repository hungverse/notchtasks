namespace NotchTasks.Core.Settings;

/// The values of the look and behavior choices, in the Mac's order (Customize.swift, Settings.swift), each with its English
/// title key and, for the toast styles, its blurb key. The panes show the titles through L10n.
public static class Choices
{
    /// One value of a choice: the stored value, its English title key and an optional blurb key.
    public sealed record Option(string Value, string TitleKey, string? BlurbKey = null);

    public static IReadOnlyList<Option> Backgrounds { get; } =
    [
        new("black", "Black"),
        new("glass", "Frosted glass"),
        new("gradient", "Gradient"),
        new("glow", "Glowing edge"),
    ];

    public static IReadOnlyList<Option> Sizes { get; } =
    [
        new("compact", "Compact"),
        new("regular", "Large"),
    ];

    public static IReadOnlyList<Option> Motions { get; } =
    [
        new("smooth", "Smooth"),
        new("bouncy", "Bouncy"),
        new("snappy", "Snappy"),
        new("reduced", "Minimal"),
    ];

    public static IReadOnlyList<Option> ToastStyles { get; } =
    [
        new("island", "Island", "Slides down from the notch."),
        new("compact", "Compact", "Fits inside the notch."),
        new("card", "Floating card", "A card floating below the notch."),
        new("banner", "Banner", "Wide, large text."),
    ];

    public static IReadOnlyList<Option> ExpandModes { get; } =
    [
        new("hover", "Hover"),
        new("click", "Click"),
    ];

    /// The accent names, as Appearance.AccentNames stores them. "auto" is the Mac's default.
    public static IReadOnlyList<Option> Accents { get; } =
    [
        new("auto", "Match app color"),
        new("blue", "Blue"),
        new("green", "Green"),
        new("orange", "Orange"),
        new("pink", "Pink"),
        new("purple", "Purple"),
        new("white", "White"),
        new("custom", "Custom…"),
    ];
}
