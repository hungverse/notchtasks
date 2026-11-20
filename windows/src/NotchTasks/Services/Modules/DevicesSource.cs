using System.Windows.Threading;
using NotchTasks.Core.Devices;
using NotchTasks.Core.Modules;
using NotchTasks.Services.Devices;

namespace NotchTasks.Services.Modules;

/// The devices feed (module "devices"). It is event driven (P3d-R3): while it is started, the reader's Changed (any thread) starts a
/// 500 ms coalescing timer on the dispatcher, and the timer's tick makes one full read. A 60 s timer makes a battery-only read, and runs
/// only while a listed device has a level. One read is in flight at a time, on the background. A request that arrives during a read
/// makes one more read after it; a full request covers a battery one. A read that Stop overtook is dropped when it lands.
///
/// A failed read (the reader returns null) changes nothing: the rows, the baseline and the battery timer stay as they were, so a failed
/// read never reads as "all devices gone" and never toasts. A good read's result goes to DevicesModule.Update on the UI thread, and the
/// pill is published when the rows changed. Stop stops the reader and both timers, and clears the module.
public sealed class DevicesSource : IModuleSource
{
    private static readonly TimeSpan CoalesceFor = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan BatteryEvery = TimeSpan.FromSeconds(60);

    private readonly DevicesModule _module;
    private readonly IDeviceReader _reader;
    private readonly Action _publish;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _coalesce = new() { Interval = CoalesceFor };
    private readonly DispatcherTimer _battery = new() { Interval = BatteryEvery };
    private bool _started;
    private bool _busy;
    private bool _wantedFull;
    private bool _wantedBattery;
    private int _generation;  // bumped on every Stop (a Start does not bump it): a read that started before a Stop is dropped when it lands

    /// module gets the reads; reader is the device watcher (ContainerReader in production); publish is called when the rows changed.
    public DevicesSource(DevicesModule module, IDeviceReader reader, Action publish)
    {
        _module = module;
        _reader = reader;
        _publish = publish;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _coalesce.Tick += (_, _) =>
        {
            _coalesce.Stop();
            Request(full: true);
        };
        _battery.Tick += (_, _) => Request(full: false);
    }

    public void Start()
    {
        if (_started)
        {
            return;
        }
        _started = true;
        _reader.Changed += OnReaderChanged;
        _reader.Start();
        Request(full: true);
    }

    /// Stops the timers and the reader, unsubscribes, and clears the module. A read in flight is dropped when it lands.
    public void Stop()
    {
        if (!_started)
        {
            return;
        }
        _started = false;
        _wantedFull = false;
        _wantedBattery = false;
        _generation++;
        _coalesce.Stop();
        _battery.Stop();
        _reader.Changed -= OnReaderChanged;
        _reader.Stop();
        _module.Clear();
    }

    /// Any thread: the coalescing timer starts on the dispatcher if it is not running. A burst of events is one read.
    private void OnReaderChanged() => _dispatcher.BeginInvoke(new Action(() =>
    {
        if (_started && !_coalesce.IsEnabled)
        {
            _coalesce.Start();
        }
    }));

    /// Asks for a read: a full one (the containers and their kinds) or a battery-only one. A full request covers a battery one.
    private void Request(bool full)
    {
        if (!_started)
        {
            return;
        }
        if (full)
        {
            _wantedFull = true;
        }
        else
        {
            _wantedBattery = true;
        }
        if (!_busy)
        {
            _ = Pump();
        }
    }

    /// Reads while a read is wanted. A read that Stop overtook is dropped; a failed read is skipped; the next wanted read runs.
    private async Task Pump()
    {
        _busy = true;
        try
        {
            while (_started && (_wantedFull || _wantedBattery))
            {
                var full = _wantedFull;
                _wantedFull = false;
                _wantedBattery = false;
                var generation = _generation;
                var found = await Task.Run(() => full ? _reader.Read() : _reader.ReadBatteries()).ConfigureAwait(true);
                if (!_started || generation != _generation || found is null)
                {
                    continue;
                }
                Apply(found);
            }
        }
        catch (Exception e)
        {
            ErrorLog.Write(e);
        }
        finally
        {
            _busy = false;
        }
    }

    /// The UI thread: the module takes the read, the battery timer runs while a listed device has a level, and the pill is published
    /// when the rows changed.
    private void Apply(IReadOnlyList<DeviceFact> found)
    {
        var changed = _module.Update(found);
        if (_module.Devices.Any(d => d.Lowest is not null))
        {
            if (!_battery.IsEnabled)
            {
                _battery.Start();
            }
        }
        else
        {
            _battery.Stop();
        }
        if (changed)
        {
            _publish();
        }
    }
}
