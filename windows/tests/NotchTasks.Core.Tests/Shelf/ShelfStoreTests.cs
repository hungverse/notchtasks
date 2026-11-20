using System.Text.Json.Nodes;
using NotchTasks.Core.Shelf;
using Xunit;

namespace NotchTasks.Core.Tests.Shelf;

/// The file shelf's list: what is loaded from state.json, how paths are added and removed, and what is saved. The state is an
/// in-memory dictionary standing for StateJson, and the files that exist are a set, so no disk is touched.
public class ShelfStoreTests
{
    private static readonly string A = Path.Combine(Path.GetTempPath(), "shelf-a.txt");
    private static readonly string B = Path.Combine(Path.GetTempPath(), "shelf-b.txt");
    private static readonly string Missing = Path.Combine(Path.GetTempPath(), "shelf-missing.txt");

    /// The state file: key to value, copied on read and write like the real file.
    private sealed class State
    {
        public Dictionary<string, JsonNode?> Values { get; } = new(StringComparer.Ordinal);

        public JsonNode? Get(string key) => Values.TryGetValue(key, out var v) ? v?.DeepClone() : null;

        public void Set(string key, JsonNode value) => Values[key] = value.DeepClone();
    }

    private static ShelfStore Store(State state, IEnumerable<string>? exists = null)
    {
        var files = new HashSet<string>(exists ?? new[] { A, B }, StringComparer.OrdinalIgnoreCase);
        return new ShelfStore(state.Get, state.Set, p => files.Contains(p));
    }

    [Fact]
    public void Load_DropsBadEntries_KeepsMissing()
    {
        var state = new State();
        state.Values["shelf"] = JsonNode.Parse($"[\"{Escape(A)}\", 5, \"relative.txt\", null, \"{Escape(Missing)}\", \"{Escape(A.ToUpperInvariant())}\"]");

        var store = Store(state);

        Assert.Equal(new[] { A, Missing }, store.Items);
        Assert.False(store.Exists(Missing));
        Assert.True(store.Exists(A));
    }

    [Fact]
    public void Load_NoKey_IsEmpty()
    {
        var store = Store(new State());

        Assert.Empty(store.Items);
    }

    [Fact]
    public void Add_UniqueIgnoreCase_AppendsAndSaves()
    {
        var state = new State();
        var store = Store(state);
        var changes = 0;
        store.Changed += () => changes++;

        var added = store.Add(new[] { A, A.ToUpperInvariant(), B });

        Assert.True(added);
        Assert.Equal(new[] { A, B }, store.Items);
        Assert.Equal(1, changes);
        Assert.Equal(new[] { A, B }, Saved(state));
    }

    [Fact]
    public void Add_OnlyDuplicates_ReturnsFalseAndSavesNothing()
    {
        var state = new State();
        var store = Store(state);
        store.Add(new[] { A });
        var changes = 0;
        store.Changed += () => changes++;

        var added = store.Add(new[] { A.ToUpperInvariant() });

        Assert.False(added);
        Assert.Equal(new[] { A }, store.Items);
        Assert.Equal(0, changes);
    }

    [Fact]
    public void Add_RelativePaths_AreIgnored()
    {
        var store = Store(new State());

        Assert.False(store.Add(new[] { "relative\\file.txt" }));
        Assert.Empty(store.Items);
    }

    [Fact]
    public void Remove_Saves()
    {
        var state = new State();
        var store = Store(state);
        store.Add(new[] { A, B });

        store.Remove(new[] { A });

        Assert.Equal(new[] { B }, store.Items);
        Assert.Equal(new[] { B }, Saved(state));
    }

    [Fact]
    public void Clear_EmptiesAndSaves()
    {
        var state = new State();
        var store = Store(state);
        store.Add(new[] { A, B });

        store.Clear();

        Assert.Empty(store.Items);
        Assert.Empty(Saved(state));
    }

    /// The shelf as it was saved to state.json, read back as the list of paths.
    private static string[] Saved(State state) =>
        ((JsonArray)state.Values["shelf"]!).Select(n => n!.GetValue<string>()).ToArray();

    private static string Escape(string path) => path.Replace("\\", "\\\\", StringComparison.Ordinal);
}
