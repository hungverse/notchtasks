using System.Diagnostics;
using NotchTasks.Services;
using Xunit;

namespace NotchTasks.AppTests;

// Each test uses its own event names, so a running NotchTasks on this PC never sees a signal from a test.
public sealed class SingleInstanceTests
{
    private static string UniqueName() => "NotchTasks-test-" + Guid.NewGuid().ToString("N");

    [Fact]
    public void SecondInstance_IsNotFirst_FirstIs()
    {
        var name = UniqueName();
        using var first = new SingleInstance(name);
        using var second = new SingleInstance(name);

        Assert.True(first.IsFirst);
        Assert.False(second.IsFirst);
    }

    [Fact]
    public void SignalQuit_ReachesListenQuit()
    {
        var name = UniqueName();
        using var first = new SingleInstance(name);
        using var second = new SingleInstance(name);
        using var quitRequested = new SemaphoreSlim(0);
        first.ListenQuit(() => quitRequested.Release());

        second.SignalQuit();

        Assert.True(quitRequested.Wait(TimeSpan.FromSeconds(5)), "the quit request did not reach the first instance");
    }

    [Fact]
    public void WaitForFirstExit_StillRunning_ReturnsAtTheTimeout()
    {
        var name = UniqueName();
        using var first = new SingleInstance(name);
        using var second = new SingleInstance(name);
        var watch = Stopwatch.StartNew();

        second.WaitForFirstExit(TimeSpan.FromMilliseconds(200));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), "the wait did not end at its timeout");
    }
}
