using System.Diagnostics;
using System.IO;
using NotchTasks.Core;
using NotchTasks.Core.Modules;
using NotchTasks.Core.Settings;
using NotchTasks.Services;
using NotchTasks.Services.Modules;
using Xunit;

namespace NotchTasks.AppTests;

/// The header's temperature feed: the thermal counter is read first, and the temperature helper is asked only when the counter gives no
/// plausible reading. Both are fakes: no WMI, no UAC, no pipe.
public sealed class ThermalSourceTests
{
    private const string Sid = "S-1-5-21-1111111111-2222222222-3333333333-1001";

    private sealed class FakeCounter : IThermalCounter
    {
        public IReadOnlyList<ThermalReading> Zones { get; set; } = Array.Empty<ThermalReading>();

        public int Reads;

        public Task<IReadOnlyList<ThermalReading>> ReadAsync()
        {
            Reads++;
            return Task.FromResult(Zones);
        }
    }

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

    /// A helper over fakes. On says whether the helper is On (it answers from the pipe).
    private static SensorHelper NewHelper(SettingsStore settings, FakePipe pipe, bool on)
    {
        var helper = new SensorHelper(settings, new NoLaunch(), pipe, new NoStop(), "NotchTasksSensors.exe", Sid, 1, _ => false);
        if (on)
        {
            Fixture.Call(helper, "SetState", HelperState.On);
        }
        return helper;
    }

    [Fact]
    public void CounterReadings_ArePublished_WithTheHelperOff()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var settings = SettingsStore.Load(AppPaths.SettingsFile);
            var pipe = new FakePipe();
            var helper = NewHelper(settings, pipe, on: false);
            var counter = new FakeCounter { Zones = new[] { new ThermalReading("\\_TZ.TZ00", 27.85) } };
            var published = new List<ThermalReading?>();
            var source = new ThermalSource(settings, helper, counter, published.Add);
            try
            {
                source.Start();
                source.PillOpened(true);
                Fixture.PumpUntil(() => published.Count > 0);

                Assert.Equal("\\_TZ.TZ00", published[0]!.Value.Name);
                Assert.Equal(27.85, published[0]!.Value.Celsius, 0.01);
                Assert.Equal(0, pipe.Calls);  // the helper is not asked while the counter reads
            }
            finally
            {
                source.Dispose();
            }
        });
    }

    [Fact]
    public void NoCounterReadings_HelperOn_PublishesTheHelpersReading()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            File.WriteAllText(AppPaths.SettingsFile, "{\"sensorHelper\": true}");
            using var settings = SettingsStore.Load(AppPaths.SettingsFile);
            var pipe = new FakePipe { Answer = new[] { new ThermalReading("TZ00", 55) } };
            var helper = NewHelper(settings, pipe, on: true);
            var counter = new FakeCounter();
            var published = new List<ThermalReading?>();
            var source = new ThermalSource(settings, helper, counter, published.Add);
            try
            {
                source.Start();
                source.PillOpened(true);
                Fixture.PumpUntil(() => published.Count > 0);

                Assert.Equal("TZ00", published[0]!.Value.Name);
                Assert.True(pipe.Calls > 0);
                Assert.True(counter.Reads > 0);  // the counter is read first
            }
            finally
            {
                source.Dispose();
            }
        });
    }

    [Fact]
    public void NoCounterReadings_HelperOff_PublishesNothing()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var settings = SettingsStore.Load(AppPaths.SettingsFile);
            var pipe = new FakePipe { Answer = new[] { new ThermalReading("TZ00", 55) } };
            var helper = NewHelper(settings, pipe, on: false);
            var counter = new FakeCounter();
            var published = new List<ThermalReading?>();
            var source = new ThermalSource(settings, helper, counter, published.Add);
            try
            {
                source.Start();
                source.PillOpened(true);
                Fixture.PumpUntil(() => counter.Reads > 0);

                Assert.Empty(published);
                Assert.Equal(0, pipe.Calls);  // nothing is asked of the helper
            }
            finally
            {
                source.Dispose();
            }
        });
    }
}
