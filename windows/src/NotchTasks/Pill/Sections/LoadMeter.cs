using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using NotchTasks.Core;

namespace NotchTasks.Pill.Sections;

/// Which load a meter shows: the whole-computer CPU, or the memory in use. It picks the tooltip's words.
public enum LoadMeterKind
{
    Cpu,
    Memory,
}

/// One meter of the open pill's header (Mac: LoadMeter in NotchView.swift): a 20 DIP ring with the percent inside, and the label
/// 5 DIP to its right. The arc runs clockwise from the top to the value (at least 2 %). The tint is red from 90 %, orange from
/// 70 % and green below. A change eases out over 0.4 s, unless animations are off or the motion is reduced. A click raises
/// Clicked (the pill opens Task Manager with it).
public sealed class LoadMeter : StackPanel
{
    private const double Size = 20;
    private const double Stroke = 2.5;
    private const double Radius = (Size - Stroke) / 2;
    private const double Center = Size / 2;
    private const double MaxFraction = 0.9999;  // a full ring is an arc that ends where it starts, so it stops just short
    private const double MinFraction = 0.02;
    private static readonly TimeSpan Ease = TimeSpan.FromSeconds(0.4);

    private static readonly Brush Red = Frozen(0xFF, 0x45, 0x3A);
    private static readonly Brush Orange = Frozen(0xFF, 0x9F, 0x0A);
    private static readonly Brush Green = Frozen(0x30, 0xD1, 0x58);

    /// The arc's fraction, animated by Show.
    private static readonly DependencyProperty FractionProperty = DependencyProperty.Register(
        "Fraction", typeof(double), typeof(LoadMeter), new PropertyMetadata(0.0, (d, _) => ((LoadMeter)d).DrawArc()));

    private readonly Path _arc = new()
    {
        StrokeThickness = Stroke,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
    };
    private readonly TextBlock _percent = new()
    {
        FontSize = 7.5,
        FontWeight = FontWeights.Bold,
        Foreground = Brushes.White,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly LoadMeterKind _kind;
    private bool _shown;

    /// A meter with its label ("CPU" or "RAM"), its kind and its first value, 0..1.
    public LoadMeter(string label, LoadMeterKind kind, double value)
    {
        _kind = kind;
        Orientation = Orientation.Horizontal;
        VerticalAlignment = VerticalAlignment.Center;
        Cursor = Cursors.Hand;
        Focusable = false;  // a click target only: the pill never takes the keyboard focus

        var ring = new Grid { Width = Size, Height = Size };
        ring.Children.Add(new Ellipse
        {
            Width = 2 * Radius,
            Height = 2 * Radius,
            Stroke = new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)),  // white at 0.14
            StrokeThickness = Stroke,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });
        ring.Children.Add(_arc);
        ring.Children.Add(_percent);
        Children.Add(ring);
        Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)),  // white at 0.6
            Margin = new Thickness(5, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });

        MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            Clicked?.Invoke();
        };
        Show(value, animate: false);
    }

    /// Raised on a click: the pill opens Task Manager.
    public event Action? Clicked;

    /// Shows a value, 0..1. The first value is shown at once; a later one eases to the new arc when animate is true.
    public void Show(double value, bool animate)
    {
        var v = Math.Clamp(value, 0, 1);
        var percent = NotchTasks.Core.Modules.HealthModule.LoadPercent(v);
        _percent.Text = percent.ToString(CultureInfo.InvariantCulture);
        _arc.Stroke = v >= 0.9 ? Red : v >= 0.7 ? Orange : Green;
        var help = _kind == LoadMeterKind.Cpu ? L10n.L("Whole-computer CPU: ") : L10n.L("Memory in use: ");
        ToolTip = help + L10n.L("{0}% · click to open Task Manager", percent);

        var target = Math.Clamp(v, MinFraction, 1);
        if (animate && _shown)
        {
            BeginAnimation(FractionProperty, new DoubleAnimation(
                (double)GetValue(FractionProperty),
                target,
                Ease)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
        }
        else
        {
            BeginAnimation(FractionProperty, null);
            SetValue(FractionProperty, target);
        }
        _shown = true;
    }

    /// Draws the arc for the current fraction: from the top, clockwise.
    private void DrawArc()
    {
        var fraction = Math.Min((double)GetValue(FractionProperty), MaxFraction);
        var angle = 2 * Math.PI * fraction;
        var start = new Point(Center, Center - Radius);
        var end = new Point(Center + Radius * Math.Sin(angle), Center - Radius * Math.Cos(angle));
        var figure = new PathFigure
        {
            StartPoint = start,
            Segments = { new ArcSegment(end, new Size(Radius, Radius), 0, fraction > 0.5, SweepDirection.Clockwise, true) },
        };
        _arc.Data = new PathGeometry { Figures = { figure } };
    }

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
