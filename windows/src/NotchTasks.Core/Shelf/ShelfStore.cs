using System.Text.Json.Nodes;

namespace NotchTasks.Core.Shelf;

/// The file shelf: the files and folders dropped on the pill, kept in state.json under "shelf" (a list of absolute paths, in
/// order, unique ignoring case). A missing file stays listed (the card dims it), as on the Mac. The list is replaced, not edited,
/// on each change, so a list a caller holds never changes under it. Changes are made on the UI thread.
public sealed class ShelfStore
{
    /// The state key the shelf is saved under.
    public const string Key = "shelf";

    private readonly Func<string, JsonNode?> _stateGet;
    private readonly Action<string, JsonNode> _stateSet;
    private readonly Func<string, bool> _exists;
    private IReadOnlyList<string> _items;

    /// Loads the shelf from the state: a non-string or relative entry is dropped, and a repeated path (ignoring case) keeps its first place.
    public ShelfStore(Func<string, JsonNode?> stateGet, Action<string, JsonNode> stateSet, Func<string, bool> exists)
    {
        _stateGet = stateGet;
        _stateSet = stateSet;
        _exists = exists;
        _items = Load(stateGet(Key));
    }

    /// Raised after the list changes (an add, a remove or a clear that changed something).
    public event Action? Changed;

    /// The shelf's paths, in order.
    public IReadOnlyList<string> Items => _items;

    /// Appends the absolute paths that are not yet on the shelf (ignoring case), in the order given, and saves. Relative and
    /// repeated paths are skipped. Returns whether anything was added.
    public bool Add(IEnumerable<string> paths)
    {
        var list = _items.ToList();
        var added = false;
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || Contains(list, path))
            {
                continue;
            }
            list.Add(path);
            added = true;
        }
        if (!added)
        {
            return false;
        }
        Replace(list);
        return true;
    }

    /// Removes the given paths (ignoring case) and saves when something was on the shelf to remove.
    public void Remove(IEnumerable<string> paths)
    {
        var gone = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        var list = _items.Where(p => !gone.Contains(p)).ToList();
        if (list.Count != _items.Count)
        {
            Replace(list);
        }
    }

    /// Empties the shelf and saves.
    public void Clear()
    {
        if (_items.Count > 0)
        {
            Replace(new List<string>());
        }
    }

    /// Whether the path names a file or folder that exists now.
    public bool Exists(string path) => _exists(path);

    private void Replace(List<string> list)
    {
        _stateSet(Key, new JsonArray(list.Select(p => (JsonNode)JsonValue.Create(p)!).ToArray()));
        _items = list;
        Changed?.Invoke();
    }

    private static IReadOnlyList<string> Load(JsonNode? node)
    {
        var list = new List<string>();
        if (node is not JsonArray array)
        {
            return list;
        }
        foreach (var entry in array)
        {
            if (entry is JsonValue value && value.TryGetValue(out string? path) && Path.IsPathFullyQualified(path)
                && !Contains(list, path))
            {
                list.Add(path);
            }
        }
        return list;
    }

    private static bool Contains(List<string> list, string path) =>
        list.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
}
