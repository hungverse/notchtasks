using System.Security.Principal;

namespace NotchTasks.Services;

/// The per-user single-instance guard: a named mutex, Local\NotchTasks-<user SID>. The first launch owns it for its lifetime.
/// A second launch signals the first one through a named auto-reset event, Local\NotchTasks-<user SID>-show, before it exits:
/// the first instance then shows its window (Listen). The installer's `--quit` signals through Local\NotchTasks-<user SID>-quit
/// instead, and then waits for the first instance to exit (WaitForFirstExit).
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _show;
    private readonly EventWaitHandle _quit;

    /// `name` replaces the per-user part of the names; the tests pass a unique one so they never reach a running app.
    public SingleInstance(string? name = null)
    {
        name ??= $@"NotchTasks-{CurrentSid()}";
        _mutex = new Mutex(initiallyOwned: true, name: $@"Local\{name}", createdNew: out bool createdNew);
        IsFirst = createdNew;
        _show = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, $@"Local\{name}-show");
        _quit = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, $@"Local\{name}-quit");
    }

    /// True when this process is the first instance of the user's session.
    public bool IsFirst { get; }

    /// A second launch asks the first instance to show its window. Call it before the second instance exits.
    public void SignalFirst() => _show.Set();

    /// The first instance waits for the second launches on a background thread. Each signal runs `onSignal` there, so the
    /// caller passes it on to the UI thread. The wait ends when this instance is disposed.
    public void Listen(Action onSignal) => ListenOn(_show, onSignal, "NotchTasks second launch");

    /// `--quit` asks the first instance to quit. Call it from a second instance, which then waits with WaitForFirstExit.
    public void SignalQuit() => _quit.Set();

    /// The first instance waits for a quit request on a background thread and runs `onQuit` there (the caller passes it on
    /// to the UI thread). A request that comes before this call is kept, so the first instance still sees it.
    public void ListenQuit(Action onQuit) => ListenOn(_quit, onQuit, "NotchTasks quit request");

    /// A `--quit` that found a first instance waits here until that instance has exited, or until `timeout` passes. The first
    /// instance holds the mutex for its lifetime, so the wait ends when it exits (an abandoned mutex counts as released).
    public void WaitForFirstExit(TimeSpan timeout)
    {
        try
        {
            if (_mutex.WaitOne(timeout))
            {
                _mutex.ReleaseMutex();
            }
        }
        catch (AbandonedMutexException)
        {
            // The first instance exited without releasing the mutex: the wait is over.
        }
    }

    public void Dispose()
    {
        _quit.Dispose();
        _show.Dispose();
        _mutex.Dispose();
    }

    private static void ListenOn(EventWaitHandle handle, Action onSignal, string threadName)
    {
        var thread = new Thread(() =>
        {
            try
            {
                while (handle.WaitOne())
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
            Name = threadName,
        };
        thread.Start();
    }

    private static string CurrentSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value ?? Environment.UserName;
    }
}
