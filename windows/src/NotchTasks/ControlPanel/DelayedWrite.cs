using System.Windows.Threading;

namespace NotchTasks.ControlPanel;

/// Runs a write once the value has stopped changing for 150 ms, so a slider writes its last value and not each step.
/// The timer runs only between a change and its write. Flush runs a pending write at once (when the pane is left).
public sealed class DelayedWrite
{
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(150);

    private readonly DispatcherTimer _timer;
    private Action? _write;

    public DelayedWrite()
    {
        _timer = new DispatcherTimer { Interval = Delay };
        _timer.Tick += (_, _) => Run();
    }

    /// True from a change until its write has run.
    public bool Pending => _timer.IsEnabled;

    /// Sets the write to run after the delay; a later change replaces it and starts the delay again.
    public void Schedule(Action write)
    {
        _write = write;
        _timer.Stop();
        _timer.Start();
    }

    /// Runs the pending write now, if there is one.
    public void Flush()
    {
        if (Pending)
        {
            Run();
        }
    }

    private void Run()
    {
        _timer.Stop();
        var write = _write;
        _write = null;
        write?.Invoke();
    }
}
