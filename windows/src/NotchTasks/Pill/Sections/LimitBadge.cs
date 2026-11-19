using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using NotchTasks.Core;
using NotchTasks.Core.Limits;

namespace NotchTasks.Pill.Sections;

/// One compact limit on an agent card (Mac LimitBadge): a 12 px ring of the window's used share at a 2 px line, then the label
/// (9 semibold, white 55 %) and the detail (9 semibold, in the ring's colour). The tooltip is "label · used% · reset".
public static class LimitBadge
{
    /// The tag of a badge's panel: how a card counts its badges.
    public const string Tag = "limit-badge";

    private static readonly Color Green = Color.FromRgb(0x30, 0xD1, 0x58);   // the Mac's green
    private static readonly Color Orange = Color.FromRgb(0xFF, 0x9F, 0x0A);  // the Mac's orange
    private static readonly Color Red = Color.FromRgb(0xFF, 0x45, 0x3A);     // the Mac's red
    private static readonly Color LabelColor = Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF);  // white at 55 %
    private static readonly Color TrackColor = Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF);  // white at 15 %
    private const double RingSize = 12;
    private const double LineWidth = 2;
    private const double Centre = RingSize / 2;
    private const double Radius = (RingSize - LineWidth) / 2;

    /// The ring's colour for a used share (Mac LimitRing.color): red from 90, orange from 70, else green.
    public static Color ColorOf(double used) => used >= 90 ? Red : used >= 70 ? Orange : Green;

    /// The badge as a panel for a card. Its tag is LimitBadge.Tag.
    public static FrameworkElement Build(Badge badge, DateTimeOffset now)
    {
        var colour = ColorOf(badge.Window.Used);
        var brush = new SolidColorBrush(colour);
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Tag = Tag,
            ToolTip = Tooltip(badge.Window, now),
        };
        panel.Children.Add(Ring(badge.Window.Used, brush));
        if (badge.Label is { } label)
        {
            panel.Children.Add(Text(label, new SolidColorBrush(LabelColor), 3));
        }
        if (badge.Detail is { } detail)
        {
            panel.Children.Add(Text(detail, brush, 3));
        }
        return panel;
    }

    /// "label · used% · reset" for a window, with the culture's reset time.
    public static string Tooltip(LimitWindow window, DateTimeOffset now) =>
        $"{window.Label} · {(int)Math.Round(window.Used, MidpointRounding.AwayFromZero)}% · "
        + Limit.ResetText(window, now, CultureInfo.CurrentCulture, TimeZoneInfo.Local);

    private static TextBlock Text(string text, Brush brush, double left) => new()
    {
        Text = text,
        FontSize = 9,
        FontWeight = FontWeights.SemiBold,
        Foreground = brush,
        Margin = new Thickness(left, 0, 0, 0),
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// The track ring with the used arc drawn over it, from the top and clockwise.
    private static Grid Ring(double used, Brush brush)
    {
        var fraction = Math.Clamp(used, 0, 100) / 100;
        var ring = new Grid { Width = RingSize, Height = RingSize, VerticalAlignment = VerticalAlignment.Center };
        ring.Children.Add(new Ellipse
        {
            Width = 2 * Radius,
            Height = 2 * Radius,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Stroke = new SolidColorBrush(TrackColor),
            StrokeThickness = LineWidth,
        });
        ring.Children.Add(new Path
        {
            Data = Arc(fraction),
            Stroke = brush,
            StrokeThickness = LineWidth,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        });
        return ring;
    }

    /// The arc of a fraction of the ring, from the top; a full ring is a circle and an empty one has no arc.
    private static Geometry? Arc(double fraction)
    {
        if (fraction <= 0.0005)
        {
            return null;
        }
        if (fraction >= 0.9995)
        {
            return new EllipseGeometry(new Point(Centre, Centre), Radius, Radius);
        }
        var angle = fraction * 2 * Math.PI - Math.PI / 2;
        var start = new Point(Centre, Centre - Radius);
        var end = new Point(Centre + Radius * Math.Cos(angle), Centre + Radius * Math.Sin(angle));
        var figure = new PathFigure { StartPoint = start, IsClosed = false };
        figure.Segments.Add(new ArcSegment(end, new Size(Radius, Radius), 0, fraction > 0.5, SweepDirection.Clockwise, true));
        return new PathGeometry(new[] { figure });
    }
}
