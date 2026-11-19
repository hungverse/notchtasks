using System.Text.Json.Nodes;
using System.Windows.Threading;
using NotchTasks.Core.Modules;
using NotchTasks.Services.Modules;
using Xunit;

namespace NotchTasks.AppTests;

public sealed class HealthSourceTests
{
    [Fact]
    public void LoadTimer_RunsOnlyWhilePillIsOpen()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            var source = Fixture.Field<HealthSource>(state, "_healthSource");
            var loadTimer = Fixture.Field<DispatcherTimer>(source, "_loadTimer");

            // The health module is on by default, so its source is started; the load still waits for the open pill.
            Assert.Contains("health", AppState.ActiveModules(state.Settings));
            Assert.False(loadTimer.IsEnabled);

            state.PillOpened(true);
            Assert.True(loadTimer.IsEnabled);
            Assert.Equal(TimeSpan.FromSeconds(2), loadTimer.Interval);

            state.PillOpened(false);
            Assert.False(loadTimer.IsEnabled);
            Assert.Null(state.Health.Load);
        });
    }

    [Fact]
    public void LoadTimer_StaysStopped_WhileSystemLoadIsOff()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            state.Settings.Set("systemLoad", JsonValue.Create(false));
            var source = Fixture.Field<HealthSource>(state, "_healthSource");
            var loadTimer = Fixture.Field<DispatcherTimer>(source, "_loadTimer");

            state.PillOpened(true);

            Assert.False(loadTimer.IsEnabled);
        });
    }
}
