using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using NotchTasks.Core.Motion;

namespace NotchTasks.Pill;

/// Enter and exit motion for the pill's chips, sections and toasts (Mac "liquid" transition). Only opacity and a scale
/// transform move, both composited: no blur per element, no layout per frame. Under "reduced" or with animations off in
/// Windows, only opacity moves, over 0.15 s.
public static class PillMotion
{
    /// The scale an element enters from and exits to (Mac liquid: 0.92 → 1).
    private const double EnterScale = 0.92;

    /// The longest fade, however long the spring settles.
    private static readonly TimeSpan MaxFade = TimeSpan.FromSeconds(0.35);

    /// The fade of "reduced" and of animations switched off.
    private static readonly TimeSpan ReducedFade = TimeSpan.FromSeconds(0.15);

    /// Set on an element that is exiting: the token of its exit. Enter clears it, which keeps a reused element from being removed.
    private static readonly DependencyProperty ExitingProperty = DependencyProperty.RegisterAttached(
        "Exiting", typeof(object), typeof(PillMotion), new PropertyMetadata(null));

    /// How long a fade lasts: min(the spring's settle time, 0.35 s), or 0.15 s when reduced or animations are off.
    public static TimeSpan Fade(string motion, bool animationsOn)
    {
        if (!animationsOn || motion == "reduced")
        {
            return ReducedFade;
        }
        var settle = TimeSpan.FromSeconds(SpringCurve.For(motion, opening: true, animationsOn: true).Settle);
        return settle < MaxFade ? settle : MaxFade;
    }

    /// Fades an element in from opacity 0, and with scale set, from 0.92 to 1 about its centre (not when reduced).
    /// Also reuses an element that is exiting: its exit is cancelled and it is not removed.
    /// The local values are the resting ones (opacity 1, scale 1). Each animation uses FillBehavior.Stop, so it ends and gives
    /// the local value back: a later local set takes effect. An element with a transform of its own (not the identity, nor a
    /// scale at 1) enters by opacity only, and its transform is left as it is.
    public static void Enter(UIElement e, bool scale, string motion, bool animationsOn)
    {
        e.SetValue(ExitingProperty, null);
        e.BeginAnimation(UIElement.OpacityProperty, null);
        var duration = Fade(motion, animationsOn);
        var ease = Ease(duration);
        e.Opacity = 1;
        e.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });

        var reduced = !animationsOn || motion == "reduced";
        if (scale && !reduced && ScaleFor(e) is { } transform)
        {
            transform.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(EnterScale, 1, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
            transform.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(EnterScale, 1, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
        }
    }

    /// The scale transform an entering element uses: a new one when it has the identity transform, or its scale transform while that
    /// is at 1 (from an earlier enter). Null for any other transform: the element's own transform is never overwritten.
    private static ScaleTransform? ScaleFor(UIElement e)
    {
        if (ReferenceEquals(e.RenderTransform, Transform.Identity))
        {
            var created = new ScaleTransform(1, 1);
            e.RenderTransformOrigin = new Point(0.5, 0.5);
            e.RenderTransform = created;
            return created;
        }
        return e.RenderTransform is ScaleTransform s
            && (double)s.GetAnimationBaseValue(ScaleTransform.ScaleXProperty) == 1
            && (double)s.GetAnimationBaseValue(ScaleTransform.ScaleYProperty) == 1
            ? s
            : null;
    }

    /// Fades an element out, then removes it from its parent. It is not hit-testable while it fades. If it is entered again
    /// before the fade ends (Enter), it stays where it is.
    public static void Exit(Panel parent, UIElement e, string motion, bool animationsOn)
    {
        e.IsHitTestVisible = false;
        var token = new object();
        e.SetValue(ExitingProperty, token);
        var duration = Fade(motion, animationsOn);
        var animation = new DoubleAnimation(0, duration)
        {
            EasingFunction = Ease(duration),
            FillBehavior = FillBehavior.HoldEnd,  // the element is removed at the end, so its opacity holds until then
        };
        animation.Completed += (_, _) =>
        {
            if (!ReferenceEquals(e.GetValue(ExitingProperty), token))
            {
                return;  // entered again before the fade ended
            }
            e.SetValue(ExitingProperty, null);
            e.BeginAnimation(UIElement.OpacityProperty, null);
            if (parent.Children.Contains(e))
            {
                parent.Children.Remove(e);
            }
        };
        e.BeginAnimation(UIElement.OpacityProperty, animation);
    }

    /// True while the element is exiting: it fades out and will be removed when the fade ends.
    public static bool IsExiting(UIElement e) => e.GetValue(ExitingProperty) is not null;

    /// The ease of a fade: an ease-in-out over the fade's own duration.
    public static SpringEase Ease(TimeSpan duration) =>
        new(new SpringCurve(duration.TotalSeconds, 1) { EaseInOut = true });
}
