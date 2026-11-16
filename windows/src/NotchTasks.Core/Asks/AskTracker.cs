namespace NotchTasks.Core.Asks;

/// Keeps asks the user has answered out of the watcher's lists, until the watcher no longer lists them. A watcher
/// snapshot taken before an answer can reach the UI after it; without this the answered ask would come back.
public sealed class AskTracker
{
    private readonly HashSet<string> _answered = new(StringComparer.Ordinal);

    /// The number of answered asks still being tracked.
    public int Count => _answered.Count;

    /// Records that the ask was answered (its reply is already written).
    public void MarkAnswered(string id) => _answered.Add(id);

    /// The list without the answered asks. An answered id that the list no longer names is forgotten.
    public IReadOnlyList<AgentAsk> Filter(IReadOnlyList<AgentAsk> list)
    {
        var listed = new HashSet<string>(list.Select(a => a.Id), StringComparer.Ordinal);
        _answered.RemoveWhere(id => !listed.Contains(id));
        return list.Where(a => !_answered.Contains(a.Id)).ToList();
    }
}
