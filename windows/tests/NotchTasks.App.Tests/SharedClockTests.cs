using System.Windows.Threading;
using NotchTasks.Core.Modules;
using NotchTasks.Core.Notch;
using NotchTasks.Services.Modules;
using Xunit;

namespace NotchTasks.AppTests;

/// The timer and the calls share one 1 s ticker: it runs only while either is live, and each tick publishes once for both.
public sealed class SharedClockTests
{
    private sealed class DropAnnouncer : IAnnouncer
    {
        public void Announce(Toast t, bool notify = true)
        {
        }
    }

    [Fact]
    public void TimerAndCallLive_ShareOneTicker_AndOnePublishPerTick()
    {
        Sta.Run(() =>
        {
            var published = 0;
            var clock = new SharedClock(() => published++);
            var timerModule = new TimerModule(new DropAnnouncer());
            var timerSource = new TimerSource(timerModule, clock);
            var sensors = new SensorsModule(new DropAnnouncer(), () => true, () => true);
            Action? captured = null;
            var reading = new SensorState(new[] { "zoom.exe" }, false, false);
            var sensorsSource = new SensorsSource(sensors, () => { }, clock, new SensorReaders(
                () => reading,
                onChange =>
                {
                    captured = onChange;
                    return new Fixture.NoopWatch();
                },
                _ => false,
                () => false));
            try
            {
                var ticker = Fixture.Field<DispatcherTimer>(clock, "_timer");
                timerSource.Start();
                sensorsSource.Start();
                captured!();  // the baseline: a mic app is recording, so a call starts
                Fixture.Flush();
                Assert.NotNull(sensors.Call);
                Assert.True(ticker.IsEnabled);  // a call alone is live: the clock runs for it

                timerModule.Start(5, DateTimeOffset.Now);  // now the timer is live too: still one ticker
                Fixture.Call(clock, "OnTick");
                Assert.Equal(1, published);  // one publish for the timer and the call together
                Fixture.Call(clock, "OnTick");
                Assert.Equal(2, published);
            }
            finally
            {
                timerSource.Stop();
                sensorsSource.Stop();
            }
        });
    }

    [Fact]
    public void Nothing_Live_NoTicker_NoPublish()
    {
        Sta.Run(() =>
        {
            var published = 0;
            var clock = new SharedClock(() => published++);
            var timerSource = new TimerSource(new TimerModule(new DropAnnouncer()), clock);
            var ticker = Fixture.Field<DispatcherTimer>(clock, "_timer");

            timerSource.Start();  // started, but no timer runs: nothing to tick
            Assert.False(ticker.IsEnabled);
            Fixture.Call(clock, "OnTick");
            Assert.Equal(0, published);
            timerSource.Stop();
        });
    }
}
