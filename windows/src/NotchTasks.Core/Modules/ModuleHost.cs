namespace NotchTasks.Core.Modules;

/// Starts and stops the module sources as their modules turn on and off (Settings modules and disabled). A source runs
/// only while its module is on. A module that is turned off has its source stopped, then onTurnedOff is called with its
/// id, so the app can drop the module's toasts. Pure logic: the app calls Apply on the UI thread whenever the active
/// modules change. A source that throws does not stop the others: the error goes to onError, and the loop goes on.
public sealed class ModuleHost
{
    private readonly IReadOnlyDictionary<string, IModuleSource> _sources;
    private readonly Action<string> _onTurnedOff;
    private readonly Action<string, Exception>? _onError;
    private readonly HashSet<string> _active = new(StringComparer.Ordinal);
    private readonly HashSet<string> _running = new(StringComparer.Ordinal);

    /// The sources by module id. A module without a source is active but has nothing to run. onError gets a message and
    /// the exception of a source that throws; it may be null.
    public ModuleHost(
        IReadOnlyDictionary<string, IModuleSource> sources,
        Action<string> onTurnedOff,
        Action<string, Exception>? onError = null)
    {
        _sources = sources;
        _onTurnedOff = onTurnedOff;
        _onError = onError;
    }

    /// The ids of the modules whose sources are running. An id is here only after its Start has returned.
    public IReadOnlySet<string> Running => _running;

    /// Makes `active` the set of active modules. Each module turned off stops its source (if any) and calls onTurnedOff;
    /// each module newly active starts its source. An unchanged set does nothing.
    public void Apply(IReadOnlyCollection<string> active)
    {
        var wanted = new HashSet<string>(active, StringComparer.Ordinal);
        foreach (var id in _active.Where(id => !wanted.Contains(id)).ToList())
        {
            _active.Remove(id);
            StopSource(id);
            _onTurnedOff(id);
        }
        foreach (var id in wanted)
        {
            _active.Add(id);
            if (_sources.ContainsKey(id) && !_running.Contains(id))
            {
                StartSource(id);
            }
        }
    }

    /// Stops every running source, for when the app exits. onTurnedOff is not called. Every source is stopped and the
    /// active set is cleared, even when a Stop throws.
    public void StopAll()
    {
        foreach (var id in _running.ToList())
        {
            StopSource(id);
        }
        _active.Clear();
    }

    /// Starts a source. It counts as running only when Start returns; a Start that throws is logged and left out.
    private void StartSource(string id)
    {
        try
        {
            _sources[id].Start();
            _running.Add(id);
        }
        catch (Exception e)
        {
            Fail("start", id, e);
        }
    }

    /// Stops a running source. It counts as stopped even when Stop throws: the id leaves Running first, and the error is logged.
    private void StopSource(string id)
    {
        if (!_running.Remove(id))
        {
            return;
        }
        try
        {
            _sources[id].Stop();
        }
        catch (Exception e)
        {
            Fail("stop", id, e);
        }
    }

    private void Fail(string what, string id, Exception e) =>
        _onError?.Invoke($"NotchTasks: the {id} module could not {what}", e);
}
