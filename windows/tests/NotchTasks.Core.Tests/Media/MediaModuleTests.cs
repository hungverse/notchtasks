using NotchTasks.Core.Media;
using Xunit;

namespace NotchTasks.Core.Tests.Media;

/// MediaModule ports the Mac's refreshNowPlaying: which SMTC session is shown, its extrapolated position, and the optimistic toggle.
public class MediaModuleTests
{
    private static MediaSessionFact Session(
        string source,
        MediaStatus status,
        bool current = false,
        string? title = "Song",
        double? position = 10,
        double? duration = 100,
        long updatedAtMs = 0) =>
        new(source, title, "Artist", status, position, duration, updatedAtMs, true, true, true, true, null, current);

    [Fact]
    public void Update_PrefersCurrentPlaying()
    {
        var module = new MediaModule();

        // The first playing session comes first in the list, but the current session is playing too: the current one wins.
        module.Update(
        [
            Session("A.exe", MediaStatus.Playing),
            Session("B.exe", MediaStatus.Playing, current: true),
            Session("C.exe", MediaStatus.Paused, current: false),
        ], 0);

        Assert.NotNull(module.Current);
        Assert.Equal("B.exe", module.Current!.Source);
        Assert.True(module.Current.Playing);
    }

    [Fact]
    public void Update_FirstPlayingWhenCurrentIsNotPlaying()
    {
        var module = new MediaModule();

        module.Update(
        [
            Session("A.exe", MediaStatus.Paused, current: true),
            Session("B.exe", MediaStatus.Playing),
            Session("C.exe", MediaStatus.Playing),
        ], 0);

        Assert.Equal("B.exe", module.Current!.Source);
    }

    [Fact]
    public void Update_FallsBackToLastPausedSource()
    {
        var module = new MediaModule();
        module.Update([Session("A.exe", MediaStatus.Playing, current: true)], 0);

        // A pauses. B is the current session with a title, but A was the last shown source and is still listed as paused.
        module.Update(
        [
            Session("A.exe", MediaStatus.Paused),
            Session("B.exe", MediaStatus.Paused, current: true, title: "Other"),
        ], 1000);

        Assert.Equal("A.exe", module.Current!.Source);
        Assert.False(module.Current.Playing);
    }

    [Fact]
    public void Update_SessionGone_ClearsOrFallsBack()
    {
        var module = new MediaModule();
        module.Update([Session("A.exe", MediaStatus.Playing, current: true)], 0);

        // A is gone and nothing else is listed: cleared, and the change is reported.
        var changed = module.Update([], 1000);
        Assert.True(changed);
        Assert.Null(module.Current);

        // A is gone and B is the current paused session with a title: B is shown.
        module.Update([Session("A.exe", MediaStatus.Playing, current: true)], 2000);
        changed = module.Update([Session("B.exe", MediaStatus.Paused, current: true)], 3000);
        Assert.True(changed);
        Assert.Equal("B.exe", module.Current!.Source);
    }

    [Fact]
    public void Update_SessionGone_NoTitleNotShown()
    {
        var module = new MediaModule();
        module.Update([Session("A.exe", MediaStatus.Playing, current: true)], 0);

        // The only session is paused with a blank title and it was not the last shown source: nothing is shown.
        module.Update([Session("B.exe", MediaStatus.Paused, current: true, title: "  ")], 1000);

        Assert.Null(module.Current);
    }

    [Fact]
    public void Update_OtherStatusIgnored()
    {
        var module = new MediaModule();

        module.Update(
        [
            Session("A.exe", MediaStatus.Other, current: true),
            Session("B.exe", MediaStatus.Playing),
        ], 0);
        Assert.Equal("B.exe", module.Current!.Source);

        module.Update([Session("A.exe", MediaStatus.Other, current: true)], 1000);
        Assert.Null(module.Current);
    }

    [Fact]
    public void Update_ReturnsWhetherCurrentChanged()
    {
        var module = new MediaModule();
        var facts = new[] { Session("A.exe", MediaStatus.Paused, current: true) };

        Assert.True(module.Update(facts, 0));
        Assert.False(module.Update(facts, 0));
    }

    [Fact]
    public void Update_CopiesMetadataAndAppNames()
    {
        var module = new MediaModule();

        module.Update(
        [
            new MediaSessionFact(
                "Spotify.exe", "Title", "Band", MediaStatus.Playing, 5, 200, 0,
                CanSeek: false, CanNext: true, CanPrevious: false, CanToggle: true,
                ArtworkKey: "art-1", IsCurrent: true),
        ], 0);

        var now = module.Current!;
        Assert.Equal("Spotify", now.AppName);
        Assert.Equal("spotify.exe", now.AppExe);
        Assert.Equal("Title", now.Title);
        Assert.Equal("Band", now.Artist);
        Assert.False(now.CanSeek);
        Assert.True(now.CanNext);
        Assert.False(now.CanPrevious);
        Assert.True(now.CanToggle);
        Assert.Equal("art-1", now.ArtworkKey);
    }

    [Fact]
    public void Position_ExtrapolatedWhilePlaying()
    {
        var module = new MediaModule();

        module.Update([Session("A.exe", MediaStatus.Playing, current: true, position: 10, updatedAtMs: 1000)], 4000);

        Assert.Equal(13, module.Current!.Position);
        Assert.Equal(4000, module.Current.PositionAtMs);
    }

    [Fact]
    public void Position_NotExtrapolatedWhilePaused_AndClampedToDuration()
    {
        var module = new MediaModule();

        module.Update([Session("A.exe", MediaStatus.Paused, current: true, position: 10, updatedAtMs: 1000)], 9000);
        Assert.Equal(10, module.Current!.Position);

        module.Update([Session("A.exe", MediaStatus.Playing, current: true, position: 99, duration: 100, updatedAtMs: 0)], 5000);
        Assert.Equal(100, module.Current!.Position);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-5.0)]
    public void Duration_ZeroIsNull(double duration)
    {
        var module = new MediaModule();

        module.Update([Session("A.exe", MediaStatus.Playing, current: true, duration: duration)], 0);

        Assert.Null(module.Current!.Duration);
    }

    [Fact]
    public void SetPlaying_Optimistic_ThenCorrected()
    {
        var module = new MediaModule();
        module.Update([Session("A.exe", MediaStatus.Playing, current: true, position: 10, updatedAtMs: 0)], 0);

        // The optimistic toggle: the position is extrapolated to nowMs with the old state, then the state changes.
        module.SetPlaying(false, 2000);
        Assert.False(module.Current!.Playing);
        Assert.Equal(12, module.Current.Position);
        Assert.Equal(2000, module.Current.PositionAtMs);

        // The next Update corrects it to what SMTC reports (still playing).
        var changed = module.Update([Session("A.exe", MediaStatus.Playing, current: true, position: 10, updatedAtMs: 0)], 3000);
        Assert.True(changed);
        Assert.True(module.Current!.Playing);
        Assert.Equal(13, module.Current.Position);
    }

    [Fact]
    public void SetPlaying_WithoutCurrent_DoesNothing()
    {
        var module = new MediaModule();

        module.SetPlaying(true, 0);

        Assert.Null(module.Current);
    }

    [Fact]
    public void Clear_RemovesCurrent_AndNextUpdateHasNoLastSource()
    {
        var module = new MediaModule();
        module.Update([Session("A.exe", MediaStatus.Playing, current: true)], 0);

        module.Clear();
        Assert.Null(module.Current);

        // After Clear there is no last shown source, so a paused session that is not current is not shown.
        module.Update([Session("A.exe", MediaStatus.Paused)], 1000);
        Assert.Null(module.Current);
    }
}
