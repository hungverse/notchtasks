using System.Diagnostics;
using System.Windows.Threading;
using NotchTasks.Core;
using NotchTasks.Core.Asks;
using NotchTasks.Core.Modules;
using NotchTasks.Core.Settings;
using NotchTasks.Services;
using NotchTasks.Services.Modules;
using Xunit;

namespace NotchTasks.AppTests;

/// The heat feed: while the health module runs and the temperature helper is On, the hottest reading is asked every 30 s, with the
/// pill closed too, and judged by the heat toast. Nothing is asked while the helper is off. The helper is built over fakes: no UAC,
/// no pipe and no stop event.
public sealed class HeatSourceTests
{
    private const string Sid = "S-1-5-21-1111111111-2222222222-3333333333-1001";

    private sealed class FakePipe : ISensorPipe
    {
        public int Calls;
        public IReadOnlyList<ThermalReading>? Answer;

        public Task<IReadOnlyList<ThermalReading>?> QueryAsync(CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(Answer);
        }
    }

    private sealed class NoLaunch : IProcessLauncher
    {
        public Task<Process?> RunAsAsync(string fileName, string arguments) => Task.FromResult<Process?>(null);
    }

    private sealed class NoStop : IHelperStop
    {
        public bool Signal() => false;
    }

    /// The app state over a temp settings file, with the helper over `pipe`. The helper starts Off (the settings are off).
    private static AppState NewState(FakePipe pipe, out SensorHelper helper)
    {
        var settings = SettingsStore.Load(AppPaths.SettingsFile);
        helper = new SensorHelper(settings, new NoLaunch(), pipe, new NoStop(), "NotchTasksSensors.exe", Sid, 1, _ => false);
        return new AppState(settings, new AskStore(AppPaths.AsksDir), Fixture.Inert(settings) with { Helper = helper });
    }

    [Fact]
    public void Timer_IsOff_WhileTheHelperIsOff()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = NewState(new FakePipe(), out _);
            var source = Fixture.Field<HeatSource>(state, "_heatSource");
            var timer = Fixture.Field<DispatcherTimer>(source, "_timer");

            // The health module is on by default, so its source is started; the heat timer waits for an On helper.
            Assert.Contains("health", AppState.ActiveModules(state.Settings));
            Assert.False(timer.IsEnabled);
        });
    }

    [Fact]
    public void Timer_RunsEvery30s_WhileTheHelperIsOn_AndStopsWhenItIsOff()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = NewState(new FakePipe(), out var helper);
            var source = Fixture.Field<HeatSource>(state, "_heatSource");
            var timer = Fixture.Field<DispatcherTimer>(source, "_timer");

            Fixture.Call(helper, "SetState", HelperState.On);
            Assert.True(timer.IsEnabled);
            Assert.Equal(TimeSpan.FromSeconds(30), timer.Interval);

            Fixture.Call(helper, "SetState", HelperState.Off);
            Assert.False(timer.IsEnabled);
        });
    }

    [Fact]
    public void WhileOff_NothingIsAsked_EvenWhenTheHeatIsHigh()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var pipe = new FakePipe { Answer = new[] { new ThermalReading("CPU", 99) } };
            using var state = NewState(pipe, out _);
            var source = Fixture.Field<HeatSource>(state, "_heatSource");

            Fixture.Call(source, "AskAsync");

            Assert.Equal(0, pipe.Calls);
            Assert.Null(state.Toasts.Current);
        });
    }

    [Fact]
    public void OnWithPillClosed_AHotReading_RaisesTheWarmToast()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var pipe = new FakePipe { Answer = new[] { new ThermalReading("CPU", 90) } };
            using var state = NewState(pipe, out var helper);

            Fixture.Call(helper, "SetState", HelperState.On);  // asks at once, the pill is closed

            Assert.True(pipe.Calls >= 1);
            var toast = state.Toasts.Current;
            Assert.NotNull(toast);
            Assert.Equal("Computer is warm", toast!.Title);
            Assert.Equal("warning", toast.Event);
            Assert.True(toast.ToPhone);
        });
    }

    [Fact]
    public void OnWithAColdReading_RaisesNothing()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var pipe = new FakePipe { Answer = new[] { new ThermalReading("CPU", 60) } };
            using var state = NewState(pipe, out var helper);

            Fixture.Call(helper, "SetState", HelperState.On);

            Assert.True(pipe.Calls >= 1);
            Assert.Null(state.Toasts.Current);
        });
    }

    [Fact]
    public void CriticalReading_RaisesTheVeryHotToast()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var pipe = new FakePipe { Answer = new[] { new ThermalReading("CPU", 97), new ThermalReading("TZ00", 40) } };
            using var state = NewState(pipe, out var helper);

            Fixture.Call(helper, "SetState", HelperState.On);

            Assert.Equal("Computer is very hot", state.Toasts.Current?.Title);
            Assert.Equal("#FF453A", state.Toasts.Current?.Tint);
        });
    }

    [Fact]
    public void Stop_CancelsTheTimer_AndAHeatAgainAfterRestartDoesNotWarnTwice()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var pipe = new FakePipe { Answer = new[] { new ThermalReading("CPU", 90) } };
            using var state = NewState(pipe, out var helper);
            var source = Fixture.Field<HeatSource>(state, "_heatSource");
            var timer = Fixture.Field<DispatcherTimer>(source, "_timer");

            Fixture.Call(helper, "SetState", HelperState.On);
            var first = state.Toasts.Current;
            Assert.NotNull(first);

            source.Stop();
            Assert.False(timer.IsEnabled);

            // The module restarts with the same heat: the crossing is still the one that warned, so no second toast.
            Fixture.Call(helper, "SetState", HelperState.Off);
            source.Start();
            Fixture.Call(helper, "SetState", HelperState.On);
            Assert.True(pipe.Calls >= 2);
            Assert.Same(first, state.Toasts.Current);
            source.Stop();
        });
    }
}
