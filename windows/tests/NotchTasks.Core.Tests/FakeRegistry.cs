using NotchTasks.Core;

namespace NotchTasks.Core.Tests;

/// An in-memory IRegistry: tests never touch the real registry.
public sealed class FakeRegistry : IRegistry
{
    private readonly Dictionary<(string Key, string Name), string> _values = new();

    public string? GetString(string keyPath, string name) =>
        _values.TryGetValue((keyPath, name), out var value) ? value : null;

    public void SetString(string keyPath, string name, string value) => _values[(keyPath, name)] = value;

    public void Delete(string keyPath, string name) => _values.Remove((keyPath, name));
}
