using System.Windows.Threading;
using NotchTasks.Core.Modules;
using NotchTasks.Core.Notch;
using NotchTasks.Services.Modules;
using Xunit;

namespace NotchTasks.AppTests;

public sealed class TimerSourceTests
{
    /// An announcer that drops the toasts: these tests look at the timer, not the queue.
    private sealed class DropAnnouncer : IAnnouncer
    {
        public void Announce(Toast t, bool notify = true)
        {
        }
    }

    [Fact]
    public void TimerSource_TimerRunsOnlyWhileStartedAndATimerRuns()
    {
        Sta.Run(() =>
        {
            var module = new TimerModule(new DropAnnouncer());
            var source = new TimerSource(module, () => { });
            try
            {
                var timer = Fixture.Field<DispatcherTimer>(source, "_timer");

                source.Start();
                Assert.False(timer.IsEnabled);

                module.Start(5, DateTimeOffset.Now);
                Assert.True(timer.IsEnabled);

                source.Stop();
                Assert.False(timer.IsEnabled);
                Assert.Null(module.State);

                // Stopped, the source no longer follows the module: a new timer does not start the clock.
                module.Start(5, DateTimeOffset.Now);
                Assert.False(timer.IsEnabled);
            }
            finally
            {
                source.Stop();
            }
        });
    }
}
