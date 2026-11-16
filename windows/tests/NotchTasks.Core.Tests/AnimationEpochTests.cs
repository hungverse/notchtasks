using NotchTasks.Core;
using Xunit;

namespace NotchTasks.Core.Tests;

public sealed class AnimationEpochTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nt-epoch-" + Guid.NewGuid().ToString("N"));

    private string StatePath => Path.Combine(_dir, "state.json");

    public AnimationEpochTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Read_MissingFileOrKey_IsZero()
    {
        Assert.Equal(0, AnimationEpoch.Read(StatePath));

        File.WriteAllText(StatePath, """{"lastPane": "badges"}""");
        Assert.Equal(0, AnimationEpoch.Read(StatePath));
    }

    [Fact]
    public void Write_ThenRead_KeepsTheMomentAndOtherKeys()
    {
        File.WriteAllText(StatePath, """{"lastPane": "badges"}""");

        AnimationEpoch.Write(StatePath, 1_763_000_000_123);

        Assert.Equal(1_763_000_000_123, AnimationEpoch.Read(StatePath));
        Assert.Equal("badges", StateJson.Get(StatePath, "lastPane")!.GetValue<string>());
    }
}
