using System.Windows.Threading;
using NotchTasks.Core.Modules;
using NotchTasks.Core.Notch;
using NotchTasks.Services;

namespace NotchTasks.Services.Modules;

/// The feed of the calls and privacy modules: one source for both ids. The modules register it under "privacy" and "calls",
/// so it runs while either is started: Start and Stop count, and the watch runs while the count is above zero.
///
/// Start reads the sensors once (the baseline), then starts the registry watch. The watch thread reads the sensors again on
/// each change (ConsentStore and the mic mute), and the reading is posted to the UI thread, where it is applied. A one-shot
/// timer runs only while a call is ending (SensorsModule.EndDue), then calls Tick. A one-second timer publishes while a call
/// is live, so the call's clock follows the time. Nothing else polls. Stop at the last count stops the watch and forgets the
/// state.
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

    /// Counts a start. The first one reads the sensors and starts the watch; later ones only count.
    public void Start()
    {
        _starts++;
        if (_starts > 1)
        {
            return;
        }
        try
        {
            OnReading(_read(), publish: false);
            _watcher = _watch(OnRegistryChanged);
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
        _endTimer.Stop();
        _clock.Stop();
        var watcher = _watcher;
        _watcher = null;
        watcher?.Stop();
        _module.Reset();
    }

    /// The mute button of the call card (the user's own click): sets the default mic's mute, then reads the sensors again and
    /// applies the reading. Nothing happens when no call is on.
    public void ToggleMute()
    {
        _module.ToggleMute(MicMute.SetMuted);
        OnReading(_read(), publish: true);
    }

    /// Called on the watch's thread: the reading is taken here, and applied on the UI thread.
    private void OnRegistryChanged()
    {
        SensorState next;
        try
        {
            next = _read();
        }
        catch (Exception e)
        {
            ErrorLog.Write(e);
            return;
        }
        _dispatcher.BeginInvoke(new Action(() =>
        {
            if (_starts > 0)
            {
                OnReading(next, publish: true);
            }
        }));
    }

    /// Applies a reading and reschedules the timers. It publishes when asked (the baseline at Start does not: the pill is not
    /// built yet).
    private void OnReading(SensorState next, bool publish)
    {
        _module.Apply(next, DateTimeOffset.Now);
        if (publish)
        {
            Changed();
        }
        else
        {
            Reschedule();
        }
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
