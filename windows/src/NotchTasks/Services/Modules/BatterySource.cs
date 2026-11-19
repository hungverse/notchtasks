using System.Windows.Threading;
using NotchTasks.Core.Modules;

namespace NotchTasks.Services.Modules;

/// The battery module's feed. It reads the power status when started, on each power status change (Refresh, forwarded
/// by AppState.PowerStatusChanged), and every 30 s while the PC has a battery, as the Mac polls (ticks % 30). A PC with
/// no battery runs no timer at all. Stop stops the timer and clears the module (it keeps no state).
public sealed class BatterySource : IModuleSource
{
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(30);

    private readonly BatteryModule _module;
    private readonly Action _publish;
    private readonly Func<PowerFacts> _read;
    private readonly DispatcherTimer _timer = new() { Interval = Poll };
    private bool _started;
    private bool _hasBattery;

    /// The source of `module`, which publishes the app state through `publish` (on the UI thread). `read` is the power
    /// status reader; the default is the Windows call.
    public BatterySource(BatteryModule module, Action publish, Func<PowerFacts>? read = null)
    {
        _module = module;
        _publish = publish;
        _read = read ?? new Func<PowerFacts>(PowerFacts.Read);
        _timer.Tick += (_, _) => ReadNow();
    }

    /// Reads the power status now, then keeps it up to date while the source runs.
    public void Start()
    {
        _started = true;
        ReadNow();
    }

    /// Stops the timer, and clears the module (the source keeps no state).
    public void Stop()
    {
        _started = false;
        _hasBattery = false;
        _timer.Stop();
        _module.Clear();
    }

    /// Reads the power status now, because it changed. Does nothing while the source is stopped.
    public void Refresh()
    {
        if (_started)
        {
            ReadNow();
        }
    }

    /// Reads the power status, publishes a change, and keeps the 30 s timer in step with whether there is a battery.
    private void ReadNow()
    {
        var facts = _read();
        _hasBattery = facts.HasBattery;
        if (_module.Update(facts))
        {
            _publish();
        }
        Sync();
    }

    /// The timer runs while the source is started and the PC has a battery; otherwise it is stopped.
    private void Sync()
    {
        var running = _started && _hasBattery;
        if (!running)
        {
            _timer.Stop();
        }
        else if (!_timer.IsEnabled)
        {
            _timer.Start();
        }
    }
}
