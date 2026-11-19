using System.Text.Json.Nodes;
using NotchTasks.Core.Agents;
using NotchTasks.Core.Settings;
using Xunit;

namespace NotchTasks.Core.Tests;

// Ported from electron/src/core/settings.js watchItem and the items list (max 64).
public sealed class WatchItemsJsonTests : IDisposable
{
    private readonly string _dir;

    public WatchItemsJsonTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "nt-items-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A watcher still holding the folder; the OS temp cleaner takes it later.
        }
        catch (UnauthorizedAccessException)
        {
            // Same as above.
        }
    }

    private static JsonNode Parse(string json) => JsonNode.Parse(json)!;

    [Fact]
    public void Json_RoundTrip_KeepsUnknownFields()
    {
        var json = Parse("""
            [{"id":"goose-1","name":"Goose","kind":"process","process":"goose","threshold":4,
              "enabled":false,"futureField":{"x":1}}]
            """);

        var items = WatchItemsJson.Read(json);
        var item = Assert.Single(items);
        Assert.Equal("goose-1", item.Id);
        Assert.Equal(WatchKind.Process, item.Kind);
        Assert.Equal(4.0, item.Threshold);
        Assert.False(item.Enabled);
        Assert.True(item.Extra.ContainsKey("futureField"));

        var written = WatchItemsJson.Write(items);
        var again = Assert.Single(WatchItemsJson.Read(written));
        Assert.Equal(1, (int)again.Extra["futureField"]!["x"]!);
        Assert.Equal("Goose", (string?)written[0]!["name"]);
        Assert.Null(written[0]!["args"]?.GetValue<string?>());
    }

    [Fact]
    public void Json_Invalid_GivesDefaults_And_CapsAt64()
    {
        // Not a list: the defaults.
        var fromText = WatchItemsJson.Read(JsonValue.Create("nope"));
        Assert.Equal(new[] { "Claude", "Codex" }, fromText.Select(i => i.Name));

        // An item that fails validation is dropped, the rest are kept.
        var mixed = Parse("""
            [
              {"id":"ok-1","name":"Goose","kind":"process","process":"goose"},
              {"name":"no kind"},
              {"id":"bad-threshold","name":"Bad","kind":"app","threshold":"fast"},
              "not an object",
              {"id":"ok-2","name":"Crush","kind":"process","process":"crush"}
            ]
            """);
        Assert.Equal(new[] { "Goose", "Crush" }, WatchItemsJson.Read(mixed).Select(i => i.Name));

        // More than 64 items: the first 64 are kept.
        var many = new JsonArray();
        for (var i = 0; i < 70; i++)
        {
            many.Add(new JsonObject { ["id"] = $"item-{i}", ["name"] = $"n{i}", ["kind"] = "app" });
        }
        Assert.Equal(64, WatchItemsJson.Read(many).Count);
    }

    [Fact]
    public void Settings_ItemsDefault_IsClaudeAndCodex()
    {
        using var store = SettingsStore.Load(Path.Combine(_dir, "settings.json"));
        Assert.Equal(new[] { "Claude", "Codex" }, store.Items.Select(i => i.Name));
        Assert.Equal(new[] { WatchKind.Claude, WatchKind.Codex }, store.Items.Select(i => i.Kind));
        Assert.Equal(30, store.DoneAfter);
    }

    [Fact]
    public void Settings_SetItems_PersistsAndKeepsOtherKeys()
    {
        var file = Path.Combine(_dir, "settings.json");
        File.WriteAllText(file, """{"unknownKey":"kept","doneAfter":12}""");
        using var store = SettingsStore.Load(file);
        Assert.Equal(12, store.DoneAfter);

        store.SetItems(new[] { new WatchItem("g", "Goose", WatchKind.Process, Process: "goose") });

        using var reopened = SettingsStore.Load(file);
        Assert.Equal(new[] { "Goose" }, reopened.Items.Select(i => i.Name));
        var text = File.ReadAllText(file);
        Assert.Contains("unknownKey", text);
    }
}
