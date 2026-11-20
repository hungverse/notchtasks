using System.Windows.Threading;
using NotchTasks.Core.Devices;
using NotchTasks.Core.Modules;
using NotchTasks.Services.Devices;

namespace NotchTasks.Services.Modules;

/// The devices feed (module "devices"). It is event driven (P3d-R3): while it is started, the reader's Changed (any thread) starts a
/// 500 ms coalescing timer on the dispatcher, and the timer's tick makes one read. One read is in flight at a time, on the background,
/// and a request that arrives during a read makes one more read after it; a read that a newer request overtook is dropped. The read's
/// result goes to DevicesModule.Update on the UI thread, and the pill is published when the rows changed. A 60 s timer re-reads the
/// battery levels, and runs only while a listed device has a level. Stop stops the reader and both timers, and clears the module.
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
    private bool _wanted;
    private int _generation;  // every Start and Stop: a read that started under an older one is dropped when it lands

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
            Request();
        };
        _battery.Tick += (_, _) => Request();
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
        Request();
    }

    /// Stops the timers and the reader, unsubscribes, and clears the module. A read in flight is dropped when it lands.
    public void Stop()
    {
        if (!_started)
        {
            return;
        }
        _started = false;
        _wanted = false;
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

    private void Request()
    {
        if (!_started)
        {
            return;
        }
        _wanted = true;
        if (!_busy)
        {
            _ = Pump();
        }
    }

    /// Reads while a read is wanted. A result that a newer request overtook is dropped, and the next read runs.
    private async Task Pump()
    {
        _busy = true;
        try
        {
            while (_started && _wanted)
            {
                _wanted = false;
                var generation = _generation;
                var found = await Task.Run(() => _reader.Read()).ConfigureAwait(true);
                if (!_started || generation != _generation || _wanted)
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
