using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using NotchTasks.ControlPanel;
using NotchTasks.Core;
using NotchTasks.Core.Agents;
using NotchTasks.Core.Calendars;
using NotchTasks.Core.Limits;
using NotchTasks.Core.Motion;
using NotchTasks.Core.Media;
using NotchTasks.Core.Notch;
using NotchTasks.Pill.Sections;
using NotchTasks.Services;

namespace NotchTasks.Pill;

/// The closed pill's chips as they sit in the wings (Mac ChipView). A chip's lead is its glyph; its trail is its text: the
/// ask's words, the timer's time left, the call's time so far (green, red while muted), an agent's working count and a limit's
/// percentage (or its wait). A sensor chip has its glyphs only.
public static class ChipView
{
    private const string ChipTag = "chip";  // the Tag of a chip's panel in a wing: what a rebuild removes
    /// The chip and side a panel was made for (its key): a fill replaces a panel of the same key in place, with no motion.
    private static readonly DependencyProperty KeyProperty = DependencyProperty.RegisterAttached(
        "ChipKey", typeof(string), typeof(ChipView), new PropertyMetadata(null));
    /// What a panel shows (its content signature). A fill whose chip has the same signature keeps the panel as it is: no new panel, no
    /// content move, so a media chip's bars are not restarted by a position that moved.
    private static readonly DependencyProperty SignatureProperty = DependencyProperty.RegisterAttached(
        "ChipSignature", typeof(object), typeof(ChipView), new PropertyMetadata(null));

    /// A media chip's content signature: the playing app (its source and exe, whose icon is the lead when there is no artwork), the
    /// artwork key, whether its artwork and its app icon are cached, the tint and the sides. Its position is not in it, so the chip is
    /// not rebuilt as the position moves.
    private sealed record MediaSignature(string Source, string? AppExe, string ArtKey, bool Art, bool Icon, Color Tint, bool Lead, bool Trail,
        string Side, double LeadSize);

    /// Any other chip's content signature: the chip record (its values, as the publish carries them), its tint, the sides, the language
    /// (its words are drawn again on a switch) and the second: a timer, a call or a limit shows the time, so the panel is rebuilt once a
    /// second at most.
    private sealed record ChipSignature(Chip Chip, Color Tint, string Language, long Second, bool Lead, bool Trail, string Side, double LeadSize);

    /// An ask chip's content signature: the chip (its count and the asking agent's item), its tint, the language, the sides and the
    /// program icon's path. No second: the ask's words do not change with time, so the lead is not rebuilt every second.
    private sealed record AskSignature(Chip Chip, Color Tint, string Language, string? IconPath, bool Lead, bool Trail, string Side, double LeadSize);
    private static readonly Color AskOrange = Color.FromRgb(0xFF, 0x9F, 0x0A);
    private static readonly Color CallGreen = Color.FromRgb(0x30, 0xD1, 0x58);  // the Mac's green: a call's time
    private static readonly Color CallRed = Color.FromRgb(0xFF, 0x45, 0x3A);    // the Mac's red: a call's time while muted
    private static readonly Color AlertRed = Color.FromRgb(0xFF, 0x45, 0x3A);   // the Mac's red: a failed CI alert (the alerts chip)

    /// One chip as a panel for a wing: its lead, then its trail. Null when the chip has neither to show. toolIcons are the settings'
    /// badges by tool, which a build chip's lead uses.
    public static FrameworkElement? Panel(Chip chip, Color? accent, IReadOnlyDictionary<string, string> moduleColors,
        DateTimeOffset now, bool lead, bool trail, IProcessReader? processes = null, IReadOnlyDictionary<string, JsonObject>? toolIcons = null,
        Func<string, ImageSource?>? art = null, double leadSize = 18, bool animate = false)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0, 3, 0) };
        if (lead && Lead(chip, leadSize, accent, moduleColors, processes, toolIcons, art, animate) is { } leadElement)
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
        IProcessReader? processes = null, IReadOnlyDictionary<string, JsonObject>? toolIcons = null, Func<string, ImageSource?>? art = null,
        bool animate = false)
    {
        if (chip.Kind == ChipKind.Media && chip.Media is { } playing)
        {
            return MediaLead(playing, size, TintOf(chip, accent, moduleColors), art);
        }
        if (chip.Kind == ChipKind.Alerts)
        {
            return Glyph("warning", size, HexColor.Brush(ToHex(TintOf(chip, accent, moduleColors))));
        }
        if (chip.Kind == ChipKind.Calendar && chip.Event is { } soon)
        {
            return CalendarLead(soon, size, TintOf(chip, accent, moduleColors));
        }
        if (chip.Kind == ChipKind.Agent && chip.Module == "ci" && chip.Agent is { } build)
        {
            return BuildLead(build, size, TintOf(chip, accent, moduleColors), processes, toolIcons);
        }
        if (chip.Kind == ChipKind.Agent && chip.Agent is { } agent)
        {
            return ItemIcon.Element(agent, size, TintOf(chip, accent, moduleColors), processes);
        }
        if (chip.Kind == ChipKind.Ask)
        {
            return AskLead(chip.AskItem, size, processes, animate);
        }
        if (chip.Kind == ChipKind.Limit && chip.Limit is { } limit)
        {
            return Glyph(limit.W.Used >= 100 ? "hourglass" : "gauge", size, new SolidColorBrush(LimitColor(limit.W.Used)));
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

    /// The Now Playing chip's lead (Mac: the artwork at the chip's size, corner 22 % of it): the artwork when it is cached, else the
    /// app's icon when it is read, else the music note in the tint.
    private static FrameworkElement MediaLead(NowPlaying playing, double size, Color tint, Func<string, ImageSource?>? art)
    {
        if (playing.ArtworkKey is { } key && art?.Invoke(key) is { } picture)
        {
            return new Image
            {
                Source = picture,
                Width = size,
                Height = size,
                Stretch = Stretch.UniformToFill,
                Clip = new RectangleGeometry(new Rect(0, 0, size, size), size * 0.22, size * 0.22),
                VerticalAlignment = VerticalAlignment.Center,
                SnapsToDevicePixels = true,
                Focusable = false,
            };
        }
        if (playing.AppExe is { } exe && ItemIcon.ExeIcon(exe) is { } icon)
        {
            return new Image
            {
                Source = icon,
                Width = size,
                Height = size,
                VerticalAlignment = VerticalAlignment.Center,
                SnapsToDevicePixels = true,
                Focusable = false,
            };
        }
        return Glyph("media", size, HexColor.Brush(ToHex(tint)));
    }

    /// The ask chip's lead (Mac ask chip): the asking agent's icon with an orange hand badge at its corner, so the chip says who waits.
    /// With no item for the agent, the orange hand alone. The badge is what tells the user it is an ask, not a running agent.
    /// The badge is the Mac's: the hand at 0.3 of the size, 2.5 of padding, a black 1.5 ring, pushed 0.2 right and 0.14 down past the
    /// icon's corner. Its opacity pulses while `animate` (the Mac's symbol pulse; the Mac sets no values, so these are chosen: 1 to
    /// 0.4 and back in 2 s). Reduced motion or animations off leave it still, as the Mac's Reduce Motion does.
    private static FrameworkElement AskLead(WatchItem? item, double size, IProcessReader? processes, bool animate)
    {
        if (item is null)
        {
            return Glyph("hand", size * 0.72, HexColor.Brush(ToHex(AskOrange)));
        }
        var badge = size * 0.3;
        var hand = FluentGlyph.Named("hand", badge);
        hand.Foreground = Brushes.White;
        var disc = new Border
        {
            Width = badge + 5,
            Height = badge + 5,
            CornerRadius = new CornerRadius((badge + 5) / 2),
            Background = HexColor.Brush(ToHex(AskOrange)),
            BorderBrush = Brushes.Black,
            BorderThickness = new Thickness(1.5),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            // a negative right and bottom margin pushes the badge past the icon's corner, as the Mac's offset does
            Margin = new Thickness(0, 0, -size * 0.2, -size * 0.14),
            Child = hand,
        };
        var lead = new Grid { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
        lead.Children.Add(ItemIcon.Element(new ItemState(item, Array.Empty<Job>()), size, AskOrange, processes));
        lead.Children.Add(disc);
        if (animate)
        {
            var pulse = new DoubleAnimation
            {
                From = 1,
                To = 0.4,
                Duration = TimeSpan.FromSeconds(1),
                AutoReverse = true,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            // Loops stops it when the lead is unloaded or hidden, so an ask that is answered costs nothing
            Loops.Run(lead, disc, UIElement.OpacityProperty, pulse);
        }
        return lead;
    }

    /// The Now Playing chip's trail: the three working bars in the tint (Mac: the chip's bars while a session plays).
    private static FrameworkElement MediaTrail(Color tint)
    {
        var bars = Loops.Bars(new SolidColorBrush(tint));
        bars.Margin = new Thickness(6, 0, 6, 0);
        return bars;
    }

    /// A build chip's lead (Mac: the chip's JobIcon). Only a dev item with no icon of its own shows its first job: the job's tool badge
    /// when its tool has one in settings, else the job's symbol. Any other item, or a job with neither, shows the item's icon.
    private static FrameworkElement BuildLead(ItemState build, double size, Color tint, IProcessReader? processes,
        IReadOnlyDictionary<string, JsonObject>? toolIcons)
    {
        var job = build.Jobs.Count > 0 ? build.Jobs[0] : null;
        if (build.Item.Kind == WatchKind.Dev && string.IsNullOrEmpty(build.Item.Icon) && job is not null)
        {
            if (job.Tool is { Length: > 0 } tool && toolIcons is not null && toolIcons.TryGetValue(tool, out var badge))
            {
                return BadgeGlyph.Create(badge, size, tint);
            }
            if (JobLead.Element(job.Symbol, size, HexColor.Brush(ToHex(tint))) is { } symbol)
            {
                return symbol;
            }
        }
        return ItemIcon.Element(build, size, tint, processes);
    }

    /// A glyph in a brush, from the Fluent System Icons font.
    private static FluentGlyph Glyph(string name, double size, Brush brush)
    {
        var glyph = FluentGlyph.Named(name, size);
        glyph.Foreground = brush;
        return glyph;
    }

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
            case ChipKind.Media when chip.Media is not null:
                return MediaTrail(TintOf(chip, accent, moduleColors));
            case ChipKind.Calendar when chip.Event is { } soon:
                return CalendarTrail(soon, size, TintOf(chip, accent, moduleColors), now);
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
    public static int FillWings(StackPanel left, StackPanel right, AppState state, Color? accent, Action? removed = null) =>
        Fill(left, right, ChipBoard.Chips(state.Snapshot), state.Settings.Placements, state.Settings.ModuleColors, accent,
            DateTimeOffset.Now, state.Processes, state.Settings.ToolIcons, state.Settings.Motion, SystemParameters.ClientAreaAnimation,
            state.MediaArt, removed, PillGeometry.IconSizeFor(state.Settings.Size));

    /// True when the chips make the one-activity case: a lone chip is split into its lead (left) and its trail (right). The
    /// closed pill's wings are then 5 DIP apart, not 10 (the Mac's single-chip HStack spacing).
    public static bool IsSingle(AppState state) =>
        ChipBoard.Sides(ChipBoard.Chips(state.Snapshot), state.Settings.Placements).Single is not null;

    /// FillWings with its inputs given: the same placement, order and badge rule. Returns how many chips there are. A chip that
    /// is new enters (PillMotion.Enter); a chip that is gone exits after its fade; a chip updated in place is replaced without
    /// motion. The badges are never touched.
    public static int Fill(StackPanel left, StackPanel right, IReadOnlyList<Chip> chips, IReadOnlyDictionary<string, string> placements,
        IReadOnlyDictionary<string, string> colors, Color? accent, DateTimeOffset now, IProcessReader? processes,
        IReadOnlyDictionary<string, JsonObject>? toolIcons = null, string motion = "smooth", bool animationsOn = true,
        Func<string, ImageSource?>? art = null, Action? removed = null, double leadSize = 18)
    {
        var (leftChips, rightChips, single) = ChipBoard.Sides(chips, placements);
        var leftPanels = new List<ChipSlot>();
        var rightPanels = new List<ChipSlot>();
        // the badge pulses only while animations run: reduced motion (the Motion setting or the system's) leaves it still
        var animate = animationsOn && motion != "reduced";
        if (single is { } lone)
        {
            Collect(left, leftPanels, lone, accent, colors, now, lead: true, trail: false, side: "L", processes, toolIcons, art, leadSize, animate);
            Collect(right, rightPanels, lone, accent, colors, now, lead: false, trail: true, side: "R", processes, toolIcons, art, leadSize, animate);
        }
        foreach (var chip in leftChips)
        {
            Collect(left, leftPanels, chip, accent, colors, now, lead: true, trail: true, side: "L", processes, toolIcons, art, leadSize, animate);
        }
        foreach (var chip in rightChips)
        {
            Collect(right, rightPanels, chip, accent, colors, now, lead: true, trail: true, side: "R", processes, toolIcons, art, leadSize, animate);
        }
        Reconcile(left, leftPanels, rightWing: false, motion, animationsOn, removed);
        Reconcile(right, rightPanels, rightWing: true, motion, animationsOn, removed);
        return chips.Count;
    }

    /// A chip's place in a fill: its key, its panel (a held one when the content is unchanged), and its content signature (null when
    /// the chip is always rebuilt).
    private readonly record struct ChipSlot(string Key, FrameworkElement Panel, object? Signature);

    /// Makes a chip's panel for one wing and keys it by chip and side, so the next fill can tell it is the same chip. The panel the wing
    /// already holds for that chip is kept, with no new panel, when its signature is unchanged.
    private static void Collect(StackPanel wing, List<ChipSlot> panels, Chip chip, Color? accent,
        IReadOnlyDictionary<string, string> colors, DateTimeOffset now, bool lead, bool trail, string side,
        IProcessReader? processes, IReadOnlyDictionary<string, JsonObject>? toolIcons, Func<string, ImageSource?>? art, double leadSize, bool animate)
    {
        var key = $"{chip.Kind}|{chip.Module}|{chip.Id}|{side}";
        var signature = SignatureOf(chip, accent, colors, now, lead, trail, side, art, leadSize);
        if (signature is not null && HeldPanel(wing, key) is { } held && Equals(held.GetValue(SignatureProperty), signature))
        {
            panels.Add(new ChipSlot(key, held, signature));
            return;
        }
        if (Panel(chip, accent, colors, now, lead, trail, processes, toolIcons, art, leadSize, animate) is { } panel)
        {
            panel.Tag = ChipTag;
            panels.Add(new ChipSlot(key, panel, signature));
        }
    }

    /// The content signature of a chip's panel, or null when the panel is always rebuilt: an agent's and a build's lead is its item icon,
    /// which reads the live processes, so it is never kept.
    private static object? SignatureOf(Chip chip, Color? accent, IReadOnlyDictionary<string, string> colors, DateTimeOffset now,
        bool lead, bool trail, string side, Func<string, ImageSource?>? art, double leadSize)
    {
        var tint = TintOf(chip, accent, colors);
        if (chip.Kind == ChipKind.Media && chip.Media is { } playing)
        {
            var artKey = playing.ArtworkKey ?? string.Empty;
            var artCached = playing.ArtworkKey is { } key && art?.Invoke(key) is not null;
            var iconCached = playing.AppExe is { } exe && ItemIcon.HasExeIcon(exe);
            return new MediaSignature(playing.Source, playing.AppExe, artKey, artCached, iconCached, tint, lead, trail, side, leadSize);
        }
        if (chip.Kind == ChipKind.Agent)
        {
            return null;
        }
        if (chip.Kind == ChipKind.Ask)
        {
            // no clock second: an ask's words do not change with time, so its lead (and the badge's pulse) is kept from publish to publish
            var iconPath = chip.AskItem is { } askItem ? ItemIcon.PathOf(new ItemState(askItem, Array.Empty<Job>()), null) : null;
            return new AskSignature(chip, tint, L10n.Current, iconPath, lead, trail, side, leadSize);
        }
        return new ChipSignature(chip, tint, L10n.Current, now.ToUnixTimeSeconds(), lead, trail, side, leadSize);
    }

    /// The panel a wing holds for a chip key, when it is not leaving; null when there is none.
    private static FrameworkElement? HeldPanel(StackPanel wing, string key) =>
        wing.Children.OfType<FrameworkElement>().FirstOrDefault(e => e.Tag is ChipTag && KeyOf(e) == key && !PillMotion.IsExiting(e));

    /// Puts a wing's chip panels in place. A chip held before and still held keeps its element, so an enter in progress carries on
    /// (its new content is moved into it, with no motion). A chip it no longer holds fades out where it is, and is removed when the
    /// fade ends (then `removed` runs, so the closed pill can follow the wing's new width). A new chip oozes in. A left wing's chips go after its badges; a right wing's chips go before its badges,
    /// after the chips already there, in the order given.
    private static void Reconcile(StackPanel wing, List<ChipSlot> desired, bool rightWing, string motion, bool animationsOn, Action? removed)
    {
        var wanted = desired.Select(d => d.Key).ToHashSet(StringComparer.Ordinal);
        var previous = wing.Children.OfType<FrameworkElement>().Where(e => e.Tag is ChipTag).ToList();
        var held = new Dictionary<string, FrameworkElement>(StringComparer.Ordinal);
        foreach (var element in previous)
        {
            var key = KeyOf(element);
            var wantedNow = key is not null && wanted.Contains(key);
            if (PillMotion.IsExiting(element))
            {
                if (wantedNow)
                {
                    wing.Children.Remove(element);  // the chip is back: the new panel enters in its place
                }
            }
            else if (!wantedNow)
            {
                PillMotion.Exit(wing, element, motion, animationsOn, removed);
            }
            else if (held.ContainsKey(key!))
            {
                wing.Children.Remove(element);  // a duplicate of a chip already held
            }
            else
            {
                held[key!] = element;
            }
        }

        var placed = new List<(FrameworkElement Element, bool IsNew)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (key, panel, signature) in desired)
        {
            if (!seen.Add(key))
            {
                continue;
            }
            if (held.TryGetValue(key, out var old) && ReferenceEquals(old, panel))
            {
                placed.Add((old, false));  // the same content: kept as it is, with no rebuild
                continue;
            }
            if (held.TryGetValue(key, out var old2) && old2 is System.Windows.Controls.Panel oldPanel && panel is System.Windows.Controls.Panel newPanel)
            {
                MoveContent(newPanel, oldPanel);
                oldPanel.SetValue(SignatureProperty, signature);
                UsageCounters.ChipRebuild();
                placed.Add((old2, false));
                continue;
            }
            if (held.TryGetValue(key, out var stale))
            {
                wing.Children.Remove(stale);
            }
            panel.SetValue(KeyProperty, key);
            panel.SetValue(SignatureProperty, signature);
            UsageCounters.ChipRebuild();
            placed.Add((panel, true));
        }

        // A kept element stays where it is while the kept elements keep their order: only the new panels are inserted, each after the
        // one before it. Removing and re-adding a kept element would fire its Unloaded and Loaded, and a loop in it (the media bars)
        // would restart at its first frame on every publish.
        var order = placed.Select(p => p.Element).ToList();
        var inWing = order.Where(e => wing.Children.Contains(e)).ToList();
        var currentOrder = wing.Children.OfType<FrameworkElement>().Where(e => inWing.Contains(e)).ToList();
        if (!currentOrder.SequenceEqual(inWing))
        {
            // the kept elements are out of order: they are taken out, and put back in order below
            foreach (var element in inWing)
            {
                wing.Children.Remove(element);
            }
        }
        for (var i = 0; i < placed.Count; i++)
        {
            var (element, isNew) = placed[i];
            if (!wing.Children.Contains(element))
            {
                // before the first kept element after it that is already in the wing (so a new chip ahead of a kept one lands
                // ahead of it, and the kept one does not move); else after the element before it; else at the wing's end
                var index = -1;
                for (var j = i + 1; j < order.Count && index < 0; j++)
                {
                    if (wing.Children.Contains(order[j]))
                    {
                        index = wing.Children.IndexOf(order[j]);
                    }
                }
                if (index < 0)
                {
                    index = i > 0 && wing.Children.Contains(order[i - 1])
                        ? wing.Children.IndexOf(order[i - 1]) + 1
                        : rightWing ? ChipCount(wing) : wing.Children.Count;
                }
                wing.Children.Insert(index, element);
            }
            if (isNew)
            {
                PillMotion.Ooze(element, leftWing: !rightWing, motion, animationsOn);
            }
        }
    }

    /// Moves a new panel's content into an old panel, which stays on screen (with its motion) and shows the new content.
    private static void MoveContent(System.Windows.Controls.Panel from, System.Windows.Controls.Panel to)
    {
        var content = from.Children.OfType<UIElement>().ToList();
        foreach (var child in content)
        {
            from.Children.Remove(child);
        }
        to.Children.Clear();
        foreach (var child in content)
        {
            to.Children.Add(child);
        }
    }

    private static string? KeyOf(FrameworkElement element) => element.GetValue(KeyProperty) as string;

    /// How many chip panels a wing holds. In a right wing they are its first children, so this is where the next one goes.
    private static int ChipCount(StackPanel wing) => wing.Children.OfType<FrameworkElement>().Count(e => e.Tag is ChipTag);

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
        // an event's chip, with no calendar module colour, takes the colour of its calendar (Mac: color(for: .calendar) ?? e.color)
        if (chip.Kind == ChipKind.Calendar && chip.Event is { } soon && HexColor.TryNormalize(soon.Color, out var calendarHex))
        {
            return HexColor.ToColor(calendarHex);
        }
        return accent ?? DefaultTint(chip.Kind);
    }

    /// The calendar chip's glyph (Mac ChipView .event): a ring filling up to the end while the event runs, else the calendar icon.
    private static FrameworkElement CalendarLead(CalendarEvent soon, double size, Color tint)
    {
        if (!soon.Running(DateTimeOffset.Now))
        {
            return Glyph("calendar", size * 0.8, new SolidColorBrush(tint));
        }
        const double stroke = 2.5;
        var diameter = size * 0.75;
        var radius = (diameter - stroke) / 2;
        var centre = size / 2;
        var fraction = Math.Max(soon.Elapsed(DateTimeOffset.Now), 0.03);  // the Mac's max(elapsed, 0.03): a sliver shows at once
        var sweep = fraction * 2 * Math.PI;
        var start = new Point(centre, centre - radius);
        var end = new Point(centre + radius * Math.Sin(sweep), centre - radius * Math.Cos(sweep));
        var arc = new Path
        {
            Stroke = new SolidColorBrush(tint),
            StrokeThickness = stroke,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Data = new PathGeometry
            {
                Figures = new PathFigureCollection
                {
                    new PathFigure
                    {
                        StartPoint = start,
                        Segments = new PathSegmentCollection
                        {
                            new ArcSegment(end, new Size(radius, radius), 0, fraction > 0.5, SweepDirection.Clockwise, true),
                        },
                    },
                },
            },
        };
        var track = new Ellipse
        {
            Width = diameter,
            Height = diameter,
            Stroke = new SolidColorBrush(Color.FromArgb(0x40, tint.R, tint.G, tint.B)),
            StrokeThickness = stroke,
        };
        var ring = new Grid { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
        ring.Children.Add(track);
        ring.Children.Add(arc);
        return ring;
    }

    /// The calendar chip's trail (Mac ChipView .event): the minutes, "N′", in white until the event starts, then in its tint while it
    /// runs (the minutes it has left). Mac: Int(seconds / 60) + 1, so a minute left reads 1′.
    private static TextBlock CalendarTrail(CalendarEvent soon, double size, Color tint, DateTimeOffset now)
    {
        var until = soon.Start - now;
        return new TextBlock
        {
            Text = soon.MinutesShown(now) + "′",
            Foreground = until > TimeSpan.Zero ? Brushes.White : new SolidColorBrush(tint),
            FontSize = size,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(6, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
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
