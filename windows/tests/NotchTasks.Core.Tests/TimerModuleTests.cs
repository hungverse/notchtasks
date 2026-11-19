using NotchTasks.Core.Modules;
using NotchTasks.Core.Notch;
using Xunit;

namespace NotchTasks.Core.Tests;

public class TimerModuleTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 19, 9, 0, 0, TimeSpan.Zero);

    /// An announcer that keeps the toasts it is given.
    private sealed class FakeAnnouncer : IAnnouncer
    {
        public List<Toast> Toasts { get; } = new();

        public void Announce(Toast t, bool notify = true) => Toasts.Add(t);
    }

    [Fact]
    public void Start_SetsEndAndTotal()
    {
        var module = new TimerModule(new FakeAnnouncer());

        module.Start(25, T0);

        Assert.Equal(new TimerState(T0.AddMinutes(25), TimeSpan.FromMinutes(25)), module.State);
    }

    [Fact]
    public void Extend_AddsToEndAndTotal_StartsWhenIdle()
    {
        var module = new TimerModule(new FakeAnnouncer());

        module.Extend(5, T0);
        Assert.Equal(new TimerState(T0.AddMinutes(5), TimeSpan.FromMinutes(5)), module.State);

        module.Extend(1, T0.AddMinutes(1));
        Assert.Equal(new TimerState(T0.AddMinutes(6), TimeSpan.FromMinutes(6)), module.State);
    }

    [Fact]
    public void Tick_AtEnd_AnnouncesTimesUp()
    {
        var announcer = new FakeAnnouncer();
        var module = new TimerModule(announcer);
        module.Start(25, T0);

        Assert.False(module.Tick(T0.AddMinutes(25).AddSeconds(-1)));
        Assert.NotNull(module.State);
        Assert.Empty(announcer.Toasts);

        Assert.True(module.Tick(T0.AddMinutes(25)));
        Assert.Null(module.State);
        var toast = Assert.Single(announcer.Toasts);
        Assert.Equal("timer", toast.Glyph);
        Assert.Equal("#FF9F0A", toast.Tint);
        Assert.Equal(L10n.L("Time's up"), toast.Title);
        Assert.Equal(L10n.L("{0} min", 25), toast.Subtitle);
        Assert.Equal("timerDone", toast.Event);
        Assert.Equal("timer", toast.Module);
    }

    [Fact]
    public void Stop_ClearsState_NoToast()
    {
        var announcer = new FakeAnnouncer();
        var module = new TimerModule(announcer);
        var changes = 0;
        module.Changed += () => changes++;
        module.Start(10, T0);

        module.Stop();

        Assert.Null(module.State);
        Assert.Equal(2, changes);
        Assert.False(module.Tick(T0.AddMinutes(10)));
        Assert.Empty(announcer.Toasts);
    }
}
