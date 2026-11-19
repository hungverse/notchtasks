using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NotchTasks.Core;
using NotchTasks.Core.Asks;
using NotchTasks.Core.Modules;
using NotchTasks.Core.Settings;
using NotchTasks.Pill;
using NotchTasks.Services.Modules;
using Xunit;

namespace NotchTasks.AppTests;

public sealed class HealthSourceTests
{
    private const long Gb = 1_000_000_000;

    /// The readings the tests set: the CPU times, the memory share and the free bytes of the drive.
    private sealed class FakeReadings
    {
        public CpuTimes? Cpu = new(0, 0, 0);
        public double? Memory = 0.4;
        public long? Free = 100 * Gb;

        public HealthReaders Readers => new(() => Cpu, () => Memory, _ => Free);
    }

    /// The app state over a temp settings file and asks folder, with the given readings in place of this PC's.
    private static AppState NewState(HealthReaders readers)
    {
        var settings = SettingsStore.Load(AppPaths.SettingsFile);
        return new AppState(settings, new AskStore(AppPaths.AsksDir), readers);
    }

    [Fact]
    public void LoadTimer_RunsOnlyWhilePillIsOpen_AndTheLastLoadStaysForReopen()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var fake = new FakeReadings();
            using var state = NewState(fake.Readers);
            var source = Fixture.Field<HealthSource>(state, "_healthSource");
            var loadTimer = Fixture.Field<DispatcherTimer>(source, "_loadTimer");

            // The health module is on by default, so its source is started; the load waits for the open pill.
            Assert.Contains("health", AppState.ActiveModules(state.Settings));
            Assert.False(loadTimer.IsEnabled);

            state.PillOpened(true);
            Assert.True(loadTimer.IsEnabled);
            Assert.Equal(TimeSpan.FromSeconds(1), loadTimer.Interval);

            // The open took the baseline; the next sample gives 50 % CPU.
            fake.Cpu = new CpuTimes(50, 70, 30);
            Fixture.Call(source, "SampleLoad");
            Assert.NotNull(state.Snapshot.Load);

            // Closing stops the timer and keeps the last meters, which are not published away.
            state.PillOpened(false);
            Assert.False(loadTimer.IsEnabled);
            Assert.NotNull(state.Health.Load);

            // Reopening shows the last load at once: the baseline sample does not clear it.
            state.PillOpened(true);
            Assert.True(loadTimer.IsEnabled);
            Assert.NotNull(state.Snapshot.Load);
            Assert.Equal(0.5, state.Snapshot.Load!.Cpu, 6);
            state.PillOpened(false);
        });
    }

    [Fact]
    public void SystemLoadOff_WhileOpen_StopsTheTimerAndClearsTheLoad()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var fake = new FakeReadings();
            using var state = NewState(fake.Readers);
            var source = Fixture.Field<HealthSource>(state, "_healthSource");
            var loadTimer = Fixture.Field<DispatcherTimer>(source, "_loadTimer");
            state.PillOpened(true);
            fake.Cpu = new CpuTimes(50, 70, 30);
            Fixture.Call(source, "SampleLoad");
            Assert.NotNull(state.Health.Load);

            state.Settings.Set("systemLoad", JsonValue.Create(false));
            source.SystemLoadChanged();

            Assert.False(loadTimer.IsEnabled);
            Assert.Null(state.Health.Load);
            Assert.Null(state.Snapshot.Load);
        });
    }

    [Fact]
    public void Stop_StopsBothTimers()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = NewState(new FakeReadings().Readers);
            var source = Fixture.Field<HealthSource>(state, "_healthSource");
            var diskTimer = Fixture.Field<DispatcherTimer>(source, "_diskTimer");
            var loadTimer = Fixture.Field<DispatcherTimer>(source, "_loadTimer");
            state.PillOpened(true);
            Assert.True(diskTimer.IsEnabled);
            Assert.True(loadTimer.IsEnabled);

            source.Stop();

            Assert.False(diskTimer.IsEnabled);
            Assert.False(loadTimer.IsEnabled);
            Assert.Null(state.Health.Load);
        });
    }

    [Fact]
    public void Disk_AtStartUnder10GB_ToastsSafely()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            // The disk is read while AppState is constructed, and its warning is announced then: that must not throw.
            using var state = NewState(new FakeReadings { Free = 5 * Gb }.Readers);

            Assert.Equal("warning", state.Toasts.Current?.Event);
            Assert.Equal("health", state.Toasts.Current?.Module);
        });
    }

    [Fact]
    public void LoadTick_PublishesOnlyOnAPercentChange_AndKeepsTheAskCards()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var fake = new FakeReadings();
            using var state = NewState(fake.Readers);
            var source = Fixture.Field<HealthSource>(state, "_healthSource");
            var pill = new PillWindow(state);
            try
            {
                state.Asks.Add(new AgentAsk("h1", 1, "work", "Bash", "ls", Array.Empty<AskQuestion>(), DateTimeOffset.Now, "claude"));
                state.Publish();
                var sections = Fixture.Named<Panel>(pill, "SectionList");
                var card = sections.Children[0];

                var notched = 0;
                var loaded = 0;
                state.NotchChanged += () => notched++;
                state.LoadChanged += () => loaded++;

                state.PillOpened(true);  // the baseline: nothing to show yet, so no publish
                Assert.Equal(0, loaded);

                fake.Cpu = new CpuTimes(50, 70, 30);  // 50 % CPU, 40 % RAM: the meters change
                Fixture.Call(source, "SampleLoad");
                Assert.Equal(1, loaded);

                fake.Cpu = new CpuTimes(100, 140, 60);  // 50 % again: the rounded percents do not change
                Fixture.Call(source, "SampleLoad");
                Assert.Equal(1, loaded);

                fake.Cpu = new CpuTimes(100, 240, 60);  // 100 % CPU: a change
                Fixture.Call(source, "SampleLoad");
                Assert.Equal(2, loaded);

                // A load tick moves the header only: the notch is not rebuilt, and the ask card is the same element.
                Assert.Equal(0, notched);
                Assert.Same(card, sections.Children[0]);
                Assert.Equal(Visibility.Visible, Fixture.Named<UIElement>(pill, "LoadMeters").Visibility);
                state.PillOpened(false);
            }
            finally
            {
                pill.Close();
            }
        });
    }
}
