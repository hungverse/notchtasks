using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using NotchTasks.Core.Motion;
using NotchTasks.Core.Notch;

namespace NotchTasks.Pill.Sections;

/// The battery toast's art (the Mac's ChargeArt, NotchView.swift): a battery outline filled to its level. Plugged in, the
/// fill grows to the level, the bolt scales in and the fill is green. Unplugged, the plug slides away to the left and the
/// fill drains from green to white (red at 20% or less). Under reduced motion, or with animations off in Windows, it
/// shows at once with no slide. A toast's art is kept for the toast's life: the view is made by ToastView.Show.
public sealed class ChargeArtView : FrameworkElement
{
    /// The art's size: a battery and, unplugged, the plug to its left. It fits the compact row's 52 px side column.
    public const double ArtWidth = 46;
    public const double ArtHeight = 20;

    private const double BodyWidth = 30;
    private const double BodyHeight = 14;
    private const double NubWidth = 2;
    private const double LowPercent = 20;
    private const double PlugSlide = 6;

    private static readonly Color GreenColor = Color.FromRgb(0x30, 0xD1, 0x58);
    private static readonly Color WhiteColor = Colors.White;
    private static readonly Color RedColor = Color.FromRgb(0xFF, 0x45, 0x3A);

    /// The animation's progress from 0 (the start) to 1 (the settled look).
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(ChargeArtView),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private readonly ChargeArt _art;

    /// The art for a toast. The spring runs from the motion setting (SpringCurve.For), unless reduced or animations are off.
    public ChargeArtView(ChargeArt art, string motion)
    {
        _art = art;
        Width = ArtWidth;
        Height = ArtHeight;
        var animationsOn = SystemParameters.ClientAreaAnimation;
        if (!animationsOn || motion == "reduced")
        {
            Progress = 1;
            return;
        }
        var curve = SpringCurve.For(motion, opening: true, animationsOn: true);
        var animation = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(curve.Settle))
        {
            EasingFunction = new SpringEase(curve),
        };
        BeginAnimation(ProgressProperty, animation);
    }

    /// The animation's progress, 0 to 1.
    public double Progress
    {
        get => (double)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var p = Math.Clamp(Progress, 0, 1);
        var level = Math.Clamp(_art.Percent / 100.0, 0, 1);
        var plugged = _art.Plugged;

        // The battery sits at the right; the plug, unplugged, at the left.
        var bodyLeft = ArtWidth - NubWidth - BodyWidth;
        var top = (ArtHeight - BodyHeight) / 2;
        var body = new Rect(bodyLeft, top, BodyWidth, BodyHeight);
        var outline = new Pen(new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)), 1.3);
        outline.Freeze();
        dc.DrawRoundedRectangle(null, outline, body, BodyHeight * 0.3, BodyHeight * 0.3);

        var nub = new Rect(bodyLeft + BodyWidth, top + BodyHeight * 0.3, NubWidth, BodyHeight * 0.4);
        dc.DrawRectangle(Brush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)), null, nub);

        // The fill: plugged, it grows with the spring; unplugged, it is at the level at once and drains its colour.
        var fillLevel = plugged ? level * p : level;
        var fillWidth = Math.Max(2, (BodyWidth - 4) * fillLevel);
        var target = plugged ? GreenColor : (_art.Percent <= LowPercent ? RedColor : WhiteColor);
        var color = plugged ? GreenColor : Lerp(GreenColor, target, p);
        dc.DrawRoundedRectangle(
            Brush(color),
            null,
            new Rect(body.Left + 2, body.Top + 2, fillWidth, body.Height - 4),
            BodyHeight * 0.2,
            BodyHeight * 0.2);

        if (plugged)
        {
            DrawBolt(dc, body, 0.2 + 0.8 * p, p);
        }
        else
        {
            DrawPlug(dc, p);
        }
    }

    /// The bolt in the battery, scaled in from 0.2 to full size and faded in.
    private static void DrawBolt(DrawingContext dc, Rect body, double scale, double opacity)
    {
        var h = body.Height * 0.8 * scale;
        var w = h * 0.6;
        var x0 = body.Left + (body.Width - w) / 2;
        var y0 = body.Top + (body.Height - h) / 2;
        var points = new[] { (0.62, 0.0), (0.12, 0.56), (0.46, 0.56), (0.36, 1.0), (0.88, 0.40), (0.52, 0.40), (0.70, 0.0) };
        var figure = new PathFigure { StartPoint = Scaled(points[0], x0, y0, w, h), IsClosed = true };
        for (var i = 1; i < points.Length; i++)
        {
            figure.Segments.Add(new LineSegment(Scaled(points[i], x0, y0, w, h), true));
        }
        var geometry = new PathGeometry(new[] { figure });
        geometry.Freeze();
        dc.PushOpacity(opacity);
        dc.DrawGeometry(Brush(Colors.White), null, geometry);
        dc.Pop();
    }

    /// The plug, unplugged: it slides left and fades as the progress runs.
    private static void DrawPlug(DrawingContext dc, double p)
    {
        var shift = -PlugSlide * p;
        var opacity = 1 - p;
        if (opacity <= 0)
        {
            return;
        }
        dc.PushOpacity(opacity);
        dc.PushTransform(new TranslateTransform(shift, 0));
        var grey = Brush(Color.FromRgb(0x8E, 0x8E, 0x93));
        var top = ArtHeight / 2 - 3;
        dc.DrawRoundedRectangle(grey, null, new Rect(0, top, 8, 8), 2, 2);
        dc.DrawRectangle(grey, null, new Rect(2, top - 3, 1.5, 3.5));
        dc.DrawRectangle(grey, null, new Rect(4.5, top - 3, 1.5, 3.5));
        dc.DrawRectangle(grey, null, new Rect(8, top + 3, 4, 1.5));
        dc.Pop();
        dc.Pop();
    }

    private static Point Scaled((double X, double Y) p, double x0, double y0, double w, double h) =>
        new(x0 + p.X * w, y0 + p.Y * h);

    private static Color Lerp(Color from, Color to, double t) => Color.FromRgb(
        (byte)Math.Round(from.R + (to.R - from.R) * t),
        (byte)Math.Round(from.G + (to.G - from.G) * t),
        (byte)Math.Round(from.B + (to.B - from.B) * t));

    private static SolidColorBrush Brush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
