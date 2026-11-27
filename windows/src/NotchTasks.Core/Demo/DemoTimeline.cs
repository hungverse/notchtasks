namespace NotchTasks.Core.Demo;

/// The toast the demo's pill shows at a moment. The app makes the toast itself (DemoSession), so the same object is shown each time.
public enum DemoToast
{
    None,
    Ask,
    Pushed,
    Charge,
}

/// What the demo shows at one moment: whether the pill is open, whether the Claude ask waits, whether its Allow button is held
/// down, and which toast is up.
public sealed record DemoFrame(bool PillOpen, bool AskPending, bool AllowPressed, DemoToast Toast);

/// The scripted timeline of `NotchTasks.exe --demo`, in a loop of 14 s. Pure: the app drives its state from the frame at a time.
/// 0 s     the closed pill (the agents' icon is on the left wing).
/// 1.5 s   the pill opens: the Now Playing card, the agents with their limits, the build row and the focus timer.
/// 6 s     the pill closes, and the Claude ask drops as a toast (Deny, Allow).
/// 9 s     Allow is held down; 9.4 s the ask leaves and the done toast shows ("Pushed to origin/main").
/// 11 s    the closed pill with the device toast ("AirPods Pro · 80%"), until the loop restarts at 14 s.
public static class DemoTimeline
{
    /// The length of one loop.
    public static readonly TimeSpan Loop = TimeSpan.FromSeconds(14);

    /// The frames per second of a captured loop.
    public const int Fps = 12;

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

        if (s < 1.5)
        {
            return new DemoFrame(PillOpen: false, AskPending: false, AllowPressed: false, Toast: DemoToast.None);
        }
        if (s < 6)
        {
            return new DemoFrame(PillOpen: true, AskPending: false, AllowPressed: false, Toast: DemoToast.None);
        }
        if (s < 9)
        {
            return new DemoFrame(PillOpen: false, AskPending: true, AllowPressed: false, Toast: DemoToast.Ask);
        }
        if (s < 9.4)
        {
            return new DemoFrame(PillOpen: false, AskPending: true, AllowPressed: true, Toast: DemoToast.Ask);
        }
        if (s < 11)
        {
            return new DemoFrame(PillOpen: false, AskPending: false, AllowPressed: false, Toast: DemoToast.Pushed);
        }
        return new DemoFrame(PillOpen: false, AskPending: false, AllowPressed: false, Toast: DemoToast.Charge);
    }
}
