using System.Text.Json.Nodes;
using NotchTasks.Core;
using Xunit;

namespace NotchTasks.Core.Tests;

public sealed class StateJsonTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nt-state-" + Guid.NewGuid().ToString("N"));

    private string StatePath => Path.Combine(_dir, "state.json");

    public StateJsonTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Set_KeepsKeysThisBuildDoesNotKnow()
    {
        File.WriteAllText(StatePath, """{"animationEpoch": 7}""");

        StateJson.Set(StatePath, "lastPane", JsonValue.Create("watching"));

        Assert.Equal(7, StateJson.Get(StatePath, "animationEpoch")!.GetValue<int>());
        Assert.Equal("watching", StateJson.Get(StatePath, "lastPane")!.GetValue<string>());
    }

    [Fact]
    public void Get_MissingFileOrKey_IsNull()
    {
        Assert.Null(StateJson.Get(StatePath, "lastPane"));

        File.WriteAllText(StatePath, "{}");
        Assert.Null(StateJson.Get(StatePath, "lastPane"));
    }

    [Fact]
    public async Task Set_FromTwoThreads_KeepsBothKeys()
    {
        // Each Set is a read-modify-write of the whole file; without one lock a write from the other thread is lost.
        var first = Task.Run(() =>
        {
            for (var i = 0; i < 200; i++)
            {
                StateJson.Set(StatePath, "alpha", JsonValue.Create(i));
            }
        });
        var second = Task.Run(() =>
        {
            for (var i = 0; i < 200; i++)
            {
                StateJson.Set(StatePath, "beta", JsonValue.Create(i));
            }
        });
        await Task.WhenAll(first, second);

        Assert.Equal(199, StateJson.Get(StatePath, "alpha")!.GetValue<int>());
        Assert.Equal(199, StateJson.Get(StatePath, "beta")!.GetValue<int>());
    }

    [Fact]
    public void Set_CorruptFileIsMovedAside()
    {
        File.WriteAllText(StatePath, "{ not json");

        StateJson.Set(StatePath, "lastPane", JsonValue.Create("modules"));

        Assert.True(File.Exists(StatePath + ".corrupt"));
        Assert.Equal("modules", StateJson.Get(StatePath, "lastPane")!.GetValue<string>());
    }
}
