namespace NotchTasks.Core.Asks;

/// Watches the Asks folder and reports the pending list when it changes (asks.js watchAsks).
/// A FileSystemWatcher kicks a scan 50 ms after each change, and a scan every 2 s catches a missed event.
/// onChange runs on a thread-pool thread (a timer or a watcher callback), one call at a time, never on the thread that
/// created the watcher; a UI must marshal to its dispatcher. The list is reported only when its ids or times change.
/// Dispose stops both the watcher and the timers.
public sealed class AskWatcher : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(50);

    private readonly AskStore _store;
    private readonly Action<IReadOnlyList<AgentAsk>> _onChange;
    private readonly int _waitSeconds;
    private readonly object _gate = new();
    private readonly object _notifyGate = new();
    private readonly FileSystemWatcher? _watcher;
    private readonly Timer _debounceTimer;
    private readonly Timer _periodicTimer;
    private string? _last;
    private bool _disposed;

    /// Starts watching: reports the current list at once, then on each change.
    public AskWatcher(AskStore store, Action<IReadOnlyList<AgentAsk>> onChange,
        int waitSeconds = AskStore.DefaultWaitSeconds, int intervalMs = 2000)
    {
        _store = store;
        _onChange = onChange;
        _waitSeconds = waitSeconds;
        Directory.CreateDirectory(store.Dir);

        _debounceTimer = new Timer(_ => Scan(), null, Timeout.Infinite, Timeout.Infinite);
        _periodicTimer = new Timer(_ => Scan(), null, Timeout.Infinite, Timeout.Infinite);
        try
        {
            _watcher = new FileSystemWatcher(store.Dir)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            _watcher.Created += Kick;
            _watcher.Changed += Kick;
            _watcher.Deleted += Kick;
            _watcher.Renamed += Kick;
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        {
            _watcher = null; // no watch here: the periodic scan still runs
        }

        Scan();
        _periodicTimer.Change(intervalMs, intervalMs);
    }

    private void Kick(object sender, FileSystemEventArgs e)
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                _debounceTimer.Change(Debounce, Timeout.InfiniteTimeSpan);
            }
        }
    }

    /// Compares the pending list with the last one reported, and reports it when it changed. onChange runs outside
    /// _gate, so a callback that waits on a UI thread cannot deadlock with Dispose; _notifyGate keeps the calls in order.
    private void Scan()
    {
        lock (_notifyGate)
        {
            IReadOnlyList<AgentAsk> list;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }
                list = _store.Pending(DateTimeOffset.UtcNow, _waitSeconds);
                var key = string.Join("|", list.Select(a => a.Id + "@" + a.At.ToString("O")));
                if (key == _last)
                {
                    return;
                }
                _last = key;
            }
            _onChange(list);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
        }
        _watcher?.Dispose();
        _debounceTimer.Dispose();
        _periodicTimer.Dispose();
    }
}
