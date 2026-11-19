using NotchTasks.Core.Modules;
using NotchTasks.Core.Notch;
using Xunit;

namespace NotchTasks.Core.Tests;

public class ModuleHostTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 19, 9, 0, 0, TimeSpan.Zero);

    /// A source that counts its Start and Stop calls.
    private sealed class FakeSource : IModuleSource
    {
        public int Starts { get; private set; }
        public int Stops { get; private set; }

        public void Start() => Starts++;

        public void Stop() => Stops++;
    }

    private static ModuleHost NewHost(
        IReadOnlyDictionary<string, IModuleSource> sources, Action<string>? onTurnedOff = null) =>
        new(sources, onTurnedOff ?? (_ => { }));

    [Fact]
    public void Apply_StartsNewlyActive_StopsNewlyInactive_Once()
    {
        var calls = new FakeSource();
        var network = new FakeSource();
        var sources = new Dictionary<string, IModuleSource> { ["calls"] = calls, ["network"] = network };
        var turnedOff = new List<string>();
        var host = NewHost(sources, turnedOff.Add);

        host.Apply(["calls"]);
        Assert.Equal(1, calls.Starts);
        Assert.Equal(0, network.Starts);
        Assert.Empty(turnedOff);

        host.Apply(["network"]);
        Assert.Equal(1, calls.Stops);
        Assert.Equal(1, network.Starts);
        Assert.Equal(0, network.Stops);
        Assert.Equal(new[] { "calls" }, turnedOff.ToArray());
        Assert.Equal(new[] { "network" }, host.Running.ToArray());
    }

    [Fact]
    public void Apply_SameSetTwice_DoesNothing()
    {
        var calls = new FakeSource();
        var turnedOff = new List<string>();
        var host = NewHost(new Dictionary<string, IModuleSource> { ["calls"] = calls }, turnedOff.Add);

        host.Apply(["calls"]);
        host.Apply(["calls"]);

        Assert.Equal(1, calls.Starts);
        Assert.Equal(0, calls.Stops);
        Assert.Empty(turnedOff);
    }

    [Fact]
    public void ModuleHost_Off_StopsSourceAndDropsItsToasts()
    {
        var network = new FakeSource();
        var queue = new ToastQueue();
        var host = NewHost(
            new Dictionary<string, IModuleSource> { ["network"] = network },
            id => queue.DropModule(id, T0));

        host.Apply(["network"]);
        queue.Enqueue(new Toast("✓", "#34C759", "Offline", Module: "network"), T0);
        queue.Enqueue(new Toast("✓", "#34C759", "Battery", Module: "battery"), T0);

        host.Apply([]);

        Assert.Equal(1, network.Stops);
        Assert.Equal("Battery", queue.Current?.Title);
        queue.Dismiss(T0);
        Assert.Null(queue.Current);
    }

    [Fact]
    public void StopAll_StopsEveryRunningSource()
    {
        var a = new FakeSource();
        var b = new FakeSource();
        var host = NewHost(new Dictionary<string, IModuleSource> { ["a"] = a, ["b"] = b });
        host.Apply(["a", "b"]);

        host.StopAll();

        Assert.Equal(1, a.Stops);
        Assert.Equal(1, b.Stops);
        Assert.Empty(host.Running);
    }

    /// A source whose Start or Stop throws.
    private sealed class ThrowingSource : IModuleSource
    {
        private readonly bool _startThrows;
        private readonly bool _stopThrows;

        public ThrowingSource(bool startThrows, bool stopThrows)
        {
            _startThrows = startThrows;
            _stopThrows = stopThrows;
        }

        public int Starts { get; private set; }
        public int Stops { get; private set; }

        public void Start()
        {
            Starts++;
            if (_startThrows)
            {
                throw new InvalidOperationException("start failed");
            }
        }

        public void Stop()
        {
            Stops++;
            if (_stopThrows)
            {
                throw new InvalidOperationException("stop failed");
            }
        }
    }

    [Fact]
    public void Apply_StartThrows_NotRunning_OthersStillStart()
    {
        var bad = new ThrowingSource(startThrows: true, stopThrows: false);
        var good = new FakeSource();
        var errors = new List<string>();
        var host = new ModuleHost(
            new Dictionary<string, IModuleSource> { ["bad"] = bad, ["good"] = good },
            _ => { },
            (message, _) => errors.Add(message));

        host.Apply(["bad", "good"]);

        Assert.Equal(1, bad.Starts);
        Assert.DoesNotContain("bad", host.Running);
        Assert.Equal(1, good.Starts);
        Assert.Equal(new[] { "good" }, host.Running.ToArray());
        Assert.Single(errors);
    }

    [Fact]
    public void StopAll_StopThrows_OthersStillStopped()
    {
        var bad = new ThrowingSource(startThrows: false, stopThrows: true);
        var good = new FakeSource();
        var errors = new List<string>();
        var host = new ModuleHost(
            new Dictionary<string, IModuleSource> { ["bad"] = bad, ["good"] = good },
            _ => { },
            (message, _) => errors.Add(message));
        host.Apply(["bad", "good"]);

        host.StopAll();

        Assert.Equal(1, bad.Stops);
        Assert.Equal(1, good.Stops);
        Assert.Empty(host.Running);
        Assert.Single(errors);
    }

    [Fact]
    public void Apply_StopThrows_StillCountsAsStopped_AndTurnedOff()
    {
        var bad = new ThrowingSource(startThrows: false, stopThrows: true);
        var good = new FakeSource();
        var turnedOff = new List<string>();
        var host = new ModuleHost(
            new Dictionary<string, IModuleSource> { ["bad"] = bad, ["good"] = good },
            turnedOff.Add,
            (_, _) => { });
        host.Apply(["bad", "good"]);

        host.Apply([]);

        Assert.Equal(1, bad.Stops);
        Assert.Equal(1, good.Stops);
        Assert.Empty(host.Running);
        Assert.Equal(new[] { "bad", "good" }.OrderBy(x => x).ToArray(), turnedOff.OrderBy(x => x).ToArray());
    }
}
