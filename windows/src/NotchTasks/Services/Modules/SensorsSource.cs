using System.Windows.Threading;
using NotchTasks.Core.Modules;
using NotchTasks.Core.Notch;
using NotchTasks.Services;

namespace NotchTasks.Services.Modules;

/// The OS calls the sensors source makes: the reading of the sensors, the registry watch, and the default mic's mute. The
/// defaults read this PC; tests pass fakes, so no test reads the registry, waits on it or touches the mic.
public sealed record SensorReaders(
    Func<SensorState> Read,
    Func<Action, ISensorWatch> Watch,
    Func<bool, bool> SetMuted,
    Func<bool?> IsMuted)
{
    /// The readings of this PC: the registry's consent keys, the registry watch, and the default mic's mute.
    public static SensorReaders Default { get; } = new(
        () => ReadSensors(MicMute.IsMuted),
        onChange => new RegistryWatch(onChange),
        MicMute.SetMuted,
        MicMute.IsMuted);

    /// The sensors as the registry and the mute say them now: the apps using the mic, the camera in use, and the mic's mute.
    /// A mute that cannot be read counts as not muted.
    public static SensorState ReadSensors(Func<bool?> isMuted)
    {
        var (mic, camera) = ConsentStore.InUse(new RegistryAccess(), AppFiles.AppExe);
        return new SensorState(mic, camera.Count > 0, isMuted() == true);
    }
}

/// The feed of the calls and privacy modules: one source for both ids. The modules register it under "privacy" and "calls",
/// so it runs while either is started: Start and Stop count, and the watch runs while the count is above zero.
///
/// The watch thread takes the baseline reading right after it arms the registry keys, then one reading per change (ConsentStore
/// and the mic mute). Each reading is posted to the UI thread with the generation it was taken in: a reading from before a Stop
/// never applies after a later Start. A reading that changes nothing shown (the same mic apps and flags, the same call) does not
/// publish. A one-shot timer runs only while a call is ending (SensorsModule.EndDue), then calls Tick. While a call is live, the
/// shared one-second clock ticks this source and asks for a publish, so the call clock follows the time. Nothing else polls.
/// Stop at the last count stops the watch and forgets the state.
public sealed class SensorsSource : IModuleSource, IClockUser
{
    private readonly SensorsModule _module;
    private readonly Action _publish;
    private readonly SharedClock _clock;
    private readonly SensorReaders _readers;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _endTimer = new();
    private ISensorWatch? _watcher;
    private int _starts;
    private int _generation;             // bumped by each Start and each last Stop: readings from an older one are dropped
    private bool _readFailureLogged;

    /// The source of `module`, which publishes the app state through `publish` on the UI thread. clock is the shared one-second
    /// ticker. readers are the OS calls; null takes this PC's (SensorReaders.Default).
    public SensorsSource(SensorsModule module, Action publish, SharedClock clock, SensorReaders? readers = null)
    {
        _module = module;
        _publish = publish;
        _clock = clock;
        _readers = readers ?? SensorReaders.Default;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _endTimer.Tick += (_, _) =>
        {
            _endTimer.Stop();
            _module.Tick(DateTimeOffset.Now);
            Changed();
        };
        _clock.Add(this);
    }

    /// Counts a start. The first one starts the watch; its baseline comes from the watch thread, so Start never reads the
    /// sensors itself. A watch that cannot be made is logged, and the sensors stay as they were.
    public void Start()
    {
        _starts++;
        if (_starts > 1)
        {
            return;
        }
        _generation++;
        _readFailureLogged = false;
        var generation = _generation;
        try
        {
            _watcher = _readers.Watch(() => OnRegistryChanged(generation));
        }
        catch (Exception e)
        {
            ErrorLog.Write(e);
        }
    }

    /// Counts a stop. The last one stops the watch and the timers and forgets the calls and the sensors.
    public void Stop()
    {
        if (_starts == 0)
        {
            return;
        }
        _starts--;
        if (_starts > 0)
        {
            return;
        }
        _generation++;  // readings still queued from this run are now stale
        _endTimer.Stop();
        var watcher = _watcher;
        _watcher = null;
        watcher?.Stop();
        _module.Reset();
        _clock.Sync();
    }

    /// The calls module was turned off during a call: the call goes at once, with no toast, and the timers follow. Privacy, if it
    /// is still on, keeps its sensors. The caller publishes.
    public void ForgetCall()
    {
        _module.ForgetCall();
        Reschedule();
    }

    /// The mute button of the call card (the user's own click). When the mute is set, the sensors are read again and applied.
    /// If the mute cannot be read again, the value SetMuted just set is kept.
    public void ToggleMute()
    {
        if (!_module.ToggleMute(_readers.SetMuted))
        {
            _publish();
            return;
        }
        var reading = _readers.Read();
        var wanted = _readers.IsMuted() ?? _module.Sensors.MicMuted;
        // The mute changed what is shown, so this always publishes (the reading may equal the one the module already has).
        _module.Apply(reading with { MicMuted = wanted }, DateTimeOffset.Now);
        Changed();
    }

    /// The clock ticks this source while a call is live: the call clock follows the time.
    public bool NeedsClock => _starts > 0 && _module.Call is not null;

    /// One second passed while a call is live: its shown time has moved, so the app publishes.
    public bool ClockTick(DateTimeOffset now) => _module.Call is not null;

    /// Called on the watch's thread: the reading is taken here, and applied on the UI thread if its generation is still current.
    private void OnRegistryChanged(int generation)
    {
        SensorState next;
        try
        {
            next = _readers.Read();
        }
        catch (Exception e)
        {
            if (!_readFailureLogged)
            {
                _readFailureLogged = true;
                ErrorLog.Write(e);
            }
            return;
        }
        _dispatcher.BeginInvoke(new Action(() =>
        {
            if (generation == _generation && _starts > 0)
            {
                OnReading(next);
            }
        }));
    }

    /// Applies a reading. It publishes only when what is shown changed: the sensors, or the call. The same reading still
    /// reschedules the timers, since the module may have moved its end.
    private void OnReading(SensorState next)
    {
        var before = _module.Sensors;
        var callBefore = _module.Call;
        _module.Apply(next, DateTimeOffset.Now);
        if (next.Same(before) && Equals(callBefore, _module.Call))
        {
            Reschedule();
            return;
        }
        Changed();
    }

    /// Publishes the state and reschedules the timers for it.
    private void Changed()
    {
        Reschedule();
        _publish();
    }

    /// The end timer runs for the time left until a call ends. The shared clock is synced, so it runs while a call is live.
    private void Reschedule()
    {
        _endTimer.Stop();
        if (_starts > 0 && _module.EndDue is { } due)
        {
            var wait = due - DateTimeOffset.Now;
            _endTimer.Interval = wait > TimeSpan.Zero ? wait : TimeSpan.FromMilliseconds(1);
            _endTimer.Start();
        }
        _clock.Sync();
    }
}
