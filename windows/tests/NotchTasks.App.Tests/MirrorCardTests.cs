using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using NotchTasks.Core;
using NotchTasks.Core.Asks;
using NotchTasks.Core.Settings;
using NotchTasks.Pill;
using NotchTasks.Pill.Sections;
using NotchTasks.Services;
using Xunit;

namespace NotchTasks.AppTests;

/// The camera mirror: its quick bar button, its card, and the camera's lifetime (it runs only while the mirror is open). The
/// camera is a fake: no test touches a real one.
[Collection("L10n")]
public sealed class MirrorCardTests
{
    /// The English texts, from the Mac's tables (the English text is the key, so these are the strings the card shows).
    private static void InitEnglish() => L10n.Init(MacTables(), "en");

    private static string MacTables()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "mac", "Sources")))
        {
            dir = Path.GetDirectoryName(dir);
        }
        return Path.Combine(dir ?? throw new DirectoryNotFoundException("mac/Sources"), "mac", "Sources");
    }

    /// An app state with only the mirror module on. Each camera it makes is added to `cameras`.
    private static AppState NewState(List<FakeCamera> cameras, Func<FakeCamera>? make = null)
    {
        var settings = SettingsStore.Load(AppPaths.SettingsFile);
        settings.Set("modules", JsonNode.Parse("[\"mirror\"]"));
        settings.Set("disabled", JsonNode.Parse("[]"));
        var options = Fixture.Inert(settings) with
        {
            Camera = () =>
            {
                var camera = make?.Invoke() ?? new FakeCamera();
                cameras.Add(camera);
                return camera;
            },
        };
        return new AppState(settings, new AskStore(AppPaths.AsksDir), options);
    }

    /// A frame of 2 by 2 pixels (16 bytes) filled with one value, so a test can tell the frames apart.
    private static byte[] Frame(byte value) => Enumerable.Repeat(value, 16).ToArray();

    [Fact]
    public void QuickBar_MirrorButton_OnlyWhenOnAndClosed()
    {
        Sta.Run(() =>
        {
            InitEnglish();
            using var root = new TempRoot();
            var cameras = new List<FakeCamera>();
            using var state = NewState(cameras);
            var pill = new PillWindow(state);
            try
            {
                state.Publish();
                var bar = Fixture.Named<QuickBar>(pill, "Bar");
                var mirror = Fixture.Named<Button>(bar, "MirrorButton");

                Assert.Equal(Visibility.Visible, mirror.Visibility);
                Assert.Equal("Quick camera check", mirror.ToolTip);

                mirror.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));  // the click opens the mirror and starts the camera
                Assert.True(state.MirrorOn);
                Assert.Single(cameras);
                state.Publish();
                Assert.Equal(Visibility.Collapsed, mirror.Visibility);

                state.CloseMirror();
                state.Publish();
                Assert.Equal(Visibility.Visible, mirror.Visibility);

                state.Settings.Set("disabled", JsonNode.Parse("[\"mirror\"]"));  // the mirror module off (the toggle): no button
                Fixture.Flush();
                state.Publish();
                Assert.Equal(Visibility.Collapsed, mirror.Visibility);
            }
            finally
            {
                pill.Close();
            }
        });
    }

    [Fact]
    public void Open_StartsSession_Close_StopsIt()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var cameras = new List<FakeCamera>();
            using var state = NewState(cameras);

            state.OpenMirror();
            var camera = Assert.Single(cameras);
            Assert.Equal(1, camera.Starts);
            Assert.Equal(0, camera.Stops);

            state.CloseMirror();
            Assert.False(state.MirrorOn);
            Assert.Equal(1, camera.Stops);
            Assert.Equal(1, camera.Disposes);

            state.CloseMirror();  // a second close does nothing
            Assert.Equal(1, camera.Stops);
        });
    }

    [Fact]
    public void PillClosed_ClosesMirror()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var cameras = new List<FakeCamera>();
            using var state = NewState(cameras);
            state.OpenMirror();

            state.PillOpened(false);

            Assert.False(state.MirrorOn);
            Assert.Equal(1, cameras[0].Stops);
        });
    }

    [Fact]
    public void MirrorTurnedOff_ClosesMirror()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var cameras = new List<FakeCamera>();
            using var state = NewState(cameras);
            state.OpenMirror();

            state.Settings.Set("disabled", JsonNode.Parse("[\"mirror\"]"));
            Fixture.Flush();

            Assert.False(state.MirrorOn);
            Assert.Equal(1, cameras[0].Stops);
        });
    }

    [Fact]
    public void Denied_ShowsSettingsButton()
    {
        Sta.Run(() =>
        {
            InitEnglish();
            using var root = new TempRoot();
            var cameras = new List<FakeCamera>();
            using var state = NewState(cameras, () => new FakeCamera { Result = CameraStart.Denied });
            state.OpenMirror();
            var card = new MirrorCard();

            Assert.True(card.Update(state));

            Assert.Equal(CameraStart.Denied, state.MirrorStatus);
            Assert.Equal("NotchTasks can't use the camera yet", card.ShownMessage);
            Assert.True(card.SettingsButtonShown);
        });
    }

    [Theory]
    [InlineData(CameraStart.NoCamera)]
    [InlineData(CameraStart.Failed)]
    public void NoCamera_ShowsMessage(CameraStart result)
    {
        Sta.Run(() =>
        {
            InitEnglish();
            using var root = new TempRoot();
            var cameras = new List<FakeCamera>();
            using var state = NewState(cameras, () => new FakeCamera { Result = result });
            state.OpenMirror();
            var card = new MirrorCard();

            Assert.True(card.Update(state));

            Assert.Equal("No camera found", card.ShownMessage);
            Assert.False(card.SettingsButtonShown);
        });
    }

    [Fact]
    public void ClosedDuringStart_ReleasesCamera()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var pending = new TaskCompletionSource<CameraStart>();
            var cameras = new List<FakeCamera>();
            using var state = NewState(cameras, () => new FakeCamera { Pending = pending });
            state.OpenMirror();
            var camera = cameras[0];

            state.CloseMirror();  // the camera is still starting
            Assert.Equal(1, camera.Stops);
            Assert.Equal(1, camera.Disposes);

            pending.SetResult(CameraStart.Running);  // the start completes after the close: its result is dropped
            Fixture.Flush();
            Assert.False(state.MirrorOn);
            Assert.Null(state.MirrorStatus);
        });
    }

    [Fact]
    public void CloseThenOpen_StartsNewCameraOnlyAfterOldStops()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var stopGate = new TaskCompletionSource();
            var cameras = new List<FakeCamera>();
            using var state = NewState(cameras, () => cameras.Count == 0 ? new FakeCamera { StopGate = stopGate } : new FakeCamera());
            state.OpenMirror();
            var old = cameras[0];
            state.CloseMirror();  // the old camera is still releasing

            state.OpenMirror();
            Fixture.Flush();
            Assert.Equal(2, cameras.Count);
            Assert.Equal(0, cameras[1].Starts);  // the new camera waits for the old release

            stopGate.SetResult();
            Fixture.Flush();
            Assert.Equal(1, cameras[1].Starts);
            Assert.Equal(1, old.Disposes);
        });
    }

    [Fact]
    public void Frames_OnlyOnePending()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var cameras = new List<FakeCamera>();
            using var state = NewState(cameras);
            state.OpenMirror();
            var delivered = new List<MirrorPixels>();
            state.MirrorFrameReady += delivered.Add;

            // three frames arrive before the dispatcher runs: only the newest waits and is delivered
            var camera = cameras[0];
            camera.Frame!(2, 2, Frame(1));
            camera.Frame!(2, 2, Frame(2));
            camera.Frame!(2, 2, Frame(3));
            Fixture.Flush();

            var only = Assert.Single(delivered);
            Assert.Equal(2, only.Width);
            Assert.Equal(2, only.Height);
            Assert.Equal(Frame(3), only.Bgra);
        });
    }

    /// A camera that answers what it is given. Pending, when set, holds Start until the test completes it.
    private sealed class FakeCamera : ICameraSession
    {
        public CameraStart Result { get; init; } = CameraStart.Running;

        public TaskCompletionSource<CameraStart>? Pending { get; init; }

        /// When set, Stop completes only when the test completes this (the camera is still releasing).
        public TaskCompletionSource? StopGate { get; init; }

        public int Starts { get; private set; }

        public int Stops { get; private set; }

        public int Disposes { get; private set; }

        public Action<int, int, byte[]>? Frame { get; private set; }

        public Task<CameraStart> Start(Action<int, int, byte[]> frame)
        {
            Starts++;
            Frame = frame;
            return Pending?.Task ?? Task.FromResult(Result);
        }

        public Task Stop()
        {
            Stops++;
            return StopGate?.Task ?? Task.CompletedTask;
        }

        public void Dispose() => Disposes++;
    }
}
