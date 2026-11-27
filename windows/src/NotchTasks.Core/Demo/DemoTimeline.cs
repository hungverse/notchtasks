namespace NotchTasks.Core.Demo;

/// The toast the demo's pill shows at a moment. The app makes the toast itself (DemoSession), so the same object is shown each time.
public enum DemoToast
{
    None,
    Ask,
    Pushed,
    Charge,
}

/// What the demo shows at one moment: whether the Claude ask waits, whether its Allow button is held down, which toast is up (at most
/// one), and whether the build row has finished.
public sealed record DemoFrame(bool AskPending, bool AllowPressed, DemoToast Toast, bool BuildDone);

/// The scripted timeline of `NotchTasks.exe --demo`, in a loop of 16 s, the same story as the Mac's GIF. The pill never opens its card
/// stack: it is the closed pill, with a toast growing out of it and shrinking back.
/// 0 s      the closed pill: the Claude and Codex icons, and the build "npm run build" running.
/// 2 s      the ask toast grows out: "Claude needs your approval", "web-shop · Run command: git push origin main", Deny and Allow.
/// 5 s      Allow is pressed and held for 0.4 s.
/// 5.4 s    the ask leaves; the pill shrinks back to the closed size for 0.6 s (the app's own animation, between two toasts).
/// 6 s      the green done toast grows out: "Pushed to origin/main · web-shop".
/// 8 s      the done toast shrinks back; the build reads "npm run build · Done" (its chip leaves the closed pill).
/// 10 s     the device toast: "AirPods Pro connected", "L 100% · R 98% · Case 62%".
/// 13 s     the device toast shrinks back to the closed pill, until the loop restarts at 16 s.
public static class DemoTimeline
{
    /// The length of one loop.
    public static readonly TimeSpan Loop = TimeSpan.FromSeconds(16);

    /// The frames per second of a captured loop.
    public const int Fps = 15;

    /// The frames of one captured loop: Loop at Fps.
    public static int FrameCount => (int)(Loop.TotalSeconds * Fps);

    /// The frame at a time. The time wraps round the loop, so any time (also a negative one) gives a frame of it.
    public static DemoFrame At(TimeSpan time)
    {
        var s = time.TotalSeconds % Loop.TotalSeconds;
        if (s < 0)
        {
            s += Loop.TotalSeconds;
        }

        if (s < 2)
        {
            return new DemoFrame(AskPending: false, AllowPressed: false, Toast: DemoToast.None, BuildDone: false);
        }
        if (s < 5)
        {
            return new DemoFrame(AskPending: true, AllowPressed: false, Toast: DemoToast.Ask, BuildDone: false);
        }
        if (s < 5.4)
        {
            return new DemoFrame(AskPending: true, AllowPressed: true, Toast: DemoToast.Ask, BuildDone: false);
        }
        if (s < 6)
        {
            return new DemoFrame(AskPending: false, AllowPressed: false, Toast: DemoToast.None, BuildDone: false);
        }
        if (s < 8)
        {
            return new DemoFrame(AskPending: false, AllowPressed: false, Toast: DemoToast.Pushed, BuildDone: false);
        }
        if (s < 10)
        {
            return new DemoFrame(AskPending: false, AllowPressed: false, Toast: DemoToast.None, BuildDone: true);
        }
        if (s < 13)
        {
            return new DemoFrame(AskPending: false, AllowPressed: false, Toast: DemoToast.Charge, BuildDone: true);
        }
        return new DemoFrame(AskPending: false, AllowPressed: false, Toast: DemoToast.None, BuildDone: true);
    }
}
