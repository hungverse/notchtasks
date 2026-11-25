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

    /// The Tag of a badge's element in a wing: what a fill tells a badge from a chip by.
    public const string BadgeTag = "badge";

    /// The badge's id (or its place, when it has none), and what it was drawn from (its settings, size and accent).
    private static readonly DependencyProperty KeyProperty = DependencyProperty.RegisterAttached(
        "BadgeKey", typeof(string), typeof(BadgeGlyph), new PropertyMetadata(null));
    private static readonly DependencyProperty SignatureProperty = DependencyProperty.RegisterAttached(
        "BadgeSignature", typeof(string), typeof(BadgeGlyph), new PropertyMetadata(null));

    /// Puts the settings' badges in their wings, a badge with "right" set in the right wing, at `size` DIP (the closed pill's icon size).
    /// Badges are the outer edge of their wing; ChipView puts the chips inside them. A badge whose id is new to the pill oozes in
    /// (PillMotion.Ooze); a badge already there and unchanged stays as it is, with no motion, as does one whose settings changed (it is
    /// drawn again in its place); a badge that is gone is removed. Returns how many badges there are.
    public static int FillWings(StackPanel left, StackPanel right, JsonArray badges, Color accent, double size = 18,
        string motion = "smooth", bool animationsOn = true)
    {
        var heldLeft = Held(left);
        var heldRight = Held(right);
        var known = new HashSet<string>(heldLeft.Keys.Concat(heldRight.Keys), StringComparer.Ordinal);
        var sides = new[]
        {
            (Wing: left, Right: false, Held: heldLeft, Items: new List<FrameworkElement>()),
            (Wing: right, Right: true, Held: heldRight, Items: new List<FrameworkElement>()),
        };
        var oozing = new List<(FrameworkElement Element, bool Right)>();
        var count = 0;
        foreach (var node in badges)
        {
            if (node is not JsonObject badge)
            {
                continue;
            }
            var side = sides[ReadBool(badge, "right") ? 1 : 0];
            var key = ReadString(badge, "id") ?? "#" + count.ToString(CultureInfo.InvariantCulture);
            count++;
            var signature = badge.ToJsonString() + "|" + size.ToString(CultureInfo.InvariantCulture) + "|" + accent;
            if (side.Held.TryGetValue(key, out var held) && Equals(held.GetValue(SignatureProperty), signature))
            {
                side.Items.Add(held);  // unchanged: kept as it is
                continue;
            }
            var glyph = Create(badge, size, accent);
            glyph.Margin = new Thickness(3, 0, 3, 0);
            glyph.VerticalAlignment = VerticalAlignment.Center;
            glyph.Tag = BadgeTag;
            glyph.SetValue(KeyProperty, key);
            glyph.SetValue(SignatureProperty, signature);
            side.Items.Add(glyph);
            if (!known.Contains(key))
            {
                oozing.Add((glyph, side.Right));  // a new id: it comes in (a changed badge is drawn again without motion)
            }
        }

        foreach (var side in sides)
        {
            var wing = side.Wing;
            foreach (var child in wing.Children.OfType<FrameworkElement>().Where(e => e.Tag is BadgeTag).ToList())
            {
                if (!side.Items.Contains(child))
                {
                    wing.Children.Remove(child);
                }
            }
            // The badges sit first in a left wing, and after its chips in a right wing (the chips go inside the badges).
            var first = side.Right ? wing.Children.OfType<FrameworkElement>().Count(e => e.Tag is not BadgeTag) : 0;
            for (var i = 0; i < side.Items.Count; i++)
            {
                var element = side.Items[i];
                var current = wing.Children.IndexOf(element);
                if (current == first + i)
                {
                    continue;
                }
                if (current >= 0)
                {
                    wing.Children.RemoveAt(current);
                }
                wing.Children.Insert(first + i, element);
            }
        }
        foreach (var (element, inRightWing) in oozing)
        {
            PillMotion.Ooze(element, leftWing: !inRightWing, motion, animationsOn);
        }
        return count;
    }

    /// The badges a wing holds, by key.
    private static Dictionary<string, FrameworkElement> Held(StackPanel wing)
    {
        var held = new Dictionary<string, FrameworkElement>(StringComparer.Ordinal);
        foreach (var element in wing.Children.OfType<FrameworkElement>().Where(e => e.Tag is BadgeTag))
        {
            if (element.GetValue(KeyProperty) is string key)
            {
                held[key] = element;
            }
        }
        return held;
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

        // A Mac symbol name that has a drawn mark (the Build & CI tools' defaults: hammer.fill, server.rack, ...) draws that mark,
        // as the Build & CI rows draw it, rather than the star.
        var symbol = ReadString(badge, "symbol");
        if (symbol is not null && !symbol.StartsWith("seg:", StringComparison.Ordinal) && ModuleGlyphs.Vector(symbol) is { } data)
        {
            var box = size * (plain ? 0.75 : 0.55);
            return new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse(data),
                Stroke = Frozen(new SolidColorBrush(ink)),
                StrokeThickness = 1.4,
                Stretch = Stretch.Uniform,
                Width = box,
                Height = box,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }

        return new TextBlock
        {
            Text = GlyphText(symbol),
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
