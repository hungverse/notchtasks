using System.Text.Json;
using System.Windows.Media;
using NotchTasks.Core.Agents;
using NotchTasks.ControlPanel;

namespace NotchTasks.Pill;

/// The colour of an agent item, shared by its card and its chip (Mac Icons.tint). The order: the agents module's colour, then the
/// item's icon colour (the "color" of its icon JSON), then the chosen accent, then Codex's blue, else a palette pick that is stable
/// per item (the sum of the key's code points, as the Mac does).
/// The Mac's icon-average step is not ported: the Mac averages an app's icon colour, and Windows has no app icon colour to average,
/// so an item's colour comes from the steps above and then the palette.
public static class ItemTint
{
    private static readonly Color CodexBlue = Color.FromRgb(0x6E, 0x8B, 0xFF);  // the Mac's #6E8BFF: Codex's icon reads as red
    /// The palette is the Mac's system colours in their dark appearance: the pill is always dark, so teal is #40C8E0 (the light
    /// appearance's #30B0C7 would be too dim on it).
    private static readonly Color[] Palette =
    {
        Color.FromRgb(0x0A, 0x84, 0xFF),  // blue
        Color.FromRgb(0x40, 0xC8, 0xE0),  // teal
        Color.FromRgb(0x30, 0xD1, 0x58),  // green
        Color.FromRgb(0xFF, 0x37, 0x5F),  // pink
        Color.FromRgb(0xBF, 0x5A, 0xF2),  // purple
        Color.FromRgb(0xFF, 0xD6, 0x0A),  // yellow
        Color.FromRgb(0xFF, 0x9F, 0x0A),  // orange
    };

    /// The item's colour on the pill.
    public static Color Of(WatchItem item, IReadOnlyDictionary<string, string> moduleColors, Color? accent)
    {
        if (moduleColors.TryGetValue("agents", out var hex) && HexColor.TryNormalize(hex, out var module))
        {
            return HexColor.ToColor(module);
        }
        if (IconColor(item) is { } icon)
        {
            return icon;
        }
        if (accent is { } chosen)
        {
            return chosen;
        }
        return item.Kind == WatchKind.Codex ? CodexBlue : PaletteColor(item);
    }

    /// The "color" of the item's icon JSON (settings.js badge object), or null when it has none or it is not a hex colour.
    public static Color? IconColor(WatchItem item)
    {
        if (item.Icon is not { Length: > 0 } json)
        {
            return null;
        }
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("color", out var value)
                && value.ValueKind == JsonValueKind.String
                && HexColor.TryNormalize(value.GetString() ?? string.Empty, out var normalized))
            {
                return HexColor.ToColor(normalized);
            }
        }
        catch (JsonException)
        {
            // an icon that is not JSON has no colour
        }
        return null;
    }

    /// The palette colour for an item: its bundle id, or else its process and arguments, summed by code point.
    private static Color PaletteColor(WatchItem item)
    {
        var key = item.BundleID ?? item.Process + (item.Args ?? string.Empty);
        long sum = 0;
        foreach (var rune in key.EnumerateRunes())
        {
            sum += rune.Value;
        }
        return Palette[(int)(sum % Palette.Length)];
    }
}
