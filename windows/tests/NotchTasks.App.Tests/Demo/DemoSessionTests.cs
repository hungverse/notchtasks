using System.IO;
using NotchTasks.Core;
using NotchTasks.Core.Asks;
using NotchTasks.Core.Demo;
using NotchTasks.Core.Devices;
using NotchTasks.Core.Media;
using NotchTasks.Core.Modules;
using NotchTasks.Core.Notch;
using NotchTasks.Demo;
using NotchTasks.Services.Devices;
using NotchTasks.Services.Media;
using NotchTasks.Services.Modules;
using Xunit;

namespace NotchTasks.AppTests.Demo;

/// The demo run's state: it touches no settings file, starts no source, and shows the timeline's content.
public sealed class DemoSessionTests
{
    /// Records the Allow hold calls.
    private sealed class RecordingStage
    {
        public bool Pressed { get; private set; }

        public void PressAllow(bool pressed) => Pressed = pressed;
    }

    /// Counts every read and start of the OS readers a source would use. A demo that starts a source reads one of these.
    private sealed class CountingReaders
    {
        public int Calls { get; private set; }

        public AppStateOptions Options() => new(
            Health: new HealthReaders(
                () => { Calls++; return new CpuTimes(0, 0, 0); },
                () => { Calls++; return 0.4; },
                _ => { Calls++; return 1L; }),
            Network: new NetworkReaders(
                () => { Calls++; return new List<NetIf>(); },
                _ => { Calls++; return new Fixture.NoopWatch(); }),
            Media: new CountingMedia(this),
            Devices: new CountingDevices(this));

        private sealed class CountingMedia : IMediaReader
        {
            private readonly CountingReaders _owner;

            public CountingMedia(CountingReaders owner) => _owner = owner;

            public event Action Changed
            {
                add { }
                remove { }
            }

            public Task Start()
            {
                _owner.Calls++;
                return Task.CompletedTask;
            }

            public void Stop()
            {
            }

            public void FollowTimeline(bool follow) => _owner.Calls++;

            public Task<IReadOnlyList<MediaSessionFact>> Read()
            {
                _owner.Calls++;
                return Task.FromResult<IReadOnlyList<MediaSessionFact>>(Array.Empty<MediaSessionFact>());
            }

            public Task Send(string source, MediaCommand command) => Task.CompletedTask;

            public Task Seek(string source, double seconds) => Task.CompletedTask;

            public Task<byte[]?> Artwork(string source) => Task.FromResult<byte[]?>(null);
        }

        private sealed class CountingDevices : IDeviceReader
        {
            private readonly CountingReaders _owner;

            public CountingDevices(CountingReaders owner) => _owner = owner;

            public event Action Changed
            {
                add { }
                remove { }
            }

            public void Start() => _owner.Calls++;

            public void Stop()
            {
            }

            public Task<IReadOnlyList<DeviceFact>?> Read()
            {
                _owner.Calls++;
                return Task.FromResult<IReadOnlyList<DeviceFact>?>(Array.Empty<DeviceFact>());
            }

            public Task<IReadOnlyList<DeviceFact>?> ReadBatteries()
            {
                _owner.Calls++;
                return Task.FromResult<IReadOnlyList<DeviceFact>?>(Array.Empty<DeviceFact>());
            }
        }
    }

    private static readonly DateTimeOffset Now = new(2026, 11, 27, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Demo_TouchesNoSettingsFile_StartsNoSource_AndWritesNothingUnderTheDataFolder()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var readers = new CountingReaders();
            using var state = DemoSession.NewState(readers.Options());
            var session = new DemoSession(state, _ => { });

            session.Start(Now);
            for (var t = TimeSpan.Zero; t < DemoTimeline.Loop; t += TimeSpan.FromMilliseconds(100))
            {
                session.Tick(t, Now + t);
            }

            Assert.True(state.IsDemo);
            Assert.Equal(0, readers.Calls);
            Assert.False(File.Exists(AppPaths.SettingsFile));
            Assert.Empty(Directory.GetFiles(root.Folder, "*", SearchOption.AllDirectories));
            Assert.Empty(Directory.GetDirectories(root.Folder));
        });
    }

    [Fact]
    public void Start_PutsTheAgentsAndTheRunningBuildInTheState_AndNoCardStackContent()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = DemoSession.NewState();
            var session = new DemoSession(state, _ => { });

            session.Start(Now);

            var running = state.Agents.Running;
            Assert.Equal(new[] { "Claude", "Codex" }, running.Select(s => s.Item.Name));
            Assert.All(running, s => Assert.True(s.Jobs[0].Working));

            Assert.NotNull(state.Ci.BuildState);
            var build = state.Ci.BuildState!;
            Assert.Equal("npm run build", build.Jobs[0].Title);
            Assert.True(build.Jobs[0].Working);

            // The closed pill only: no now playing session, no limits, no focus timer.
            Assert.Null(state.Media.Current);
            Assert.Empty(state.LimitsModule.Limits);
            Assert.Null(state.Timer.State);
            Assert.Equal(0, state.Snapshot.AskCount);
        });
    }

    [Fact]
    public void Tick_FollowsTheSixteenSecondTimeline_OneToastAtATime()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = DemoSession.NewState();
            var stage = new RecordingStage();
            var session = new DemoSession(state, stage.PressAllow);
            session.Start(Now);

            session.Tick(TimeSpan.Zero, Now);
            Assert.Empty(state.Asks);
            Assert.Null(state.Toasts.Current);

            session.Tick(TimeSpan.FromSeconds(2), Now);
            var ask = Assert.Single(state.Asks);
            Assert.Equal(1, state.Snapshot.AskCount);
            Assert.Equal("web-shop", ask.Session);
            Assert.Equal("git push origin main", ask.Summary);
            Assert.Equal("Claude needs your approval", state.Toasts.Current!.Title);
            Assert.Equal("web-shop · Run command: git push origin main", state.Toasts.Current.Subtitle);
            Assert.Equal(ask.Id, state.Toasts.Current.AskId);

            session.Tick(TimeSpan.FromSeconds(5), Now);
            Assert.True(stage.Pressed);
            Assert.Single(state.Asks);

            session.Tick(TimeSpan.FromSeconds(5.4), Now);
            Assert.False(stage.Pressed);
            Assert.Empty(state.Asks);
            // The done toast takes the ask's place at once: the queue shows one toast, the next, with no closed pill between them.
            Assert.Equal("Pushed to origin/main · web-shop", state.Toasts.Current!.Title);

            session.Tick(TimeSpan.FromSeconds(6), Now);
            Assert.Equal("Pushed to origin/main · web-shop", state.Toasts.Current!.Title);
            Assert.Equal("#30D158", state.Toasts.Current.Tint);

            session.Tick(TimeSpan.FromSeconds(8), Now);
            Assert.Null(state.Toasts.Current);
            Assert.False(state.Ci.BuildState!.Jobs[0].Working);
            Assert.Equal("Done", state.Ci.BuildState.Jobs[0].Detail);
            Assert.DoesNotContain(state.Ci.BuildState.Jobs, j => j.Working);  // the build chip leaves the closed pill

            session.Tick(TimeSpan.FromSeconds(10), Now);
            Assert.Equal("AirPods Pro connected", state.Toasts.Current!.Title);
            Assert.Equal("L 100% · R 98% · Case 62%", state.Toasts.Current.Subtitle);
            Assert.Null(state.Toasts.Current.Art);  // the headphones glyph, not an art

            session.Tick(TimeSpan.FromSeconds(13), Now);
            Assert.Null(state.Toasts.Current);
        });
    }

    [Fact]
    public void Allow_InTheDemo_LeavesTheAskAndWritesNoReply()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = DemoSession.NewState();
            var session = new DemoSession(state, _ => { });
            session.Start(Now);
            session.Tick(TimeSpan.FromSeconds(2), Now);
            var ask = Assert.Single(state.Asks);

            state.Answer(ask.Id, new AskReply(ReplyKinds.Allow, null));

            Assert.Empty(state.Asks);
            Assert.Empty(Directory.GetFiles(root.Folder, "*", SearchOption.AllDirectories));
        });
    }
}
