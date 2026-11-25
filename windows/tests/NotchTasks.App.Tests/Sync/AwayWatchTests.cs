using NotchTasks.Services.Sync;
using Xunit;

namespace NotchTasks.AppTests;

/// Away is a locked session or 120 s without input. The idle reading is a fake here; the lock comes from the session event.
public sealed class AwayWatchTests
{
    [Fact]
    public void AwayWatch_IdleOver120_IsAway()
    {
        using var watch = new AwayWatch(() => TimeSpan.FromSeconds(121));

        Assert.True(watch.IsAway());
    }

    [Fact]
    public void AwayWatch_IdleUnder120_IsNotAway()
    {
        using var watch = new AwayWatch(() => TimeSpan.FromSeconds(119));

        Assert.False(watch.IsAway());
    }

    [Fact]
    public void AwayWatch_Locked_IsAway()
    {
        using var watch = new AwayWatch(() => TimeSpan.Zero);
        Assert.False(watch.IsAway());

        watch.SetLocked(true);
        Assert.True(watch.IsAway());

        watch.SetLocked(false);
        Assert.False(watch.IsAway());
    }
}
