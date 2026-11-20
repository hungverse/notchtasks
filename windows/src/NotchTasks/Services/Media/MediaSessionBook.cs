namespace NotchTasks.Services.Media;

/// Runs something when disposed (the subscription handles the book keeps).
public sealed class ActionDisposable : IDisposable
{
    private Action? _action;

    public ActionDisposable(Action action) => _action = action;

    public void Dispose()
    {
        var action = Interlocked.Exchange(ref _action, null);
        action?.Invoke();
    }
}

/// The bookkeeping behind SmtcReader, with the OS types left out so it can be tested. It holds one lock around everything the
/// reader's threads share: the running flag, the start generation, the attached manager, and the subscriptions by session id.
///
/// - BeginStart gives each Start a generation. Attach takes the manager only for the current generation and only once, so a Start
///   that was overtaken by a Stop (or a second Start) while its request was pending does nothing.
/// - Sync brings the subscriptions in line with the manager's listed sessions. It does nothing once stopped or for a manager that is
///   no longer attached, so a session change that lands after Stop cannot subscribe again.
/// - A session is matched by its id and by its object: a session that closed and reopened under the same id is resubscribed.
public sealed class MediaSessionBook<TManager, TSession>
    where TManager : class
    where TSession : class
{
    private readonly object _gate = new();
    private readonly Func<TManager, IReadOnlyList<TSession>> _list;
    private readonly Func<TSession, string> _idOf;
    private readonly Func<TSession, IDisposable> _subscribe;
    private readonly Func<TManager, IDisposable> _watch;
    private readonly Dictionary<string, (TSession Session, IDisposable Handle)> _entries = new(StringComparer.Ordinal);
    private int _generation;
    private bool _running;
    private TManager? _manager;
    private IDisposable? _managerWatch;

    /// list reads a manager's sessions; idOf names a session; subscribe hooks a session's events (its handle takes them off);
    /// watch hooks a manager's session-list events (the handle takes them off).
    public MediaSessionBook(
        Func<TManager, IReadOnlyList<TSession>> list,
        Func<TSession, string> idOf,
        Func<TSession, IDisposable> subscribe,
        Func<TManager, IDisposable> watch)
    {
        _list = list;
        _idOf = idOf;
        _subscribe = subscribe;
        _watch = watch;
    }

    /// The number of sessions subscribed now.
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// The attached manager, or null when the book is stopped or has not been attached.
    public TManager? Manager
    {
        get
        {
            lock (_gate)
            {
                return _manager;
            }
        }
    }

    /// Marks the book running and returns the generation of this start.
    public int BeginStart()
    {
        lock (_gate)
        {
            _running = true;
            return ++_generation;
        }
    }

    /// Attaches the manager a start asked for. False when that start is no longer current, or a manager is already attached.
    public bool Attach(int generation, TManager manager)
    {
        lock (_gate)
        {
            if (!_running || generation != _generation || _manager is not null)
            {
                return false;
            }
            _manager = manager;
            _managerWatch = _watch(manager);
            return true;
        }
    }

    /// Brings the subscriptions in line with the manager's listed sessions. Returns false, and changes nothing, when the book is
    /// stopped or the manager is not the attached one.
    public bool Sync(TManager manager)
    {
        var listed = _list(manager);
        var byId = new Dictionary<string, TSession>(StringComparer.Ordinal);
        foreach (var session in listed)
        {
            byId[_idOf(session)] = session;
        }

        lock (_gate)
        {
            if (!_running || !ReferenceEquals(_manager, manager))
            {
                return false;
            }
            foreach (var id in _entries.Keys.ToList())
            {
                var entry = _entries[id];
                if (byId.TryGetValue(id, out var listedNow) && ReferenceEquals(listedNow, entry.Session))
                {
                    continue;
                }
                entry.Handle.Dispose();
                _entries.Remove(id);
            }
            foreach (var (id, session) in byId)
            {
                if (!_entries.ContainsKey(id))
                {
                    _entries[id] = (session, _subscribe(session));
                }
            }
            return true;
        }
    }

    /// Takes every subscription and the manager's watch off, and detaches the manager. A Start still pending does nothing when it lands.
    public void Stop()
    {
        lock (_gate)
        {
            _running = false;
            _generation++;
            foreach (var entry in _entries.Values)
            {
                entry.Handle.Dispose();
            }
            _entries.Clear();
            _managerWatch?.Dispose();
            _managerWatch = null;
            _manager = null;
        }
    }

    /// The subscribed session with an id, or null.
    public TSession? Find(string id)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(id, out var entry) ? entry.Session : null;
        }
    }
}
