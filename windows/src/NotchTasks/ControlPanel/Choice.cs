using System.ComponentModel;
using System.IO;
using System.Windows.Media;
using NotchTasks.Core;
using NotchTasks.Core.Settings;
using NotchTasks.Core.Sounds;
using NotchTasks.Pill;
using NotchTasks.Services;

namespace NotchTasks.ControlPanel;

/// One item of a picker: the value it stores and its title. A title made from a key reads the current language and
/// raises PropertyChanged when the language changes (as PaneItem does); a literal title never changes.
public sealed class Choice : INotifyPropertyChanged
{
    private readonly Func<string> _title;
    private readonly string? _blurbKey;

    private Choice(string value, Func<string> title, bool localized, string? blurbKey, Brush? swatch, bool separator = false)
    {
        Value = value;
        _title = title;
        _blurbKey = blurbKey;
        Swatch = swatch;
        IsSeparator = separator;
        if (localized)
        {
            LocalizedStrings.Instance.PropertyChanged += (_, _) =>
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Title)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Blurb)));
            };
        }
    }

    /// The value stored (or applied): a setting value, a sound name, a language code.
    public string Value { get; }

    /// The text shown. Changes with the language for a key-made choice.
    public string Title => _title();

    /// The explanation under the picker (the toast styles have one), "" when there is none.
    public string Blurb => _blurbKey is null ? string.Empty : L10n.L(_blurbKey);

    /// A colour dot shown before the title (the accents), or null.
    public Brush? Swatch { get; }

    /// True for the line between a picker's sounds and its extra items: drawn as a rule, never chosen.
    public bool IsSeparator { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// A choice whose title is a key of the Mac's English texts.
    public static Choice Key(string value, string titleKey, string? blurbKey = null, Brush? swatch = null) =>
        new(value, () => L10n.L(titleKey), localized: true, blurbKey, swatch);

    /// A choice whose title is a text as it is (a sound name, a language's own name, a file name).
    public static Choice Literal(string value, string text) =>
        new(value, () => text, localized: false, blurbKey: null, swatch: null);

    /// A rule between groups of a picker's items. It has no title and is never applied.
    public static Choice Separator() =>
        new("\u0001separator", () => string.Empty, localized: false, blurbKey: null, swatch: null, separator: true);

    /// A choice whose title is made by a function that reads the language (the Device language, which names the device's language too).
    public static Choice Computed(string value, Func<string> title) =>
        new(value, title, localized: true, blurbKey: null, swatch: null);
}

/// The items of the pickers, made once: a choice's subscription to the language lives as long as the app.
public static class ChoiceItems
{
    public static IReadOnlyList<Choice> Backgrounds { get; } = FromOptions(Choices.Backgrounds);

    public static IReadOnlyList<Choice> Sizes { get; } = FromOptions(Choices.Sizes);

    public static IReadOnlyList<Choice> Motions { get; } = FromOptions(Choices.Motions);

    public static IReadOnlyList<Choice> ToastStyles { get; } = FromOptions(Choices.ToastStyles);

    public static IReadOnlyList<Choice> ExpandModes { get; } = FromOptions(Choices.ExpandModes);

    /// Where a module sits when the notch is collapsed (Customize.swift Placement).
    public static IReadOnlyList<Choice> Placements { get; } =
    [
        Choice.Key("auto", "Automatic"),
        Choice.Key("left", "Left side"),
        Choice.Key("right", "Right side"),
    ];

    /// The accents, each with a dot in its colour: the system accent for "auto", the palette colour otherwise, none for "custom".
    public static IReadOnlyList<Choice> Accents { get; } = Choices.Accents
        .Select(o => Choice.Key(o.Value, o.TitleKey, swatch: AccentSwatch(o.Value)))
        .ToList();

    /// "None" first, then the 14 Mac sound names.
    public static IReadOnlyList<Choice> SoundNames { get; } =
        new[] { Choice.Key(string.Empty, "None") }
            .Concat(SoundFiles.Names.Select(name => Choice.Literal(name, name)))
            .ToList();

    /// The five packs. "Custom" is added to a picker only while the sounds match no pack (CustomPack).
    public static IReadOnlyList<Choice> SoundPacks { get; } =
        NotchTasks.Core.Sounds.SoundPacks.All.Select(p => Choice.Key(p.Name, p.Name)).ToList();

    /// The pack label of a custom map (Preferences.swift: "Yours").
    public static Choice CustomPack { get; } = Choice.Key(string.Empty, "Yours");

    /// The value of the "Choose your file…" item: the pane opens a file dialog instead of saving it.
    public const string ChooseFileValue = "\u0001choose-file";

    public static Choice ChooseFile { get; } = Choice.Key(ChooseFileValue, "Choose your file…");

    public static Choice SoundSeparator { get; } = Choice.Separator();

    /// An event's picker: "None", the 14 sounds, a rule, then "Choose your file…" (Preferences.swift SoundRow).
    public static IReadOnlyList<Choice> SoundPickerItems { get; } = SoundNames.Concat(new[] { SoundSeparator, ChooseFile }).ToList();

    public static IReadOnlyList<Choice> SoundEvents { get; } =
        NotchTasks.Core.Sounds.SoundPacks.Events.Select(e => Choice.Key(e.Id, e.Title)).ToList();

    /// "Device · <the device's language>", then the 16 languages in their own names.
    public static IReadOnlyList<Choice> Languages { get; } =
        new[] { Choice.Computed(string.Empty, () => L10n.L("Device") + " · " + DeviceName()) }
            .Concat(L10n.Languages.Select(l => Choice.Literal(l.Code, l.Name)))
            .ToList();

    private static string DeviceName() =>
        L10n.Languages.Where(l => l.Code == L10n.DeviceLanguage).Select(l => l.Name).DefaultIfEmpty("English").First();

    private static IReadOnlyList<Choice> FromOptions(IEnumerable<Choices.Option> options) =>
        options.Select(o => Choice.Key(o.Value, o.TitleKey, o.BlurbKey)).ToList();

    private static Brush? AccentSwatch(string accent)
    {
        if (accent == "custom")
        {
            return null;
        }
        var hex = Appearance.ResolveAccent(accent, string.Empty, SystemAccent.Hex()) ?? Appearance.FallbackAccent;
        return HexColor.Brush(hex);
    }
}

/// Colours as "#RRGGBB" text, and their brushes.
public static class HexColor
{
    /// The "#RRGGBB" text for a hex colour (the # is optional, the case is free), or false when it is not one.
    public static bool TryNormalize(string? text, out string hex)
    {
        hex = string.Empty;
        var digits = text?.Trim().TrimStart('#');
        if (digits is not { Length: 6 } || !digits.All(Uri.IsHexDigit))
        {
            return false;
        }
        hex = "#" + digits.ToUpperInvariant();
        return true;
    }

    public static System.Windows.Media.Color ToColor(string hex) => System.Windows.Media.Color.FromRgb(
        byte.Parse(hex.AsSpan(1, 2), System.Globalization.NumberStyles.HexNumber),
        byte.Parse(hex.AsSpan(3, 2), System.Globalization.NumberStyles.HexNumber),
        byte.Parse(hex.AsSpan(5, 2), System.Globalization.NumberStyles.HexNumber));

    /// A frozen solid brush of the colour.
    public static SolidColorBrush Brush(string hex)
    {
        var brush = new SolidColorBrush(ToColor(hex));
        brush.Freeze();
        return brush;
    }
}
