using NotchTasks.Core.Modules;
using NotchTasks.Core.Notch;
using Xunit;

namespace NotchTasks.Core.Tests;

/// The calls and privacy module ports the Mac's applySensors (Model.swift): a call starts and ends with a compact toast,
/// a call that comes back within 8 s carries on, and a mic or camera outside a call says so once.
[Collection("L10n")]
public class SensorsModuleTests
{
    /// An announcer that keeps every toast with its notify flag.
    private sealed class RecordingAnnouncer : IAnnouncer
    {
        public List<(Toast Toast, bool Notify)> Calls { get; } = new();

        public void Announce(Toast t, bool notify = true) => Calls.Add((t, notify));
    }

    private static readonly DateTimeOffset T0 = new(2026, 11, 19, 10, 0, 0, TimeSpan.Zero);
    private static readonly SensorState Quiet = new(Array.Empty<string>(), false, false);

    private static SensorState Mic(params string[] apps) => new(apps, false, false);

    private static SensorsModule Make(RecordingAnnouncer announce, bool privacy = true, bool calls = true) =>
        new(announce, () => privacy, () => calls);

    [Fact]
    public void CallStart_AnnouncesCompact()
    {
        var announce = new RecordingAnnouncer();
        var module = Make(announce);

        module.Apply(Mic("zoom.exe"), T0);

        Assert.NotNull(module.Call);
        Assert.Equal("zoom.exe", module.Call!.App);
        Assert.Equal("Zoom", module.Call.Name);
        Assert.Equal(T0, module.Call.Since);
        var (toast, notify) = Assert.Single(announce.Calls);
        Assert.False(notify);
        Assert.Equal("phone", toast.Glyph);
        Assert.Equal("#30D158", toast.Tint);
        Assert.Equal("On a call", toast.Title);
        Assert.Equal("Zoom", toast.Subtitle);
        Assert.Equal("callStart", toast.Event);
        Assert.True(toast.Compact);
        Assert.Equal("calls", toast.Module);
    }

    [Fact]
    public void CallGone_EndsAfter8s_AnnouncesDuration()
    {
        var announce = new RecordingAnnouncer();
        var module = Make(announce);
        module.Apply(Mic("zoom.exe"), T0);

        module.Apply(Quiet, T0 + TimeSpan.FromSeconds(60));
        Assert.NotNull(module.Call);  // the call is still there for 8 s
        Assert.Equal(T0 + TimeSpan.FromSeconds(68), module.EndDue);

        module.Tick(T0 + TimeSpan.FromSeconds(68));

        Assert.Null(module.Call);
        Assert.Null(module.EndDue);
        var (ended, notify) = announce.Calls[1];
        Assert.False(notify);
        Assert.Equal("phonedown", ended.Glyph);
        Assert.Equal("#FF453A", ended.Tint);
        Assert.Equal("Ended", ended.Title);
        Assert.Equal("1:00", ended.Subtitle);
        Assert.Equal("callEnd", ended.Event);
        Assert.True(ended.Compact);
        Assert.Equal("calls", ended.Module);
    }

    [Fact]
    public void BackInsideGrace_KeepsTheCall_SinceAndNoToast()
    {
        // The Mac: callEnded is set while the call still exists, so an app back inside the 8 s grace is the same call.
        var announce = new RecordingAnnouncer();
        var module = Make(announce);
        module.Apply(Mic("zoom.exe"), T0);
        module.Apply(Quiet, T0 + TimeSpan.FromSeconds(10));
        Assert.NotNull(module.Call);

        module.Apply(Mic("zoom.exe"), T0 + TimeSpan.FromSeconds(15));  // back 5 s into the grace

        Assert.NotNull(module.Call);
        Assert.Equal(T0, module.Call!.Since);
        Assert.Null(module.EndDue);
        Assert.Single(announce.Calls);  // only the start: no new call toast
    }

    [Fact]
    public void BackAfterEnded_IsANewCall_WithItsOwnToast()
    {
        // After "Ended" the call is gone (Call == null): the app that comes back starts a new call, as on the Mac.
        var announce = new RecordingAnnouncer();
        var module = Make(announce);
        module.Apply(Mic("zoom.exe"), T0);
        module.Apply(Quiet, T0 + TimeSpan.FromSeconds(10));
        module.Tick(T0 + TimeSpan.FromSeconds(18));  // "Ended" here
        Assert.Null(module.Call);

        var back = T0 + TimeSpan.FromSeconds(20);  // end + 2 s
        module.Apply(Mic("zoom.exe"), back);

        Assert.NotNull(module.Call);
        Assert.Equal(back, module.Call!.Since);
        Assert.Equal(3, announce.Calls.Count);
        Assert.Equal("callStart", announce.Calls[2].Toast.Event);
        Assert.Equal("On a call", announce.Calls[2].Toast.Title);
    }

    [Fact]
    public void CallEnding_ReportsItsEndDue_AndClearsWhenTheAppReturns()
    {
        var module = Make(new RecordingAnnouncer());
        module.Apply(Mic("zoom.exe"), T0);
        Assert.Null(module.EndDue);

        module.Apply(Quiet, T0 + TimeSpan.FromSeconds(5));

        Assert.Equal(T0 + TimeSpan.FromSeconds(13), module.EndDue);
        module.Apply(Mic("zoom.exe"), T0 + TimeSpan.FromSeconds(6));  // back before the end: the mark clears
        Assert.Null(module.EndDue);
    }

    [Fact]
    public void Privacy_MicOutsideCall_AnnouncesOnce()
    {
        var announce = new RecordingAnnouncer();
        var module = Make(announce);

        module.Apply(Mic("spotify.exe"), T0);
        module.Apply(Mic("spotify.exe"), T0 + TimeSpan.FromSeconds(1));

        Assert.Null(module.Call);
        var (toast, notify) = Assert.Single(announce.Calls);
        Assert.False(notify);
        Assert.Equal("mic", toast.Glyph);
        Assert.Equal("#FF9F0A", toast.Tint);
        Assert.Equal("Microphone on", toast.Title);
        Assert.Equal("spotify", toast.Subtitle);
        Assert.Equal("sensorOn", toast.Event);
        Assert.True(toast.Compact);
        Assert.Equal("privacy", toast.Module);
    }

    [Fact]
    public void Privacy_CameraOn_Announces()
    {
        var announce = new RecordingAnnouncer();
        var module = Make(announce);

        module.Apply(new SensorState(Array.Empty<string>(), true, false), T0);

        var (toast, _) = Assert.Single(announce.Calls);
        Assert.Equal("camera", toast.Glyph);
        Assert.Equal("#30D158", toast.Tint);
        Assert.Equal("Camera on", toast.Title);
        Assert.Equal("Camera", toast.Subtitle);
        Assert.Equal("sensorOn", toast.Event);
        Assert.Equal("privacy", toast.Module);
    }

    [Fact]
    public void Privacy_CameraAfterMic_SubtitleIsTheMicApp()
    {
        var announce = new RecordingAnnouncer();
        var module = Make(announce);
        module.Apply(Mic("spotify.exe"), T0);
        announce.Calls.Clear();

        module.Apply(new SensorState(new[] { "spotify.exe" }, true, false), T0 + TimeSpan.FromSeconds(1));

        Assert.Equal("Camera on", Assert.Single(announce.Calls).Toast.Title);
        Assert.Equal("spotify", announce.Calls[0].Toast.Subtitle);
    }

    [Fact]
    public void PrivacyOff_NoSensorToasts_CallsStillWork()
    {
        var announce = new RecordingAnnouncer();
        var module = Make(announce, privacy: false);

        module.Apply(new SensorState(new[] { "spotify.exe" }, true, false), T0);
        Assert.Empty(announce.Calls);

        module.Apply(new SensorState(new[] { "zoom.exe" }, true, false), T0 + TimeSpan.FromSeconds(1));
        Assert.Equal("callStart", Assert.Single(announce.Calls).Toast.Event);
        Assert.Equal("zoom.exe", module.Call!.App);
    }

    [Fact]
    public void CallsOff_NoCall_ButPrivacyStillSays()
    {
        var announce = new RecordingAnnouncer();
        var module = Make(announce, calls: false);

        module.Apply(Mic("zoom.exe"), T0);

        Assert.Null(module.Call);
        Assert.Equal("Microphone on", Assert.Single(announce.Calls).Toast.Title);
    }

    [Fact]
    public void Mute_Success_SetsCallAndSensors()
    {
        var announce = new RecordingAnnouncer();
        var module = Make(announce);
        module.Apply(Mic("zoom.exe"), T0);

        var ok = module.ToggleMute(_ => true);

        Assert.True(ok);
        Assert.True(module.Call!.Muted);
        Assert.True(module.Sensors.MicMuted);
        Assert.Single(announce.Calls);  // only the start
    }

    [Fact]
    public void Mute_Failure_AnnouncesCouldntMute()
    {
        var announce = new RecordingAnnouncer();
        var module = Make(announce);
        module.Apply(Mic("zoom.exe"), T0);

        var ok = module.ToggleMute(_ => false);

        Assert.False(ok);
        Assert.False(module.Call!.Muted);
        var (toast, notify) = announce.Calls[1];
        Assert.False(notify);
        Assert.Equal("micoff", toast.Glyph);
        Assert.Equal("Couldn't mute", toast.Title);
        Assert.Equal("Use the button in Zoom", toast.Subtitle);
        Assert.Null(toast.Event);
        Assert.True(toast.Compact);
        Assert.Equal("calls", toast.Module);
    }

    [Fact]
    public void Mute_NoCall_DoesNothing()
    {
        var module = Make(new RecordingAnnouncer());

        Assert.False(module.ToggleMute(_ => throw new InvalidOperationException("no call: nothing to mute")));
    }

    [Fact]
    public void Reset_ForgetsTheCall()
    {
        var module = Make(new RecordingAnnouncer());
        module.Apply(Mic("zoom.exe"), T0);

        module.Reset();

        Assert.Null(module.Call);
        Assert.Equal(Quiet, module.Sensors);
    }

    [Fact]
    public void CallApp_DedicatedBeatsBrowser()
    {
        Assert.Equal("zoom.exe", CallApps.CallApp(new[] { "chrome.exe", "zoom.exe" }));
        Assert.Equal("chrome.exe", CallApps.CallApp(new[] { "chrome.exe", "unknown.exe" }));
        Assert.Null(CallApps.CallApp(new[] { "unknown.exe" }));
    }

    [Fact]
    public void CallApps_NameFallsBackToExeName()
    {
        Assert.Equal("Teams", CallApps.Name("msteams_8wekyb3d8bbwe"));
        Assert.Equal("Edge", CallApps.Name("msedge.exe"));
        Assert.Equal("spotify", CallApps.Name("spotify.exe"));
        Assert.Equal("Windowscamera", CallApps.Name("microsoft.windowscamera_8wekyb3d8bbwe"));
        Assert.Equal("Spotify", CallApps.Name("spotify"));
    }
}
