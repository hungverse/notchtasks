using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace NotchTasks.Pill;

/// The pill's decorative loops (the working bars, the ripple). A loop runs at 30 fps, and it runs only while its owner is on
/// screen: WPF keeps an animation ticking after its element leaves the tree, so Run stops it on Unloaded and when the owner
/// is hidden, and starts it again on Loaded and when it shows.
public static class Loops
{
    /// The frame rate a decorative loop is drawn at.
    private const double FrameRate = 30;

    /// The working bars' period: 2π / 3.4 s.
    private static readonly TimeSpan BarsPeriod = TimeSpan.FromSeconds(2 * Math.PI / 3.4);

    /// The ripple's length.
    private static readonly TimeSpan RipplePeriod = TimeSpan.FromSeconds(1.2);

    /// The object an animation is aimed at, and its property: set by Aim, read by Run.
    private static readonly DependencyProperty AimTargetProperty = DependencyProperty.RegisterAttached(
        "AimTarget", typeof(IAnimatable), typeof(Loops), new PropertyMetadata(null));
    private static readonly DependencyProperty AimPropertyProperty = DependencyProperty.RegisterAttached(
        "AimProperty", typeof(DependencyProperty), typeof(Loops), new PropertyMetadata(null));

    /// Aims an animation at a property of an object (a transform or an element), as a storyboard would be.
    public static void Aim(AnimationTimeline animation, IAnimatable target, DependencyProperty property)
    {
        animation.SetValue(AimTargetProperty, target);
        animation.SetValue(AimPropertyProperty, property);
    }

    /// Makes a loop of an animation aimed with Aim: repeated forever, at 30 fps, running while the owner is on screen.
    public static void Run(FrameworkElement owner, Timeline t)
    {
        var animation = t as AnimationTimeline ?? throw new ArgumentException("A loop is an animation aimed with Aim.", nameof(t));
        var target = animation.GetValue(AimTargetProperty) as IAnimatable
            ?? throw new ArgumentException("The loop has no target: call Loops.Aim first.", nameof(t));
        var property = animation.GetValue(AimPropertyProperty) as DependencyProperty
            ?? throw new ArgumentException("The loop has no property: call Loops.Aim first.", nameof(t));
        Timeline.SetDesiredFrameRate(animation, (int)FrameRate);
        animation.RepeatBehavior = RepeatBehavior.Forever;

        var running = false;
        void Start()
        {
            if (!running && owner.IsLoaded && owner.IsVisible)
            {
                target.BeginAnimation(property, animation);
                running = true;
            }
        }
        void Stop()
        {
            if (running)
            {
                target.BeginAnimation(property, null);
                running = false;
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
            Run(panel, BarAnimation(transform, i));
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
        var scaleX = new DoubleAnimation(1, 2.4, RipplePeriod) { EasingFunction = ease };
        var scaleY = new DoubleAnimation(1, 2.4, RipplePeriod) { EasingFunction = ease };
        var fade = new DoubleAnimation(0.8, 0, RipplePeriod) { EasingFunction = ease };
        Aim(scaleX, transform, ScaleTransform.ScaleXProperty);
        Aim(scaleY, transform, ScaleTransform.ScaleYProperty);
        Aim(fade, circle, UIElement.OpacityProperty);
        Run(circle, scaleX);
        Run(circle, scaleY);
        Run(circle, fade);
        return circle;
    }

    /// The bar's Y scale at time t (seconds) for bar i: (3 + 8·|sin(t·3.4 + i·1.1)|) / 11.
    private static double Bar(double t, int i) => (3 + 8 * Math.Abs(Math.Sin(t * 3.4 + i * 1.1))) / 11;

    /// One bar's Y scale over one period, as 24 linear keyframes. The last keyframe is at the period's end, where the wave
    /// is back at its start, so the loop has no hold.
    private static DoubleAnimationUsingKeyFrames BarAnimation(ScaleTransform transform, int i)
    {
        const int steps = 24;
        var animation = new DoubleAnimationUsingKeyFrames { Duration = new Duration(BarsPeriod) };
        for (var k = 1; k <= steps; k++)
        {
            var time = BarsPeriod.TotalSeconds * k / steps;
            animation.KeyFrames.Add(new LinearDoubleKeyFrame(Bar(time, i), KeyTime.FromTimeSpan(TimeSpan.FromSeconds(time))));
        }
        Aim(animation, transform, ScaleTransform.ScaleYProperty);
        return animation;
    }
}
