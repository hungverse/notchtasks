using NotchTasks.Core;

namespace NotchTasks.Core.Tests;

/// An in-memory IRegistry: tests never touch the real registry. A key exists once a value is set under it or under a
/// key below it, so SubKeyNames sees every ancestor's children.
public sealed class FakeRegistry : IRegistry
{
    private readonly Dictionary<(string Key, string Name), string> _values = new();
    private readonly Dictionary<(string Key, string Name), long> _longs = new();
    private readonly HashSet<string> _keys = new(StringComparer.Ordinal);

    public string? GetString(string keyPath, string name) =>
        _values.TryGetValue((keyPath, name), out var value) ? value : null;

    public long? GetLong(string keyPath, string name) =>
        _longs.TryGetValue((keyPath, name), out var value) ? value : null;

    public IReadOnlyList<string> SubKeyNames(string keyPath)
    {
        var prefix = keyPath + "\\";
        return _keys
            .Where(key => key.StartsWith(prefix, StringComparison.Ordinal) && key.IndexOf('\\', prefix.Length) < 0)
            .Select(key => key[prefix.Length..])
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
    }

    public void SetString(string keyPath, string name, string value)
    {
        Touch(keyPath);
        _values[(keyPath, name)] = value;
    }

    /// Sets a QWORD value, creating the key when it is missing.
    public void SetLong(string keyPath, string name, long value)
    {
        Touch(keyPath);
        _longs[(keyPath, name)] = value;
    }

    public void Delete(string keyPath, string name)
    {
        _values.Remove((keyPath, name));
        _longs.Remove((keyPath, name));
    }

    /// Adds the key and each key above it.
    private void Touch(string keyPath)
    {
        var path = keyPath;
        while (true)
        {
            _keys.Add(path);
            var cut = path.LastIndexOf('\\');
            if (cut < 0)
            {
                return;
            }
            path = path[..cut];
        }
    }
}
