using System.Windows.Threading;
using NotchTasks.Core.Modules;
using NotchTasks.Core.Notch;
using NotchTasks.Services.Modules;
using Xunit;

namespace NotchTasks.AppTests;

public sealed class BatterySourceTests
{
    /// An announcer that keeps the toasts it is given.
    private sealed class KeepAnnouncer : IAnnouncer
    {
        public List<Toast> Toasts { get; } = new();

        public void Announce(Toast t, bool notify = true) => Toasts.Add(t);
    }

    [Fact]
    public void BatterySource_TimerRunsOnlyWithABatteryWhileStarted()
    {
        Sta.Run(() =>
        {
            var reading = new PowerFacts(false, 0, false, false);
            var reads = 0;
            var module = new BatteryModule(new KeepAnnouncer());
            var source = new BatterySource(module, () => { }, () => { reads++; return reading; });
            try
            {
                var timer = Fixture.Field<DispatcherTimer>(source, "_timer");

                // No battery: Start reads once and runs no timer at all.
                source.Start();
                Assert.Equal(1, reads);
                Assert.Null(module.State);
                Assert.False(timer.IsEnabled);

                // Stopped, Refresh reads nothing.
                source.Stop();
                source.Refresh();
                Assert.Equal(1, reads);

                // A battery: the 30 s timer runs while started, and stops with Stop, which clears the module.
                reading = new PowerFacts(true, 70, false, false);
                source.Start();
                Assert.True(timer.IsEnabled);
                Assert.Equal(TimeSpan.FromSeconds(30), timer.Interval);
                Assert.Equal(new BatteryState(70, false, false), module.State);

                source.Stop();
                Assert.False(timer.IsEnabled);
                Assert.Null(module.State);
            }
            finally
            {
                source.Stop();
            }
        });
    }

    [Fact]
    public void BatterySource_RefreshAnnouncesAPlugChangeAndPublishes()
    {
        Sta.Run(() =>
        {
            var reading = new PowerFacts(true, 60, false, false);
            var announcer = new KeepAnnouncer();
            var published = 0;
            var module = new BatteryModule(announcer);
            var source = new BatterySource(module, () => published++, () => reading);
            try
            {
                source.Start();
                Assert.Equal(1, published);  // the baseline reading is a state change, so it publishes once
                published = 0;

                source.Refresh();  // no change: nothing published
                Assert.Equal(0, published);

                reading = new PowerFacts(true, 60, true, true);
                source.Refresh();

                Assert.Equal(1, published);
                var toast = Assert.Single(announcer.Toasts);
                Assert.Equal("Charging", toast.Title);
            }
            finally
            {
                source.Stop();
            }
        });
    }
}
