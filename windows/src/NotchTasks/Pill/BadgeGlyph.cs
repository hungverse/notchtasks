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

    /// The badge at `size` DIP square, without margins (the caller places it). See Fill for what it shows.
    public static FrameworkElement Create(JsonObject badge, double size, Color accent)
    {
        var grid = new Grid();
        Fill(grid, badge, size, accent);
        return grid;
    }

    /// Draws the badge into `grid`, replacing what it held. What it shows follows the Mac's NotchBadge: an animation (a sprite, or a
    /// GIF or spritesheet) first, then a picture, then letters, then the symbol. A picture fills the badge and wins over letters.
    /// A picture or GIF is decoded on a background thread: until it is ready the badge shows its fill, and when it is ready the badge
    /// is drawn again into the same grid. Letters widen the badge into a pill, so its width follows them; the rest are size by size.
    private static void Fill(Grid grid, JsonObject badge, double size, Color accent)
    {
        var image = BadgeEdits.ImageOf(badge);
        var animated = BadgeEdits.IsAnimated(badge);
        // "No background" (plain) means none: no fill behind a symbol, no tint, and no shape cut for a picture or an animation.
        // The border is a separate choice (the Border flag), so a plain badge keeps it only when it is ticked.
        var plain = ReadBool(badge, "plain");
        var tint = ColorOf(ReadString(badge, "color")) ?? (accent == default ? FallbackFill : accent);
        var ink = ColorOf(ReadString(badge, "fg")) ?? Colors.White;
        // the shape of the badge: its border follows it even when plain (a plain badge has no fill and no clip, only the border's shape)
        var corner = ReadString(badge, "shape") == "rounded" ? size * 0.28 : size / 2;

        var pending = new List<Task>();
        var (sheet, still) = Art(badge, image, animated, size, pending);
        var text = image is null ? TextOf(ReadString(badge, "text")) : null;

        grid.Children.Clear();
        grid.Clip = null;
        // letters keep the badge's height and never narrow below it: one or two letters sit in a round badge, more widen it
        grid.Width = text is not null ? double.NaN : size;
        grid.MinWidth = text is not null ? size : 0;
        grid.Height = size;

        // A plain badge is drawn with no clip at all: its animation runs at full size, its picture is drawn whole (Uniform).
        // A filled animation is clipped to the badge's shape (or its square when the fill is off).
        if (sheet is not null && !plain)
        {
            grid.Clip = Frozen(new RectangleGeometry(new Rect(0, 0, size, size), corner, corner));
        }

        // A picture is the fill. Filled, it covers the badge and is cut to its shape (the Mac's scaledToFill); plain, the whole
        // file is drawn inside the square on a transparent background.
        Brush fill = plain ? Brushes.Transparent
            : still is not null ? Frozen(new ImageBrush(still) { Stretch = Stretch.UniformToFill })
            : Frozen(new SolidColorBrush(tint));
        grid.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(corner),
            Background = fill,
            BorderBrush = ReadBool(badge, "border") ? Frozen(new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF))) : null,
            BorderThickness = new Thickness(ReadBool(badge, "border") ? 1 : 0),
        });
        if (still is not null && plain)
        {
            // a plain picture is drawn whole in its own Image, not as the Border's brush, so the Border's shape never cuts it
            grid.Children.Add(new System.Windows.Controls.Image
            {
                Source = still,
                Stretch = Stretch.Uniform,
                Width = size,
                Height = size,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        if (sheet is not null)
        {
            // on a fill the animation sits a little smaller, as the Mac draws it
            grid.Children.Add(new SpriteView(sheet, plain ? size : size * 0.78)
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
        else if (pending.Count == 0 && still is null)
        {
            grid.Children.Add(text is not null ? Letters(text, size, ink) : Content(badge, size, plain, ink));
        }

        if (pending.Count > 0)
        {
            // draw the badge again once what it waits for has been decoded (on the UI thread, into this same grid)
            // Fill reads a task's result only when it completed successfully, so a faulted or cancelled decode draws the fallback
            _ = Task.WhenAll(pending).ContinueWith(
                done => grid.Dispatcher.BeginInvoke(new Action(() => Fill(grid, badge, size, accent))),
                TaskScheduler.Default);
        }
    }

    /// The animation or picture a badge shows: a built-in sprite; else its GIF or spritesheet when it animates (a GIF that is refused
    /// shows the symbol, not its still); else its picture still. A decode still running is added to pending, and the result is null
    /// for now. A missing or refused file gives null, and the badge then falls through to letters or the symbol.
    private static (SpriteSheet? Sheet, ImageSource? Still) Art(JsonObject badge, string? image, bool animated, double size,
        List<Task> pending)
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
        if (animated)
        {
            var animation = BadgeMedia.AnimationAsync(image, ReadInt(badge, "frames"), ReadDouble(badge, "fps"), size);
            if (!animation.IsCompleted)
            {
                pending.Add(animation);
                return (null, null);
            }
            return (animation.IsCompletedSuccessfully ? animation.Result : null, null);  // a faulted task shows nothing, never throws
        }
        var still = BadgeMedia.StillAsync(image, size);
        if (!still.IsCompleted)
        {
            pending.Add(still);
            return (null, null);
        }
        return (null, still.IsCompletedSuccessfully ? still.Result : null);
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
        var symbol = ReadString(badge, "symbol");

        // the glyph is centred in its box by FluentGlyph (a TextBlock would sit it on the baseline)
        return new FluentGlyph
        {
            Code = GlyphText(symbol),
            Filled = symbol is not null && !symbol.StartsWith("seg:", StringComparison.Ordinal) && ModuleGlyphs.IsFilled(symbol),
            Size = size * (plain ? 0.75 : 0.55),
            Foreground = Frozen(new SolidColorBrush(ink)),
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
        if (symbol is not null && !symbol.StartsWith("seg:", StringComparison.Ordinal) && ModuleGlyphs.Has(symbol))
        {
            return ModuleGlyphs.Get(symbol).Glyph;  // a Mac symbol the Fluent map names (hammer.fill, server.rack, ...)
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
