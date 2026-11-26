using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NotchTasks.ControlPanel;
using NotchTasks.Core;
using NotchTasks.Core.Notch;

namespace NotchTasks.Pill.Sections;

/// The timer's card in the open pill (Mac TimerCard). It is one element kept for the pill's life, so a click on its buttons is
/// not lost when the pill rebuilds its sections each second: Update refreshes it from the app state.
public partial class TimerCard : UserControl
{
    private static readonly Color OrangeTint = Color.FromRgb(0xFF, 0x9F, 0x0A);  // the Mac's .orange, the timer's default
    private const double RingRadius = 15.5;  // the ring's centre line, in the 34 px box
    private const double RingCentre = 17;

    private AppState? _state;

    public TimerCard()
    {
        InitializeComponent();
    }

    /// The timer's tint, as the Mac's `color(for: .timer) ?? accentColor ?? .orange`: its module colour, else the chosen accent
    /// (null when the accent is "auto"), else orange. The chip uses the same rule (ChipView.TintOf).
    public static Color TintOf(AppState state, Color? accent)
    {
        if (state.Settings.ModuleColors.TryGetValue("timer", out var hex) && HexColor.TryNormalize(hex, out var normalized))
        {
            return HexColor.ToColor(normalized);
        }
        return accent ?? OrangeTint;
    }

    /// Refreshes the card from the running timer and returns true. Returns false, and leaves the card alone, when no timer runs.
    public bool Update(AppState state, Color? accent)
    {
        if (state.Timer.State is not { } timer)
        {
            return false;
        }
        _state = state;
        var tint = new SolidColorBrush(TintOf(state, accent));
        var left = timer.End - DateTimeOffset.Now;
        var fraction = timer.Total.TotalSeconds > 0 ? Math.Clamp(left.TotalSeconds / timer.Total.TotalSeconds, 0, 1) : 0;
        TimeText.Text = TimerState.Mmss(left);
        CaptionText.Text = L10n.L("{0}-minute timer", (int)timer.Total.TotalMinutes);
        (Glyph.FontFamily, Glyph.Text) = ModuleGlyphs.Get("timer");
        Glyph.Foreground = tint;
        Ring.Stroke = tint;
        Ring.Data = RingGeometry(fraction);
        return true;
    }

    /// The arc from the top, clockwise, over the part of the ring that is left. A full ring is a circle; an empty one has no arc.
    private static Geometry? RingGeometry(double fraction)
    {
        if (fraction <= 0.0005)
        {
            return null;
        }
        if (fraction >= 0.9995)
        {
            return new EllipseGeometry(new Point(RingCentre, RingCentre), RingRadius, RingRadius);
        }
        var angle = fraction * 2 * Math.PI - Math.PI / 2;
        var start = new Point(RingCentre, RingCentre - RingRadius);
        var end = new Point(RingCentre + RingRadius * Math.Cos(angle), RingCentre + RingRadius * Math.Sin(angle));
        var figure = new PathFigure { StartPoint = start, IsClosed = false };
        figure.Segments.Add(new ArcSegment(end, new Size(RingRadius, RingRadius), 0, fraction > 0.5,
            SweepDirection.Clockwise, true));
        return new PathGeometry(new[] { figure });
    }

    private void OnPlusOne(object sender, RoutedEventArgs e) => Extend(1);

    private void OnPlusFive(object sender, RoutedEventArgs e) => Extend(5);

    private void OnStop(object sender, RoutedEventArgs e)
    {
        if (_state is not { } state)
        {
            return;
        }
        state.Timer.Stop();
        state.Publish();
    }

    private void Extend(int minutes)
    {
        if (_state is not { } state)
        {
            return;
        }
        state.Timer.Extend(minutes, DateTimeOffset.Now);
        state.Publish();
    }
}
