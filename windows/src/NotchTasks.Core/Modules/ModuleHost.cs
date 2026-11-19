namespace NotchTasks.Core.Modules;

/// Starts and stops the module sources as their modules turn on and off (Settings modules and disabled). A source runs
/// only while its module is on. A module that is turned off has its source stopped, then onTurnedOff is called with its
/// id, so the app can drop the module's toasts. Pure logic: the app calls Apply on the UI thread whenever the active
/// modules change.
public sealed class ModuleHost
{
    private readonly IReadOnlyDictionary<string, IModuleSource> _sources;
    private readonly Action<string> _onTurnedOff;
    private readonly HashSet<string> _active = new(StringComparer.Ordinal);
    private readonly HashSet<string> _running = new(StringComparer.Ordinal);

    /// The sources by module id. A module without a source is active but has nothing to run.
    public ModuleHost(IReadOnlyDictionary<string, IModuleSource> sources, Action<string> onTurnedOff)
    {
        _sources = sources;
        _onTurnedOff = onTurnedOff;
    }

    /// The ids of the modules whose sources are running.
    public IReadOnlySet<string> Running => _running;

    /// Makes `active` the set of active modules. Each module turned off stops its source (if any) and calls onTurnedOff;
    /// each module newly active starts its source. An unchanged set does nothing.
    public void Apply(IReadOnlyCollection<string> active)
    {
        var wanted = new HashSet<string>(active, StringComparer.Ordinal);
        foreach (var id in _active.Where(id => !wanted.Contains(id)).ToList())
        {
            _active.Remove(id);
            if (_running.Remove(id))
            {
                _sources[id].Stop();
            }
            _onTurnedOff(id);
        }
        foreach (var id in wanted)
        {
            _active.Add(id);
            if (_sources.TryGetValue(id, out var source) && _running.Add(id))
            {
                source.Start();
            }
        }
    }

    /// Stops every running source, for when the app exits. onTurnedOff is not called.
    public void StopAll()
    {
        foreach (var id in _running.ToList())
        {
            _running.Remove(id);
            _sources[id].Stop();
        }
        _active.Clear();
    }
}
