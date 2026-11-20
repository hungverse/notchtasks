using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using NotchTasks.ControlPanel;
using NotchTasks.Core;
using NotchTasks.Core.Agents;
using NotchTasks.Core.Limits;
using NotchTasks.Core.Notch;
using NotchTasks.Pill.Sections;

namespace NotchTasks.Pill;

/// The closed pill's chips as they sit in the wings (Mac ChipView). A chip's lead is its glyph; its trail is its text: the
/// ask's words, the timer's time left, the call's time so far (green, red while muted), an agent's working count and a limit's
/// percentage (or its wait). A sensor chip has its glyphs only.
public static class ChipView
{
    private const string SegoeFont = "Segoe Fluent Icons, Segoe MDL2 Assets";
    private static readonly FontFamily SegoeFamily = new(SegoeFont);  // made once: a FontFamily per glyph was a cost each rebuild
    private const string ChipTag = "chip";  // the Tag of a chip's panel in a wing: what a rebuild removes
    private static readonly Color AskOrange = Color.FromRgb(0xFF, 0x9F, 0x0A);
    private static readonly Color CallGreen = Color.FromRgb(0x30, 0xD1, 0x58);  // the Mac's green: a call's time
    private static readonly Color CallRed = Color.FromRgb(0xFF, 0x45, 0x3A);    // the Mac's red: a call's time while muted
    private static readonly Color AlertRed = Color.FromRgb(0xFF, 0x45, 0x3A);   // the Mac's red: a failed CI alert (the alerts chip)

    /// One chip as a panel for a wing: its lead, then its trail. Null when the chip has neither to show. toolIcons are the settings'
    /// badges by tool, which a build chip's lead uses.
    public static FrameworkElement? Panel(Chip chip, Color? accent, IReadOnlyDictionary<string, string> moduleColors,
        DateTimeOffset now, bool lead, bool trail, IProcessReader? processes = null, IReadOnlyDictionary<string, JsonObject>? toolIcons = null)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0, 3, 0) };
        if (lead && Lead(chip, 18, accent, moduleColors, processes, toolIcons) is { } leadElement)
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
    /// An agent's lead is its item icon; a build chip's is its job's (BuildLead). The alerts chip has the warning glyph. A limit's is
    /// a gauge, or an hourglass at 100 %, in its own colour. An ask has no glyph here: its trail is its text.
    public static FrameworkElement? Lead(Chip chip, double size, Color? accent, IReadOnlyDictionary<string, string> moduleColors,
        IProcessReader? processes = null, IReadOnlyDictionary<string, JsonObject>? toolIcons = null)
    {
        if (chip.Kind == ChipKind.Alerts)
        {
            return Glyph("warning", size, HexColor.Brush(ToHex(TintOf(chip, accent, moduleColors))));
        }
        if (chip.Kind == ChipKind.Agent && chip.Module == "ci" && chip.Agent is { } build)
        {
            return BuildLead(build, size, TintOf(chip, accent, moduleColors), processes, toolIcons);
        }
        if (chip.Kind == ChipKind.Agent && chip.Agent is { } agent)
        {
            return ItemIcon.Element(agent, size, TintOf(chip, accent, moduleColors), processes);
        }
        if (chip.Kind == ChipKind.Limit && chip.Limit is { } limit)
        {
            return Vector(limit.W.Used >= 100 ? "hourglass" : "gauge", size, new SolidColorBrush(LimitColor(limit.W.Used)));
        }
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

    /// A build chip's lead (Mac: the chip's JobIcon): the first job's tool badge when its tool has one in settings, else the job's
    /// symbol, else the item's icon.
    private static FrameworkElement BuildLead(ItemState build, double size, Color tint, IProcessReader? processes,
        IReadOnlyDictionary<string, JsonObject>? toolIcons)
    {
        var job = build.Jobs.Count > 0 ? build.Jobs[0] : null;
        if (job?.Tool is { Length: > 0 } tool && toolIcons is not null && toolIcons.TryGetValue(tool, out var badge))
        {
            return BadgeGlyph.Create(badge, size, tint);
        }
        if (JobLead.Element(job?.Symbol, size, HexColor.Brush(ToHex(tint))) is { } symbol)
        {
            return symbol;
        }
        return ItemIcon.Element(build, size, tint, processes);
    }

    /// A glyph in a brush, from the Segoe Fluent Icons font.
    private static TextBlock Glyph(string name, double size, Brush brush) => new()
    {
        Text = ModuleGlyphs.Get(name),
        FontFamily = SegoeFamily,
        FontSize = size,
        Foreground = brush,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// A drawn glyph (the gauge and the hourglass, which the font does not have) in a brush, in a size by size box.
    private static Path Vector(string name, double size, Brush brush) => new()
    {
        Data = Geometry.Parse(ModuleGlyphs.Vector(name) ?? string.Empty),
        Stroke = brush,
        StrokeThickness = 1.5,
        Stretch = Stretch.Uniform,
        Width = size,
        Height = size,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// A limit's colour: red from 95 % used, else orange (the Mac's `w.used >= 95 ? .red : .orange`).
    public static Color LimitColor(double used) => used >= 95 ? CallRed : AskOrange;

    /// The chip's text: "Waiting for you" (one ask) or "{0} waiting" (more) in orange, a running timer's time left, a call's time
    /// so far in green (red while the mic is muted), an agent's working count in its tint, and a limit's percentage, or "Waiting"
    /// and its reset time at 100 %. Null for a chip with no text (the sensor).
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
                    Foreground = new SolidColorBrush(CallTimeTint(call.Muted, moduleColors)),
                    FontSize = size,
                    FontWeight = FontWeights.Bold,
                    Margin = new Thickness(6, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
            case ChipKind.Agent when chip.Agent is { } agent:
                return new TextBlock
                {
                    Text = agent.Jobs.Count(j => j.Working).ToString(CultureInfo.InvariantCulture),
                    Foreground = HexColor.Brush(ToHex(TintOf(chip, accent, moduleColors))),
                    FontSize = size,
                    FontWeight = FontWeights.Bold,
                    Margin = new Thickness(6, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
            case ChipKind.Limit when chip.Limit is { } limit:
                return LimitTrail(limit, size, now);
            case ChipKind.Alerts:
                return new TextBlock
                {
                    Text = chip.AlertCount == 1
                        ? L10n.L("1 error")
                        : L10n.L("{0} errors", chip.AlertCount.ToString(CultureInfo.InvariantCulture)),
                    Foreground = HexColor.Brush(ToHex(TintOf(chip, accent, moduleColors))),
                    FontSize = size,
                    FontWeight = FontWeights.Bold,
                    Margin = new Thickness(6, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
            default:
                return null;
        }
    }

    /// A limit's trail (Mac NotchView .limit): when you can go again at 100 %, else the percentage used; the tooltip names the limit.
    private static TextBlock LimitTrail(LimitChip limit, double size, DateTimeOffset now)
    {
        var used = limit.W.Used;
        return new TextBlock
        {
            Text = used >= 100
                ? L10n.L("Waiting ") + Limit.ResetText(limit.W, now, CultureInfo.CurrentCulture, TimeZoneInfo.Local)
                : $"{(int)used}%",
            Foreground = new SolidColorBrush(LimitColor(used)),
            FontSize = size,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(6, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = L10n.L("{0} used {1}% of its {2} limit", limit.Name, (int)used, limit.W.Label),
        };
    }

    /// Fills the wings with the closed pill's chips, inside the badges already in them: a badge is always the outermost item of
    /// its wing. The left wing reads badges then chips; the right wing reads chips then badges. A lone chip shows its lead on the
    /// left and its trail on the right; the others sit whole in the wing ChipBoard.Sides puts them in. Returns how many chips there are.
    public static int FillWings(StackPanel left, StackPanel right, AppState state, Color? accent) =>
        Fill(left, right, ChipBoard.Chips(state.Snapshot), state.Settings.Placements, state.Settings.ModuleColors, accent,
            DateTimeOffset.Now, state.Processes, state.Settings.ToolIcons);

    /// FillWings with its inputs given: the same placement, order and badge rule. Returns how many chips there are.
    public static int Fill(StackPanel left, StackPanel right, IReadOnlyList<Chip> chips, IReadOnlyDictionary<string, string> placements,
        IReadOnlyDictionary<string, string> colors, Color? accent, DateTimeOffset now, IProcessReader? processes,
        IReadOnlyDictionary<string, JsonObject>? toolIcons = null)
    {
        RemoveChips(left);
        RemoveChips(right);
        var (leftChips, rightChips, single) = ChipBoard.Sides(chips, placements);
        if (single is { } lone)
        {
            AddTo(left, lone, accent, colors, now, lead: true, trail: false, rightWing: false, processes, toolIcons);
            AddTo(right, lone, accent, colors, now, lead: false, trail: true, rightWing: true, processes, toolIcons);
        }
        foreach (var chip in leftChips)
        {
            AddTo(left, chip, accent, colors, now, lead: true, trail: true, rightWing: false, processes, toolIcons);
        }
        foreach (var chip in rightChips)
        {
            AddTo(right, chip, accent, colors, now, lead: true, trail: true, rightWing: true, processes, toolIcons);
        }
        return chips.Count;
    }

    /// Adds a chip's panel to a wing. A left wing's chips go after its badges; a right wing's chips go before its badges, after
    /// the chips already there, so their order holds.
    private static void AddTo(StackPanel wing, Chip chip, Color? accent, IReadOnlyDictionary<string, string> colors,
        DateTimeOffset now, bool lead, bool trail, bool rightWing, IProcessReader? processes,
        IReadOnlyDictionary<string, JsonObject>? toolIcons)
    {
        if (Panel(chip, accent, colors, now, lead, trail, processes, toolIcons) is { } panel)
        {
            panel.Tag = ChipTag;
            if (rightWing)
            {
                wing.Children.Insert(ChipCount(wing), panel);
            }
            else
            {
                wing.Children.Add(panel);
            }
        }
    }

    /// How many chip panels a wing holds. In a right wing they are its first children, so this is where the next one goes.
    private static int ChipCount(StackPanel wing) => wing.Children.OfType<FrameworkElement>().Count(e => e.Tag is ChipTag);

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
        if (chip.Kind == ChipKind.Agent && chip.Agent is { } agent)
        {
            // the same colour as the agent's card (a build chip, as its build card: the ci colour, else the item's)
            return chip.Module == "ci"
                ? CiCards.TintOf(agent.Item, moduleColors, accent)
                : ItemTint.Of(agent.Item, moduleColors, accent);
        }
        if (moduleColors.TryGetValue(chip.Module, out var hex) && HexColor.TryNormalize(hex, out var normalized))
        {
            return HexColor.ToColor(normalized);
        }
        if (chip.Kind == ChipKind.Alerts)
        {
            return AlertRed;
        }
        return accent ?? DefaultTint(chip.Kind);
    }

    /// The colour of a call's time: red while the mic is muted, else the calls module's colour, else green (the Mac's
    /// `c.muted ? .red : color(for: .calls) ?? .green`).
    public static Color CallTimeTint(bool muted, IReadOnlyDictionary<string, string> moduleColors)
    {
        if (muted)
        {
            return CallRed;
        }
        return moduleColors.TryGetValue("calls", out var hex) && HexColor.TryNormalize(hex, out var normalized)
            ? HexColor.ToColor(normalized)
            : CallGreen;
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
