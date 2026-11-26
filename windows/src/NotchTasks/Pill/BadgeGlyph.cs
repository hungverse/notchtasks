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
/// a Fluent glyph for a "seg:" symbol, and the star for any other symbol (the Mac's SF Symbol names). The fill, shape,
/// border and symbol colour follow the badge's own fields; a badge without a colour fills with the accent, as the Mac does.
/// The pill and the Icons pane's preview draw badges with this, so they look the same.
public static class BadgeGlyph
{
    /// The Fluent code of the star (star_20_regular): what a symbol the badge list does not know draws as.
    public const string StarCode = "F70F";

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
        var animate = animationsOn && motion != "reduced";  // reduced motion: a GIF or sprite stands on its first frame
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
            var signature = badge.ToJsonString() + "|" + size.ToString(CultureInfo.InvariantCulture) + "|" + accent + "|" + (animate ? "a" : "s");
            if (side.Held.TryGetValue(key, out var held) && Equals(held.GetValue(SignatureProperty), signature))
            {
                side.Items.Add(held);  // unchanged: kept as it is
                continue;
            }
            var glyph = Create(badge, size, accent, animate);
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

    /// The badge at `size` DIP square, without margins (the caller places it). What it shows follows the Mac's NotchBadge: an
    /// animation (a sprite, or a GIF or spritesheet) first, then a picture, then letters, then the symbol. A picture fills the badge
    /// and wins over letters. An animation runs unless animate is false (reduced motion), when it stands on its first frame.
    /// Letters widen the badge into a pill, so its width follows them; the other kinds are size by size.
    public static FrameworkElement Create(JsonObject badge, double size, Color accent, bool animate = true)
    {
        var image = BadgeEdits.ImageOf(badge);
        var animated = BadgeEdits.IsAnimated(badge);
        // a fill is turned off for an animation (it stands on the notch) and for a symbol or letters; a picture keeps its own fill
        var plain = ReadBool(badge, "plain") && (image is null || animated);
        var tint = ColorOf(ReadString(badge, "color")) ?? (accent == default ? FallbackFill : accent);
        var ink = ColorOf(ReadString(badge, "fg")) ?? (plain ? tint : Colors.White);
        var corner = ReadString(badge, "shape") == "rounded" ? size * 0.28 : size / 2;

        var (sheet, still) = Art(badge, image, animated, size);
        var text = image is null ? TextOf(ReadString(badge, "text")) : null;

        var grid = new Grid();
        if (sheet is not null)
        {
            // an animation is clipped to the badge's shape, or to its square when it has no fill (as the Mac does)
            grid.Width = size;
            grid.Height = size;
            var radius = plain ? 0 : corner;
            grid.Clip = Frozen(new RectangleGeometry(new Rect(0, 0, size, size), radius, radius));
        }
        else if (still is not null)
        {
            grid.Width = size;
            grid.Height = size;
        }
        else if (text is not null)
        {
            // letters keep the badge's height and never narrow below it: one or two letters sit in a round badge, more widen it
            grid.MinWidth = size;
            grid.Height = size;
        }
        else
        {
            grid.Width = size;
            grid.Height = size;
        }

        // a picture is the fill: the file scaled to cover the badge and cut to its shape, as the Mac's scaledToFill draws it
        Brush fill = still is not null
            ? Frozen(new ImageBrush(still) { Stretch = Stretch.UniformToFill })
            : plain ? Brushes.Transparent : Frozen(new SolidColorBrush(tint));
        grid.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(corner),
            Background = fill,
            BorderBrush = ReadBool(badge, "border") ? Frozen(new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF))) : null,
            BorderThickness = new Thickness(ReadBool(badge, "border") ? 1 : 0),
        });

        if (sheet is not null)
        {
            // on a fill the animation sits a little smaller, as the Mac draws it
            grid.Children.Add(new SpriteView(sheet, plain ? size : size * 0.78, still: !animate)
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
        else if (still is null)
        {
            grid.Children.Add(text is not null ? Letters(text, size, ink) : Content(badge, size, plain, ink));
        }
        return grid;
    }

    /// The animation or picture a badge shows: a built-in sprite; else its GIF or spritesheet when it animates; else its picture
    /// still. Each is null when it is not there or cannot be read, and the badge then falls through to letters or the symbol.
    private static (SpriteSheet? Sheet, ImageSource? Still) Art(JsonObject badge, string? image, bool animated, double size)
    {
        var sprite = ReadString(badge, "sprite");
        if (sprite is not null && SpriteCache.Get(sprite) is { } builtIn)
        {
            return (builtIn, null);
        }
        if (image is null)
        {
            return (null, null);
        }
        if (animated && BadgeMedia.Animation(image, ReadInt(badge, "frames"), ReadDouble(badge, "fps"), size) is { } frames)
        {
            return (frames, null);
        }
        return (null, BadgeMedia.Still(image, size));
    }

    /// Letters in the badge's ink. One or two letters sit centred; more get side room, so the badge widens into a pill.
    private static FrameworkElement Letters(string text, double size, Color ink)
    {
        var wide = new StringInfo(text).LengthInTextElements > 2;
        var side = wide ? size * 0.25 : 0;
        return new TextBlock
        {
            Text = text,
            FontWeight = FontWeights.Bold,
            FontSize = size * 0.5,
            Foreground = Frozen(new SolidColorBrush(ink)),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.NoWrap,
            Margin = new Thickness(side, 0, side, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    /// The icon inside the fill: a glyph, or a drawn mark. On a fill it sits a little smaller, as the Mac draws it.
    private static UIElement Content(JsonObject badge, double size, bool plain, Color ink)
    {
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
            FontFamily = FluentIcons.Regular,
            FontSize = size * (plain ? 0.75 : 0.55),
            Foreground = Frozen(new SolidColorBrush(ink)),
            TextAlignment = TextAlignment.Center,
            Width = size,
            Height = size,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    /// The character of a symbol: "seg:XXXX" is the Fluent icon that code was stored as (ModuleGlyphs.Legacy), so a badge saved
    /// before the Fluent icons keeps its look. Anything else (an SF name, or a code the list never offered) is the star.
    internal static string GlyphText(string? symbol)
    {
        if (symbol is not null && symbol.StartsWith("seg:", StringComparison.Ordinal)
            && ModuleGlyphs.Legacy(symbol[4..]) is { } glyph)
        {
            return glyph;
        }
        return new Rune(Convert.ToInt32(StarCode, 16)).ToString();
    }

    private static string? TextOf(string? text) => string.IsNullOrEmpty(text) ? null : text;

    /// A number field as an int (the Mac writes frames as a whole number; Windows may store it as a double).
    private static int? ReadInt(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue(out double d) ? (int)Math.Round(d) : null;

    private static double? ReadDouble(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue(out double d) ? d : null;

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
