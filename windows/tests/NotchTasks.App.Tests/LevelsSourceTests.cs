using System.Windows.Threading;
using NotchTasks.Core.Levels;
using NotchTasks.Core.Modules;
using NotchTasks.Services.Modules;
using Xunit;

namespace NotchTasks.AppTests;

/// The volume and brightness source with fake outputs and a fake panel: the baselines at start, the bar on a volume or brightness change,
/// a default device change that re-binds without a bar, a PC with no panel, and Stop releasing everything. No test touches Core Audio or WMI.
public sealed class LevelsSourceTests
{
    /// An output endpoint the test controls: its volume and mute, a change it can raise, and whether it was disposed.
    private sealed class FakeEndpoint : IOutputEndpoint
    {
        public FakeEndpoint(string id, VolumeReading? volume)
        {
            Id = id;
            Volume = volume;
        }

        public string Id { get; }
        public VolumeReading? Volume { get; set; }
        public bool Disposed { get; private set; }
        public event Action? Changed;

        public VolumeReading? Read() => Volume;

        public void RaiseChanged() => Changed?.Invoke();

        public void Dispose() => Disposed = true;
    }

    /// The output devices: the endpoints it hands out in order (a null is no output), and a default change it can raise.
    private sealed class FakeOutputs : IOutputDevices
    {
        public Queue<FakeEndpoint?> Plan { get; } = new();
        public int Opens { get; private set; }
        public bool Disposed { get; private set; }
        public event Action? DefaultChanged;

        public IOutputEndpoint? OpenDefault()
        {
            Opens++;
            return Plan.Count > 0 ? Plan.Dequeue() : null;
        }

        public void RaiseDefaultChanged() => DefaultChanged?.Invoke();

        public void Dispose() => Disposed = true;
    }

    /// The panel's brightness: null for a PC with no panel. Watching is on while the source asks for the change feed.
    private sealed class FakePanel : IBrightness
    {
        public FakePanel(double? level) => Level = level;

        public double? Level { get; set; }
        public bool Watching { get; private set; }
        public int WatchCalls { get; private set; }
        public bool Disposed { get; private set; }

        /// When set, the first read waits for it: a test holds the panel's open on its worker thread to stop the source before it lands.
        public ManualResetEventSlim? Gate { get; set; }
        public event Action<double>? Changed;

        public double? Read()
        {
            Gate?.Wait();
            return Level;
        }

        public void Watch(bool on)
        {
            WatchCalls++;
            Watching = on;
        }

        public void RaiseChanged(double level) => Changed?.Invoke(level);

        public void Dispose()
        {
            Disposed = true;
            Watching = false;
        }
    }

    private static DispatcherTimer Timer(LevelsSource source) => Fixture.Field<DispatcherTimer>(source, "_hide");

    private static LevelsSource Source(LevelsModule module, FakeOutputs outputs, FakePanel panel) =>
        new(module, () => outputs, () => panel);

    /// Whether the panel the worker opened has come back to the source (it is adopted on the UI thread, after Start returns).
    private static bool Adopted(LevelsSource source) => Fixture.Field<IBrightness?>(source, "_panel") is not null;

    [Fact]
    public void Start_ReadsTheBaselines_AndShowsNothing_AndWatchesThePanel()
    {
        Sta.Run(() =>
        {
            var module = new LevelsModule();
            var outputs = new FakeOutputs();
            outputs.Plan.Enqueue(new FakeEndpoint("speakers", new VolumeReading(0.5, false)));
            var panel = new FakePanel(0.4);
            var source = Source(module, outputs, panel);
            try
            {
                source.Start();
                Fixture.PumpUntil(() => Adopted(source));

                Assert.Null(module.Hud);
                Assert.Equal(1, outputs.Opens);
                Assert.True(panel.Watching);
                // The baselines are set: a move past the step is a bar, a small one is not.
                Assert.False(module.Brightness(0.402));
            }
            finally
            {
                source.Stop();
            }
        });
    }

    [Fact]
    public void VolumeChange_ShowsTheVolumeBar_ThenHidesAfterItsTime()
    {
        Sta.Run(() =>
        {
            var module = new LevelsModule();
            var outputs = new FakeOutputs();
            var speakers = new FakeEndpoint("speakers", new VolumeReading(0.5, false));
            outputs.Plan.Enqueue(speakers);
            var source = Source(module, outputs, new FakePanel(null));
            try
            {
                source.Start();

                speakers.Volume = new VolumeReading(0.7, false);
                speakers.RaiseChanged();
                Fixture.PumpUntil(() => module.Hud is not null);

                Assert.Equal(new LevelHud(LevelKind.Volume, 0.7, false), module.Hud);
                Assert.True(Timer(source).IsEnabled);
                Fixture.PumpUntil(() => module.Hud is null);
                Assert.False(Timer(source).IsEnabled);
            }
            finally
            {
                source.Stop();
            }
        });
    }

    [Fact]
    public void BrightnessChange_ShowsTheBrightnessBar()
    {
        Sta.Run(() =>
        {
            var module = new LevelsModule();
            var panel = new FakePanel(0.4);
            var source = Source(module, new FakeOutputs(), panel);
            try
            {
                source.Start();
                Fixture.PumpUntil(() => Adopted(source));

                panel.RaiseChanged(0.9);
                Fixture.PumpUntil(() => module.Hud is not null);

                Assert.Equal(new LevelHud(LevelKind.Brightness, 0.9), module.Hud);
            }
            finally
            {
                source.Stop();
            }
        });
    }

    [Fact]
    public void PcWithNoPanel_WatchesNothing_AndShowsOnlyVolume()
    {
        Sta.Run(() =>
        {
            var module = new LevelsModule();
            var panel = new FakePanel(null);
            var source = Source(module, new FakeOutputs(), panel);
            try
            {
                source.Start();
                Fixture.PumpUntil(() => Adopted(source));

                Assert.False(panel.Watching);
                Assert.Equal(0, panel.WatchCalls);
            }
            finally
            {
                source.Stop();
            }
        });
    }

    [Fact]
    public void StopBeforeThePanelOpens_DiscardsIt_AndNeverWatches()
    {
        // The panel's first read runs on a worker; a Stop before it lands must give the panel back, not adopt it.
        Sta.Run(() =>
        {
            var module = new LevelsModule();
            var panel = new FakePanel(0.4) { Gate = new ManualResetEventSlim(false) };
            var source = Source(module, new FakeOutputs(), panel);

            source.Start();
            source.Stop();
            panel.Gate.Set();

            Fixture.PumpUntil(() => panel.Disposed);
            Assert.False(panel.Watching);
            Assert.False(Adopted(source));
            Assert.Null(module.Hud);
        });
    }

    [Fact]
    public void Start_ReturnsBeforeThePanelOpens()
    {
        // Start must not wait on WMI: with the panel's read held, Start still returns, and nothing is adopted yet.
        Sta.Run(() =>
        {
            var panel = new FakePanel(0.4) { Gate = new ManualResetEventSlim(false) };
            var source = Source(new LevelsModule(), new FakeOutputs(), panel);
            try
            {
                source.Start();

                Assert.False(Adopted(source));
                Assert.False(panel.Watching);
            }
            finally
            {
                panel.Gate.Set();
                source.Stop();
            }
        });
    }

    [Fact]
    public void DefaultDeviceChange_RebindsWithoutShowingTheJump_AndDropsTheOldEndpoint()
    {
        Sta.Run(() =>
        {
            var module = new LevelsModule();
            var outputs = new FakeOutputs();
            var speakers = new FakeEndpoint("speakers", new VolumeReading(0.5, false));
            var headphones = new FakeEndpoint("headphones", new VolumeReading(0.9, false));
            outputs.Plan.Enqueue(speakers);
            outputs.Plan.Enqueue(headphones);
            var source = Source(module, outputs, new FakePanel(null));
            try
            {
                source.Start();

                outputs.RaiseDefaultChanged();
                Fixture.PumpUntil(() => outputs.Opens == 2 && speakers.Disposed);

                // The new output's volume is its baseline, not a change: no bar.
                Assert.Null(module.Hud);

                // The old endpoint's notification is no longer heard.
                speakers.Volume = new VolumeReading(0.1, false);
                speakers.RaiseChanged();
                Fixture.Flush();
                Assert.Null(module.Hud);

                // The new output's own change shows the bar.
                headphones.Volume = new VolumeReading(0.95, false);
                headphones.RaiseChanged();
                Fixture.PumpUntil(() => module.Hud is not null);
                Assert.Equal(new LevelHud(LevelKind.Volume, 0.95, false), module.Hud);
            }
            finally
            {
                source.Stop();
            }
        });
    }

    [Fact]
    public void NoOutputAtStart_TakesTheFirstDeviceLater_WithoutAJump()
    {
        Sta.Run(() =>
        {
            var module = new LevelsModule();
            var outputs = new FakeOutputs();
            outputs.Plan.Enqueue(null);
            var headphones = new FakeEndpoint("headphones", new VolumeReading(0.3, false));
            outputs.Plan.Enqueue(headphones);
            var source = Source(module, outputs, new FakePanel(null));
            try
            {
                source.Start();
                Assert.Null(module.Hud);

                outputs.RaiseDefaultChanged();
                Fixture.PumpUntil(() => outputs.Opens == 2);
                Fixture.Flush();
                Assert.Null(module.Hud);

                headphones.Volume = new VolumeReading(0.6, false);
                headphones.RaiseChanged();
                Fixture.PumpUntil(() => module.Hud is not null);
                Assert.Equal(0.6, module.Hud!.Value.Value);
            }
            finally
            {
                source.Stop();
            }
        });
    }

    [Fact]
    public void Stop_ReleasesTheEndpoint_TheDeviceWatch_AndThePanel_AndClearsTheBar()
    {
        Sta.Run(() =>
        {
            var module = new LevelsModule();
            var outputs = new FakeOutputs();
            var speakers = new FakeEndpoint("speakers", new VolumeReading(0.5, false));
            outputs.Plan.Enqueue(speakers);
            var panel = new FakePanel(0.4);
            var source = Source(module, outputs, panel);
            source.Start();
            speakers.Volume = new VolumeReading(0.7, false);
            speakers.RaiseChanged();
            Fixture.PumpUntil(() => module.Hud is not null);
            Fixture.PumpUntil(() => Adopted(source) && panel.Watching);

            source.Stop();

            Assert.True(speakers.Disposed);
            Assert.True(outputs.Disposed);
            Assert.False(panel.Watching);
            Assert.True(panel.Disposed);
            Assert.Null(module.Hud);
            Assert.False(Timer(source).IsEnabled);
        });
    }

    [Fact]
    public void AfterStop_ChangesAreHeardNoMore()
    {
        Sta.Run(() =>
        {
            var module = new LevelsModule();
            var outputs = new FakeOutputs();
            var speakers = new FakeEndpoint("speakers", new VolumeReading(0.5, false));
            outputs.Plan.Enqueue(speakers);
            var panel = new FakePanel(0.4);
            var source = Source(module, outputs, panel);
            source.Start();
            source.Stop();

            speakers.Volume = new VolumeReading(0.9, false);
            speakers.RaiseChanged();
            panel.RaiseChanged(0.9);
            Fixture.Flush();

            Assert.Null(module.Hud);
        });
    }
}
