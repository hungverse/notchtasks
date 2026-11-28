using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using NotchTasks.ControlPanel.Panes;
using NotchTasks.Core;
using NotchTasks.Core.Modules;
using NotchTasks.Core.Sensors;
using NotchTasks.Core.Settings;
using NotchTasks.Services;
using NotchTasks.Services.Modules;
using Xunit;

namespace NotchTasks.AppTests;

/// The CPU temperature: the helper's readings come first while it is On; a counter zone that never moves is not shown; the Turn on
/// confirmation's Cancel starts nothing. The helper, the pipe and the thermal counter are fakes: no UAC, no pipe, no WMI.
public sealed class CpuTemperatureTests
{
    private const string Sid = "S-1-5-21-1111111111-2222222222-3333333333-1001";

    private sealed class CountingLauncher : IProcessLauncher
    {
        public int Calls;

        public Task<Process?> RunAsAsync(string fileName, string arguments)
        {
            Calls++;
            return Task.FromResult<Process?>(null);
        }
    }

    private sealed class FakePipe : ISensorPipe
    {
        public IReadOnlyList<ThermalReading>? Answer;

        public Task<IReadOnlyList<ThermalReading>?> QueryAsync(CancellationToken ct) => Task.FromResult(Answer);
    }

    private sealed class NoStop : IHelperStop
    {
        public bool Signal() => false;
    }

    private sealed class FakeCounter : IThermalCounter
    {
        public IReadOnlyList<ThermalReading> Zones { get; set; } = Array.Empty<ThermalReading>();

        public Task<IReadOnlyList<ThermalReading>> ReadAsync() => Task.FromResult(Zones);
    }

    private static SensorHelper NewHelper(SettingsStore settings, IProcessLauncher launcher, FakePipe pipe, HelperState state)
    {
        var helper = new SensorHelper(settings, launcher, pipe, new NoStop(), "NotchTasksSensors.exe", Sid, 1, _ => false);
        Fixture.Call(helper, "SetState", state);
        return helper;
    }

    private static Window Show(FrameworkElement pane)
    {
        var window = new Window { Content = pane };
        Fixture.ShowOffScreen(window);
        window.UpdateLayout();
        return window;
    }

    [Fact]
    public void HelperOn_ItsCpuReadingIsShownOverTheCounter()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            File.WriteAllText(AppPaths.SettingsFile, "{\"sensorHelper\": true}");
            using var settings = SettingsStore.Load(AppPaths.SettingsFile);
            var pipe = new FakePipe { Answer = new[] { new ThermalReading("CPU", 55), new ThermalReading("GPU", 61) } };
            var helper = NewHelper(settings, new CountingLauncher(), pipe, HelperState.On);
            var counter = new FakeCounter { Zones = new[] { new ThermalReading("\\_TZ.TZ00", 27.85) } };
            var published = new List<ThermalReading?>();
            var source = new ThermalSource(settings, helper, counter, published.Add);
            try
            {
                source.Start();
                source.PillOpened(true);
                Fixture.PumpUntil(() => published.Count > 0);

                Assert.Equal(new ThermalReading("CPU", 55), published[0]);
            }
            finally
            {
                source.Dispose();
            }
        });
    }

    [Fact]
    public void FixedCounterZone_WithTheHelperOff_ShowsNothing_AndOffersTurnOn()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var settings = SettingsStore.Load(AppPaths.SettingsFile);
            var pipe = new FakePipe();
            var helper = NewHelper(settings, new CountingLauncher(), pipe, HelperState.Off);
            // The counter's zone reads 28 C on every read: the fixed BIOS value. Ten reads make it fixed.
            var counter = new FixedZoneCounter(new FakeCounter { Zones = new[] { new ThermalReading("\\_TZ.TZ00", 28) } });
            for (var read = 0; read < FixedZoneRule.Reads; read++)
            {
                Assert.Empty(counter.ReadAsync().GetAwaiter().GetResult());
            }

            var pane = new TemperaturesPane(settings, helper, counter, _ => true);
            var window = Show(pane);
            try
            {
                Assert.Equal(Visibility.Collapsed, Fixture.Named<Border>(pane, "HottestCard").Visibility);
                Assert.Equal(Visibility.Visible, Fixture.Named<Border>(pane, "HelperCard").Visibility);
                Assert.Equal(Visibility.Visible, Fixture.Named<Button>(pane, "TurnOnButton").Visibility);
                Assert.Equal(L10n.L("CPU temperature") + " · " + L10n.L("Needs administrator"), Fixture.Named<TextBlock>(pane, "HelperTitle").Text);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void TurnOnCancelled_StartsNothing()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var settings = SettingsStore.Load(AppPaths.SettingsFile);
            var launcher = new CountingLauncher();
            var helper = NewHelper(settings, launcher, new FakePipe(), HelperState.Off);
            var pane = new TemperaturesPane(settings, helper, new FakeCounter(), _ => false);  // the confirmation's Cancel
            var window = Show(pane);
            try
            {
                Fixture.Named<Button>(pane, "TurnOnButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                Assert.Equal(0, launcher.Calls);
                Assert.Equal(HelperState.Off, helper.State);
                Assert.False(settings.SensorHelper);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void HelperAcpiFallbackZone_IsNotShown_WhileItNeverMoves()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var settings = SettingsStore.Load(AppPaths.SettingsFile);
            var pipe = new FakePipe { Answer = new[] { new ThermalReading("\\_TZ.TZ00", 28) } };
            var helper = NewHelper(settings, new CountingLauncher(), pipe, HelperState.On);

            // The helper answers its WMI fallback (no CPU from the hardware): the fixed zone is judged as the counter's zones are.
            for (var read = 0; read < FixedZoneRule.Reads; read++)
            {
                Assert.Empty(helper.ReadAsync().GetAwaiter().GetResult());
            }

            pipe.Answer = new[] { new ThermalReading("CPU", 49) };
            Assert.Equal([new ThermalReading("CPU", 49)], helper.ReadAsync().GetAwaiter().GetResult());
        });
    }
}
