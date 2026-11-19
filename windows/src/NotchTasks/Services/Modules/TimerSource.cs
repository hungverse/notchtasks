using NotchTasks.Core.Modules;

namespace NotchTasks.Services.Modules;

/// The timer module's feed. While the source is started and a timer runs, the shared one-second clock ticks it: each tick ends
/// the timer when its time is up, and asks for a publish while a timer runs, so the chip's and the card's mm:ss follow the
/// clock. An idle timer module costs no wake-ups. Stop stops following the module and clears it (it keeps no state).
public sealed class TimerSource : IModuleSource, IClockUser
{
    private readonly TimerModule _module;
    private readonly SharedClock _clock;
    private bool _started;

    /// The source of `module`. clock is the app's shared one-second ticker, which publishes for the timer and the calls.
    public TimerSource(TimerModule module, SharedClock clock)
    {
        _module = module;
        _clock = clock;
        _clock.Add(this);
    }

    /// Starts following the module: the clock runs while a timer is running.
    public void Start()
    {
        _started = true;
        _module.Changed -= Sync;  // never subscribed twice
        _module.Changed += Sync;
        Sync();
    }

    /// Stops following the module and clears it (the source keeps no state). The clock stops with it, unless a call needs it.
    public void Stop()
    {
        _started = false;
        _module.Changed -= Sync;
        _module.Stop();
        _clock.Sync();
    }

    /// True while the source is started and a timer runs.
    public bool NeedsClock => _started && _module.State is not null;

    /// One second passed: the timer ends when its time is up. Returns true while a timer runs, or when it has just ended.
    public bool ClockTick(DateTimeOffset now)
    {
        var ended = _module.Tick(now);
        return ended || _module.State is not null;
    }

    private void Sync() => _clock.Sync();
}
