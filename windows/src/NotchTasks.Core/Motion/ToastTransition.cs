namespace NotchTasks.Core.Motion;

/// The steps of a change of toast, as the Mac's content follows its shape: the old content fades out, the shape moves, and the new
/// content rises as the shape's spring passes its rise point. A shape larger than the closed pill is never empty for more than a frame.
public enum ToastStep
{
    /// The old content's opacity goes to 0 (OldOut long). The shape starts moving MorphLead into it.
    FadeOutOld,

    /// The shape springs to the new size, or to the closed pill when there is none.
    Morph,

    /// The new content is shown, laid out at opacity 0, once the old content is gone.
    ShowNew,

    /// The new content's opacity rises to 1 (NewIn long), starting at the later of the old content being gone and the spring's RisesAt.
    FadeInNew,
}

/// The timing and the plan of a change of toast. Pure: the pill runs the steps at these times.
public static class ToastTransition
{
    /// How long the old content takes to fade out.
    public static readonly TimeSpan OldOut = TimeSpan.FromMilliseconds(80);

    /// How far into the old content's fade the shape starts moving.
    public static readonly TimeSpan MorphLead = TimeSpan.FromMilliseconds(40);

    /// How long the new content takes to rise.
    public static readonly TimeSpan NewIn = TimeSpan.FromMilliseconds(120);

    /// The share of the shape's way to its target at which the new content starts to rise (or the old content is gone, if that is later).
    public const double RisesAt = 0.45;

    /// The steps from the shown toast (oldShown: a toast's content is on screen) to the next one (newShown: a toast is to show).
    /// Nothing to change gives no steps. Between two toasts the old content leaves and the shape moves straight to the new size: the
    /// closed pill is not shown between them.
    public static IReadOnlyList<ToastStep> Plan(bool oldShown, bool newShown) => (oldShown, newShown) switch
    {
        (true, true) => new[] { ToastStep.FadeOutOld, ToastStep.Morph, ToastStep.ShowNew, ToastStep.FadeInNew },
        (true, false) => new[] { ToastStep.FadeOutOld, ToastStep.Morph },
        (false, true) => new[] { ToastStep.Morph, ToastStep.ShowNew, ToastStep.FadeInNew },
        _ => Array.Empty<ToastStep>(),
    };

    /// The share of a shrinking shape's way to its target at which its old content starts to fade (the mirror of RisesAt).
    public const double SheddingAt = 0.55;

    /// The steps of a shrink from a shown toast (to a smaller toast, or to the closed pill): the shape moves at once, its old content
    /// stays until the shape has gone SheddingAt of its way and then fades (OldOut), and the new content is shown once the old is gone.
    public static IReadOnlyList<ToastStep> PlanShrink(bool newShown) =>
        newShown
            ? new[] { ToastStep.Morph, ToastStep.FadeOutOld, ToastStep.ShowNew, ToastStep.FadeInNew }
            : new[] { ToastStep.Morph, ToastStep.FadeOutOld };

    /// Whether a change of shape is a shrink: the target's area is smaller than the shape's on screen now.
    public static bool IsShrink(double width, double height, double targetWidth, double targetHeight) =>
        targetWidth * targetHeight < width * height;

    /// The time from the start of a spring to the first moment it reaches a share of its way (0 to 1), scanned in 1 ms steps.
    public static TimeSpan TimeTo(SpringCurve curve, double share)
    {
        for (var t = TimeSpan.Zero; t < TimeSpan.FromSeconds(curve.Settle); t += TimeSpan.FromMilliseconds(1))
        {
            if (curve.At(t.TotalSeconds) >= share)
            {
                return t;
            }
        }
        return TimeSpan.FromSeconds(curve.Settle);
    }

    /// The time from the start of a spring to its rise point: the first time the curve reaches RisesAt.
    public static TimeSpan RiseTime(SpringCurve curve) => TimeTo(curve, RisesAt);

    /// When a shrink's old content is gone: the spring reaches SheddingAt, and the content takes OldOut to fade.
    public static TimeSpan OldGoneInShrink(SpringCurve curve) => TimeTo(curve, SheddingAt) + OldOut;

    /// How long after the new content is shown (showNew, from the change's start) its own rise must wait, so it begins at the shape's
    /// rise point (morphStart + RiseTime). Zero when that point is already past.
    public static TimeSpan RisePause(SpringCurve curve, TimeSpan morphStart, TimeSpan showNew)
    {
        var pause = morphStart + RiseTime(curve) - showNew;
        return pause < TimeSpan.Zero ? TimeSpan.Zero : pause;
    }
}
