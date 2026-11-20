using NotchTasks.Core.Media;
using Xunit;

namespace NotchTasks.Core.Tests.Media;

/// Update reports a change only when what the card shows changed: a steady play, read again, is not a change (the position is
/// extrapolated from the old reading), while a metadata, state or position change (more than 1.5 s off) is.
public class MediaModuleChangeTests
{
    private static MediaSessionFact Playing(double position, long updatedAtMs, MediaStatus status = MediaStatus.Playing) =>
        new("A.exe", "Song", "Artist", status, position, 100, updatedAtMs, true, true, true, true, "art", IsCurrent: true);

    [Fact]
    public void SteadyPlaying_TwoReads_NotAChange()
    {
        var module = new MediaModule();
        Assert.True(module.Update([Playing(10, 1000)], 1000));

        // One second later the reading agrees with the extrapolation (11 s): nothing the card shows changed.
        Assert.False(module.Update([Playing(11, 2000)], 2000));
    }

    [Fact]
    public void UnchangedRead_KeepsTheShownReading()
    {
        var module = new MediaModule();
        module.Update([Playing(10, 1000)], 1000);
        var shown = module.Current;

        module.Update([Playing(11, 2000)], 2000);

        Assert.Same(shown, module.Current);
        Assert.Equal(1000, module.Current!.PositionAtMs);
    }

    [Fact]
    public void PositionJumpOfFiveSeconds_IsAChange()
    {
        var module = new MediaModule();
        module.Update([Playing(10, 1000)], 1000);

        // Extrapolated to 11 s at 2000 ms; the reading is 16 s.
        Assert.True(module.Update([Playing(16, 2000)], 2000));
        Assert.Equal(16, module.Current!.Position);
    }

    [Fact]
    public void Pause_IsAChange()
    {
        var module = new MediaModule();
        module.Update([Playing(10, 1000)], 1000);

        Assert.True(module.Update([Playing(11, 2000, MediaStatus.Paused)], 2000));
        Assert.False(module.Current!.Playing);
    }

    [Fact]
    public void TitleOrArtworkChange_IsAChange()
    {
        var module = new MediaModule();
        module.Update([Playing(10, 1000)], 1000);

        var renamed = Playing(11, 2000) with { Title = "Other" };
        Assert.True(module.Update([renamed], 2000));

        var art = renamed with { ArtworkKey = "new" };
        Assert.True(module.Update([art], 2000));
    }
}
