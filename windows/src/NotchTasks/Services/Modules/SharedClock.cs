using System.Windows.Threading;

namespace NotchTasks.Services.Modules;

/// A feed that uses the shared one-second clock: the timer and the calls. NeedsClock says whether the feed is live now (a timer
/// runs, or a call is live). ClockTick runs once a second while it is; it returns true when the feed's shown state changed (or
/// still shows a live one), so the app publishes.
public interface IClockUser
{
    bool NeedsClock { get; }

    bool ClockTick(DateTimeOffset now);
}

/// The one 1 s ticker of the timer and the calls. It runs only while a user needs it, so an idle app wakes for nothing, and
/// each tick publishes once for all the users that asked for it (not once per user). Users join with Add, and call Sync when
/// their need may have changed. The ticker is made on the UI thread.
public sealed class SharedClock
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly List<IClockUser> _users = new();
    private readonly Action _publish;

    /// publish is called at most once per tick, on the UI thread.
    public SharedClock(Action publish)
    {
        _publish = publish;
        _timer.Tick += (_, _) => OnTick();
    }

    /// Adds a user. Users are added once, when their feed is made.
    public void Add(IClockUser user) => _users.Add(user);

    /// Runs the ticker while any user needs it, and stops it when none does.
    public void Sync()
    {
        if (_users.Any(u => u.NeedsClock))
        {
            if (!_timer.IsEnabled)
            {
                _timer.Start();
            }
            return;
        }
        _timer.Stop();
    }

    /// One tick: every user that needs the clock ticks, and the app publishes once if any of them changed.
    private void OnTick()
    {
        var now = DateTimeOffset.Now;
        var changed = false;
        foreach (var user in _users.ToList())
        {
            if (user.NeedsClock)
            {
                changed |= user.ClockTick(now);
            }
        }
        if (changed)
        {
            _publish();
        }
        Sync();
    }
}
