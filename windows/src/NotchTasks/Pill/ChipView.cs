using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NotchTasks.ControlPanel;
using NotchTasks.Core;
using NotchTasks.Core.Notch;

namespace NotchTasks.Pill;

/// The closed pill's chips as they sit in the wings (Mac ChipView). A chip's lead is its glyph; its trail is its text: the
/// ask's words, the timer's time left, and the call's time so far (green, red while muted). A sensor chip has its glyphs only.
public static class ChipView
{
    private const string SegoeFont = "Segoe Fluent Icons, Segoe MDL2 Assets";
    private const string ChipTag = "chip";  // the Tag of a chip's panel in a wing: what a rebuild removes
    private static readonly Color AskOrange = Color.FromRgb(0xFF, 0x9F, 0x0A);
    private static readonly Color CallGreen = Color.FromRgb(0x30, 0xD1, 0x58);  // the Mac's green: a call's time
    private static readonly Color CallRed = Color.FromRgb(0xFF, 0x45, 0x3A);    // the Mac's red: a call's time while muted

    /// One chip as a panel for a wing: its lead, then its trail. Null when the chip has neither to show.
    public static FrameworkElement? Panel(Chip chip, Color? accent, IReadOnlyDictionary<string, string> moduleColors,
        DateTimeOffset now, bool lead, bool trail)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0, 3, 0) };
        if (lead && Lead(chip, 18, accent, moduleColors) is { } leadElement)
        {
            panel.Children.Add(leadElement);
        }
        if (trail && Trail(chip, 11, accent, moduleColors, now) is { } trailElement)
        {
            panel.Children.Add(trailElement);
        }
        return panel.Children.Count == 0 ? null : panel;
    }

    /// The chip's glyph in its tint: the timer's, the call's (phone, video or muted mic) and the sensor's (mic and/or camera).
    /// An ask has no glyph here: its trail is its text.
    public static FrameworkElement? Lead(Chip chip, double size, Color? accent, IReadOnlyDictionary<string, string> moduleColors)
    {
        var brush = HexColor.Brush(ToHex(TintOf(chip, accent, moduleColors)));
        if (chip.Kind == ChipKind.Sensor)
        {
            var sensors = chip.Sensors ?? new SensorState(Array.Empty<string>(), false, false);
            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (sensors.Mic)
            {
                row.Children.Add(Glyph("mic", size, brush));
            }
            if (sensors.Camera)
            {
                row.Children.Add(Glyph("camera", size, brush));
            }
            return row.Children.Count == 0 ? null : row;
        }
        var name = chip.Kind switch
        {
            ChipKind.Timer => "timer",
            ChipKind.Call => chip.Call is { Muted: true } ? "micoff" : chip.Call is { Video: true } ? "video" : "phone",
            _ => null,
        };
        return name is null ? null : Glyph(name, size, brush);
    }

    /// A glyph in a brush, from the Segoe Fluent Icons font.
    private static TextBlock Glyph(string name, double size, Brush brush) => new()
    {
        Text = ModuleGlyphs.Get(name),
        FontFamily = new FontFamily(SegoeFont),
        FontSize = size,
        Foreground = brush,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// The chip's text: "Waiting for you" (one ask) or "{0} waiting" (more) in orange, a running timer's time left, and a call's
    /// time so far in green (red while the mic is muted). Null for a chip with no text (the sensor).
    public static FrameworkElement? Trail(Chip chip, double size, Color? accent, IReadOnlyDictionary<string, string> moduleColors, DateTimeOffset now)
    {
        switch (chip.Kind)
        {
            case ChipKind.Ask:
                return new TextBlock
                {
                    Text = chip.AskCount == 1
                        ? L10n.L("Waiting for you")
                        : L10n.L("{0} waiting", chip.AskCount.ToString(CultureInfo.InvariantCulture)),
                    Foreground = HexColor.Brush(ToHex(AskOrange)),
                    FontSize = 11,
                    FontWeight = FontWeights.Bold,
                    Margin = new Thickness(6, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
            case ChipKind.Timer when chip.Timer is { } timer:
                return new TextBlock
                {
                    Text = TimerState.Mmss(timer.End - now),
                    Foreground = HexColor.Brush(ToHex(TintOf(chip, accent, moduleColors))),
                    FontSize = size + 1,
                    FontWeight = FontWeights.Bold,
                    Margin = new Thickness(6, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
            case ChipKind.Call when chip.Call is { } call:
                return new TextBlock
                {
                    Text = TimerState.Mmss(now - call.Since),
                    Foreground = new SolidColorBrush(call.Muted ? CallRed : CallGreen),
                    FontSize = size,
                    FontWeight = FontWeights.Bold,
                    Margin = new Thickness(6, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
            default:
                return null;
        }
    }

    /// Fills the wings with the closed pill's chips, after the badges already in them. A lone chip shows its lead on the left
    /// and its trail on the right; the others sit whole in the wing ChipBoard.Sides puts them in. Returns how many chips there are.
    public static int FillWings(StackPanel left, StackPanel right, AppState state, Color? accent)
    {
        RemoveChips(left);
        RemoveChips(right);
        var chips = ChipBoard.Chips(state.Snapshot);
        var (leftChips, rightChips, single) = ChipBoard.Sides(chips, state.Settings.Placements);
        var colors = state.Settings.ModuleColors;
        var now = DateTimeOffset.Now;
        if (single is { } lone)
        {
            AddTo(left, lone, accent, colors, now, lead: true, trail: false);
            AddTo(right, lone, accent, colors, now, lead: false, trail: true);
        }
        foreach (var chip in leftChips)
        {
            AddTo(left, chip, accent, colors, now, lead: true, trail: true);
        }
        foreach (var chip in rightChips)
        {
            AddTo(right, chip, accent, colors, now, lead: true, trail: true);
        }
        return chips.Count;
    }

    private static void AddTo(StackPanel wing, Chip chip, Color? accent, IReadOnlyDictionary<string, string> colors,
        DateTimeOffset now, bool lead, bool trail)
    {
        if (Panel(chip, accent, colors, now, lead, trail) is { } panel)
        {
            panel.Tag = ChipTag;
            wing.Children.Add(panel);
        }
    }

    private static void RemoveChips(StackPanel wing)
    {
        for (var i = wing.Children.Count - 1; i >= 0; i--)
        {
            if (wing.Children[i] is FrameworkElement { Tag: ChipTag })
            {
                wing.Children.RemoveAt(i);
            }
        }
    }

    /// The chip's colour: its module's colour when it has one, else the chosen accent (null for "auto", as the Mac's accentColor
    /// is nil then), else the chip's own default (the timer's is orange). The ask chip is always orange, as the Mac's.
    public static Color TintOf(Chip chip, Color? accent, IReadOnlyDictionary<string, string> moduleColors)
    {
        if (chip.Kind == ChipKind.Ask)
        {
            return AskOrange;
        }
        if (moduleColors.TryGetValue(chip.Module, out var hex) && HexColor.TryNormalize(hex, out var normalized))
        {
            return HexColor.ToColor(normalized);
        }
        return accent ?? DefaultTint(chip.Kind);
    }

    private static Color DefaultTint(ChipKind kind) => kind switch
    {
        ChipKind.Call => Color.FromRgb(0x30, 0xD1, 0x58),
        ChipKind.Sensor => AskOrange,
        ChipKind.Timer => AskOrange,
        _ => Color.FromRgb(0x32, 0xD2, 0xFF),
    };

    private static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
}
