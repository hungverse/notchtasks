using System.Windows.Threading;
using NotchTasks.Core.Modules;

namespace NotchTasks.Services.Modules;

/// The timer module's feed. A DispatcherTimer at 1 s runs only while the source is started and a timer runs, so an idle
/// timer module costs no wake-ups. Each tick ends the timer when its time is up, and publishes the app state while a timer
/// runs, so the chip's and the card's mm:ss follow the clock. Stop stops the timer and clears the module (it keeps no state).
public sealed class TimerSource : IModuleSource
{
    private readonly TimerModule _module;
    private readonly Action _publish;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _started;

    /// The source of `module`, which publishes the app state through `publish` (on the UI thread).
    public TimerSource(TimerModule module, Action publish)
    {
        _module = module;
        _publish = publish;
        _timer.Tick += OnTick;
        _module.Changed += Sync;
    }

    public void Start()
    {
        _started = true;
        Sync();
    }

    public void Stop()
    {
        _started = false;
        _timer.Stop();
        _module.Stop();
    }

    /// The timer runs while the source is started and a timer is running; otherwise it is stopped.
    private void Sync()
    {
        var running = _started && _module.State is not null;
        if (!running)
        {
            _timer.Stop();
        }
        else if (!_timer.IsEnabled)
        {
            _timer.Start();
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var ended = _module.Tick(DateTimeOffset.Now);
        if (ended || _module.State is not null)
        {
            _publish();
        }
    }
}
