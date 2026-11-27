namespace NotchTasks.Core.Motion;

/// The steps of a change of toast, in order (the Mac's): the old content fades out completely, then the shape moves to the new size,
/// then the new content fades in. Two contents are never visible at once, and a shape never shrinks around content still on screen.
public enum ToastStep
{
    /// The old content's opacity goes to 0 (about 120 ms).
    FadeOutOld,

    /// The new content is laid out at opacity 0, so the shape can be sized to it.
    ShowNew,

    /// The shape springs to the new toast's size, or to the closed pill when there is none.
    Morph,

    /// The new content's opacity goes to 1 (about 150 ms), after the shape has moved.
    FadeInNew,
}

/// The plan for a change of toast. Pure: the pill runs the steps in order, each one after the previous has finished.
public static class ToastTransition
{
    /// The steps from the shown toast (oldShown: a toast's content is on screen) to the next one (newShown: a toast is to show).
    /// Nothing to change gives no steps.
    public static IReadOnlyList<ToastStep> Plan(bool oldShown, bool newShown) => (oldShown, newShown) switch
    {
        (true, true) => new[] { ToastStep.FadeOutOld, ToastStep.ShowNew, ToastStep.Morph, ToastStep.FadeInNew },
        (true, false) => new[] { ToastStep.FadeOutOld, ToastStep.Morph },
        (false, true) => new[] { ToastStep.ShowNew, ToastStep.Morph, ToastStep.FadeInNew },
        _ => Array.Empty<ToastStep>(),
    };
}
