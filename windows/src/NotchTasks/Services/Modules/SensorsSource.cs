using System.Windows.Threading;
using NotchTasks.Core.Modules;
using NotchTasks.Core.Notch;
using NotchTasks.Services;

namespace NotchTasks.Services.Modules;

/// The feed of the calls and privacy modules: one source for both ids. The modules register it under "privacy" and "calls",
/// so it runs while either is started: Start and Stop count, and the watch runs while the count is above zero.
///
/// The watch thread takes the baseline reading right after it arms the registry keys, then one reading per change (ConsentStore
/// and the mic mute). Each reading is posted to the UI thread with the generation it was taken in: a reading from before a Stop
/// never applies after a later Start. A one-shot timer runs only while a call is ending (SensorsModule.EndDue), then calls Tick.
/// A one-second timer publishes while a call is live, so the call's clock follows the time. Nothing else polls. Stop at the last
/// count stops the watch and forgets the state.
public sealed class SensorsSource : IModuleSource
{
    private readonly SensorsModule _module;
    private readonly Action _publish;
    private readonly Func<SensorState> _read;
    private readonly Func<Action, ISensorWatch> _watch;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _endTimer = new();
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private ISensorWatch? _watcher;
    private int _starts;
    private int _generation;             // bumped by each Start and each last Stop: readings from an older one are dropped
    private bool _readFailureLogged;

    /// The source of `module`, which publishes the app state through `publish` on the UI thread. read and watch are the seams
    /// for tests: by default the sensors are read from the registry and the mute, and the watch is the registry watch.
    public SensorsSource(SensorsModule module, Action publish, Func<SensorState>? read = null, Func<Action, ISensorWatch>? watch = null)
    {
        _module = module;
        _publish = publish;
        _read = read ?? ReadSensors;
        _watch = watch ?? (onChange => new RegistryWatch(onChange));
        _dispatcher = Dispatcher.CurrentDispatcher;
        _endTimer.Tick += (_, _) =>
        {
            _endTimer.Stop();
            _module.Tick(DateTimeOffset.Now);
            Changed();
        };
        _clock.Tick += (_, _) => _publish();
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
        var generation = _generation;
        try
        {
            _watcher = _watch(() => OnRegistryChanged(generation));
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
        _clock.Stop();
        var watcher = _watcher;
        _watcher = null;
        watcher?.Stop();
        _module.Reset();
    }

    /// The mute button of the call card (the user's own click). When the mute is set, the sensors are read again and applied.
    /// If the mute cannot be read again, the value SetMuted just set is kept.
    public void ToggleMute()
    {
        if (!_module.ToggleMute(MicMute.SetMuted))
        {
            _publish();
            return;
        }
        var reading = _read();
        var muted = MicMute.IsMuted();
        var wanted = muted ?? _module.Sensors.MicMuted;
        OnReading(reading with { MicMuted = wanted });
    }

    /// Called on the watch's thread: the reading is taken here, and applied on the UI thread if its generation is still current.
    private void OnRegistryChanged(int generation)
    {
        SensorState next;
        try
        {
            next = _read();
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

    /// Applies a reading, reschedules the timers and publishes the state.
    private void OnReading(SensorState next)
    {
        _module.Apply(next, DateTimeOffset.Now);
        Changed();
    }

    /// Publishes the state and reschedules the timers for it.
    private void Changed()
    {
        Reschedule();
        _publish();
    }

    /// The end timer runs for the time left until a call ends, and the clock runs while a call is live. Each is stopped
    /// when it has nothing to do.
    private void Reschedule()
    {
        _endTimer.Stop();
        if (_starts > 0 && _module.EndDue is { } due)
        {
            var wait = due - DateTimeOffset.Now;
            _endTimer.Interval = wait > TimeSpan.Zero ? wait : TimeSpan.FromMilliseconds(1);
            _endTimer.Start();
        }

        if (_starts > 0 && _module.Call is not null)
        {
            if (!_clock.IsEnabled)
            {
                _clock.Start();
            }
        }
        else
        {
            _clock.Stop();
        }
    }

    /// The sensors as the registry and the mute say them now: the apps using the mic, the camera in use, and the mic's mute.
    /// A mute that cannot be read counts as not muted.
    private static SensorState ReadSensors()
    {
        var (mic, camera) = ConsentStore.InUse(new RegistryAccess(), AppFiles.AppExe);
        return new SensorState(mic, camera.Count > 0, MicMute.IsMuted() == true);
    }
}
