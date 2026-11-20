using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using NotchTasks.Services;

namespace NotchTasks.Pill;

/// The pill's decorative loops (the working bars, the status ripple). A loop runs at 30 fps, and only while its owner is on
/// screen: WPF keeps an animation ticking after its element leaves the tree, so Run stops it on Unloaded and when the owner is
/// hidden, and starts it again on Loaded and when it shows. A loop must never break the pill: a failed start is logged once.
public static class Loops
{
    /// The frame rate a decorative loop is drawn at.
    private const double FrameRate = 30;

    /// The frame rate of the chip's working bars: small and always on while media plays, so drawn at 20 fps (P3d ruling).
    private const double BarsFrameRate = 20;

    /// The working bars' period: 2π / 3.4 s.
    private static readonly TimeSpan BarsPeriod = TimeSpan.FromSeconds(2 * Math.PI / 3.4);

    /// The ripple's length.
    private static readonly TimeSpan RipplePeriod = TimeSpan.FromSeconds(1.2);

    /// Whether a failed loop start has been logged. Only the first is written, so a repeating failure does not flood the log.
    private static bool _startFailureLogged;

    /// Repeats an animation of a property of a target forever, at 30 fps, while the owner is on screen. The animation is not
    /// changed beyond its repeat and frame rate: nothing is stored on it, so it can be frozen when it starts.
    public static void Run(FrameworkElement owner, IAnimatable target, DependencyProperty property, AnimationTimeline animation,
        double frameRate = FrameRate)
    {
        Timeline.SetDesiredFrameRate(animation, (int)frameRate);
        animation.RepeatBehavior = RepeatBehavior.Forever;

        var running = false;
        void Start()
        {
            if (running || !owner.IsLoaded || !owner.IsVisible)
            {
                return;
            }
            try
            {
                target.BeginAnimation(property, animation);
                running = true;
            }
            catch (Exception error)
            {
                LogStartFailure(error);  // the loop stays off; the pill goes on
            }
        }
        void Stop()
        {
            if (!running)
            {
                return;
            }
            running = false;
            try
            {
                target.BeginAnimation(property, null);
            }
            catch (Exception error)
            {
                LogStartFailure(error);
            }
        }

        owner.Loaded += (_, _) => Start();
        owner.Unloaded += (_, _) => Stop();
        owner.IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue)
            {
                Start();
            }
            else
            {
                Stop();
            }
        };
        Start();
    }

    /// Three bars in a row (the Mac's working indicator): each 2.5 × 11, 1.5 apart, scaled on Y by its own wave.
    public static FrameworkElement Bars(Brush brush)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Focusable = false };
        for (var i = 0; i < 3; i++)
        {
            var transform = new ScaleTransform(1, Bar(0, i));
            var bar = new Rectangle
            {
                Width = 2.5,
                Height = 11,
                RadiusX = 1.25,
                RadiusY = 1.25,
                Fill = brush,
                Margin = new Thickness(i == 0 ? 0 : 1.5, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = transform,
                Focusable = false,
            };
            Run(panel, transform, ScaleTransform.ScaleYProperty, BarAnimation(i), BarsFrameRate);
            panel.Children.Add(bar);
        }
        return panel;
    }

    /// A stroke circle that grows from its size to 2.4 times it while it fades out, over 1.2 s, repeated (the Mac's status ripple).
    public static FrameworkElement Ripple(Brush brush, double size)
    {
        var transform = new ScaleTransform(1, 1);
        var circle = new Ellipse
        {
            Width = size,
            Height = size,
            Stroke = brush,
            StrokeThickness = 1.5,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = transform,
            IsHitTestVisible = false,
            Focusable = false,
        };
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        Run(circle, transform, ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 2.4, RipplePeriod) { EasingFunction = ease });
        Run(circle, transform, ScaleTransform.ScaleYProperty, new DoubleAnimation(1, 2.4, RipplePeriod) { EasingFunction = ease });
        Run(circle, circle, UIElement.OpacityProperty, new DoubleAnimation(0.8, 0, RipplePeriod) { EasingFunction = ease });
        return circle;
    }

    /// The bar's Y scale at time t (seconds) for bar i: (3 + 8·|sin(t·3.4 + i·1.1)|) / 11.
    private static double Bar(double t, int i) => (3 + 8 * Math.Abs(Math.Sin(t * 3.4 + i * 1.1))) / 11;

    /// One bar's Y scale over one period, as 24 linear keyframes. The last keyframe is at the period's end, where the wave
    /// is back at its start, so the loop has no hold.
    private static DoubleAnimationUsingKeyFrames BarAnimation(int i)
    {
        const int steps = 24;
        var animation = new DoubleAnimationUsingKeyFrames { Duration = new Duration(BarsPeriod) };
        for (var k = 1; k <= steps; k++)
        {
            var time = BarsPeriod.TotalSeconds * k / steps;
            animation.KeyFrames.Add(new LinearDoubleKeyFrame(Bar(time, i), KeyTime.FromTimeSpan(TimeSpan.FromSeconds(time))));
        }
        return animation;
    }

    /// Logs a failed loop start or stop, once for the process.
    private static void LogStartFailure(Exception error)
    {
        if (_startFailureLogged)
        {
            return;
        }
        _startFailureLogged = true;
        ErrorLog.Write(error);
    }
}
