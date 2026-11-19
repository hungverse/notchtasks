using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NotchTasks.ControlPanel;
using NotchTasks.Core.Settings;

namespace NotchTasks.Pill;

/// One badge as it sits on the notch, from settings "badges": an animated icon (SpriteView) when it names a sprite that exists,
/// a Segoe Fluent glyph for a "seg:" symbol, and the star for any other symbol (the Mac's SF Symbol names). The fill, shape,
/// border and symbol colour follow the badge's own fields; a badge without a colour fills with the accent, as the Mac does.
/// The pill and the Icons pane's preview draw badges with this, so they look the same.
public static class BadgeGlyph
{
    /// The Segoe Fluent Icons code of the star (Favorite): what a symbol the Windows glyphs do not know draws as.
    public const string StarCode = "E734";

    private const string SegoeFont = "Segoe Fluent Icons, Segoe MDL2 Assets";
    private static readonly Color FallbackFill = Color.FromRgb(0x4D, 0x4D, 0x4D);

    /// The accent of the settings: what a badge without a colour fills with (the pill's own accent).
    public static Color AccentOf(SettingsStore store)
    {
        var hex = Appearance.ResolveAccent(store.Accent, store.CustomAccent, SystemAccent.Hex()) ?? Appearance.FallbackAccent;
        return HexColor.ToColor(hex);
    }

    /// Puts the settings' badges in their wings, replacing the badges already there: a badge with "right" set goes to the
    /// right wing. Returns how many badges there are.
    public static int FillWings(StackPanel left, StackPanel right, JsonArray badges, Color accent)
    {
        left.Children.Clear();
        right.Children.Clear();
        foreach (var node in badges)
        {
            if (node is not JsonObject badge)
            {
                continue;
            }
            var wing = ReadBool(badge, "right") ? right : left;
            var glyph = Create(badge, 18, accent);
            glyph.Margin = new Thickness(3, 0, 3, 0);
            glyph.VerticalAlignment = VerticalAlignment.Center;
            wing.Children.Add(glyph);
        }
        return badges.Count;
    }

    /// The badge at `size` DIP square, without margins (the caller places it).
    public static FrameworkElement Create(JsonObject badge, double size, Color accent)
    {
        var plain = ReadBool(badge, "plain");
        var tint = ColorOf(ReadString(badge, "color")) ?? (accent == default ? FallbackFill : accent);
        var ink = ColorOf(ReadString(badge, "fg")) ?? (plain ? tint : Colors.White);
        var corner = ReadString(badge, "shape") == "rounded" ? size * 0.28 : size / 2;

        var grid = new Grid { Width = size, Height = size };
        grid.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(corner),
            Background = plain ? Brushes.Transparent : Frozen(new SolidColorBrush(tint)),
            BorderBrush = ReadBool(badge, "border") ? Frozen(new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF))) : null,
            BorderThickness = new Thickness(ReadBool(badge, "border") ? 1 : 0),
        });
        grid.Children.Add(Content(badge, size, plain, ink));
        return grid;
    }

    /// The icon inside the fill: a sprite, or a glyph. On a fill it sits a little smaller, as the Mac draws it.
    private static UIElement Content(JsonObject badge, double size, bool plain, Color ink)
    {
        var sprite = ReadString(badge, "sprite");
        var sheet = sprite is null ? null : SpriteCache.Get(sprite);
        if (sheet is not null)
        {
            return new SpriteView(sheet, plain ? size : size * 0.78)
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }

        return new TextBlock
        {
            Text = GlyphText(ReadString(badge, "symbol")),
            FontFamily = new FontFamily(SegoeFont),
            FontSize = size * (plain ? 0.75 : 0.55),
            Foreground = Frozen(new SolidColorBrush(ink)),
            TextAlignment = TextAlignment.Center,
            Width = size,
            Height = size,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    /// The character of a symbol: "seg:XXXX" is that Segoe glyph; anything else (an SF name) is the star.
    internal static string GlyphText(string? symbol)
    {
        if (symbol is not null && symbol.StartsWith("seg:", StringComparison.Ordinal)
            && int.TryParse(symbol.AsSpan(4), NumberStyles.HexNumber, null, out var code)
            && Rune.IsValid(code))
        {
            return new Rune(code).ToString();
        }
        return new Rune(Convert.ToInt32(StarCode, 16)).ToString();
    }

    private static Color? ColorOf(string? hex) =>
        HexColor.TryNormalize(hex, out var normalized) ? HexColor.ToColor(normalized) : null;

    private static string? ReadString(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    private static bool ReadBool(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue(out bool b) && b;

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
