using System.Security.Principal;

namespace NotchTasks.Services;

/// The per-user single-instance guard: a named mutex, Local\NotchTasks-<user SID>. The first launch owns it for its lifetime.
/// A second launch signals the first one through a named auto-reset event, Local\NotchTasks-<user SID>-show, before it exits:
/// the first instance then shows its window (Listen).
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _show;

    public SingleInstance()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ?? Environment.UserName;
        _mutex = new Mutex(initiallyOwned: true, name: $@"Local\NotchTasks-{sid}", createdNew: out bool createdNew);
        IsFirst = createdNew;
        _show = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, $@"Local\NotchTasks-{sid}-show");
    }

    /// True when this process is the first instance of the user's session.
    public bool IsFirst { get; }

    /// A second launch asks the first instance to show its window. Call it before the second instance exits.
    public void SignalFirst() => _show.Set();

    /// The first instance waits for the second launches on a background thread. Each signal runs `onSignal` there, so the
    /// caller passes it on to the UI thread. The wait ends when this instance is disposed.
    public void Listen(Action onSignal)
    {
        var thread = new Thread(() =>
        {
            try
            {
                while (_show.WaitOne())
                {
                    onSignal();
                }
            }
            catch (ObjectDisposedException)
            {
                // The app is exiting: the event was disposed under the wait.
            }
        })
        {
            IsBackground = true,
            Name = "NotchTasks second launch",
        };
        thread.Start();
    }

    public void Dispose()
    {
        _show.Dispose();
        _mutex.Dispose();
    }
}
