using NotchTasks.Core.Notch;

namespace NotchTasks.Core.Modules;

/// The focus timer (Mac: timerEnd, timerTotal, startTimer, extendTimer, stopTimer and the time's-up toast). Pure logic:
/// the app's source calls Tick once a second while State is not null. Changed is raised whenever State changes.
public sealed class TimerModule
{
    private readonly IAnnouncer _announce;

    /// The announcer receives the time's-up toast.
    public TimerModule(IAnnouncer announce)
    {
        _announce = announce;
    }

    /// The running timer, or null when none runs.
    public TimerState? State { get; private set; }

    /// Raised after State changes.
    public event Action? Changed;

    /// Starts a timer of `minutes` that ends `minutes` after now. A timer already running is replaced.
    public void Start(int minutes, DateTimeOffset now)
    {
        var total = TimeSpan.FromMinutes(minutes);
        State = new TimerState(now + total, total);
        Changed?.Invoke();
    }

    /// Adds `minutes` to the running timer, to its end and its total. With no timer running, it starts one.
    public void Extend(int minutes, DateTimeOffset now)
    {
        if (State is not { } state)
        {
            Start(minutes, now);
            return;
        }
        var more = TimeSpan.FromMinutes(minutes);
        State = new TimerState(state.End + more, state.Total + more);
        Changed?.Invoke();
    }

    /// Clears the timer. No toast is made.
    public void Stop()
    {
        if (State is null)
        {
            return;
        }
        State = null;
        Changed?.Invoke();
    }

    /// Ends the timer when its time is up: the time's-up toast is announced and State is cleared. Returns true when State
    /// changed, which is only when the timer has ended.
    public bool Tick(DateTimeOffset now)
    {
        if (State is not { } state || now < state.End)
        {
            return false;
        }
        State = null;
        var minutes = (int)state.Total.TotalMinutes;
        _announce.Announce(new Toast(
            "timer",
            "#FF9F0A",
            L10n.L("Time's up"),
            L10n.L("{0} min", minutes),
            Event: "timerDone",
            Module: "timer"));
        Changed?.Invoke();
        return true;
    }
}
