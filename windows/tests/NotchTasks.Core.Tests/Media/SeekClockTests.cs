using NotchTasks.Core.Media;
using Xunit;

namespace NotchTasks.Core.Tests.Media;

/// SeekClock ports the Mac's SeekBar: a position that runs while playing, ignored when it drifts less than 1.5 s,
/// rebased on a jump or a duration change, and frozen or resumed from the predicted value on a play-state change.
public class SeekClockTests
{
    [Fact]
    public void SeekClock_SmallDrift_Ignored()
    {
        var clock = new SeekClock();
        clock.Report(10, playing: true, duration: 100, nowMs: 0);

        // Predicted 12 at 2000 ms; the report is 0.5 s away: the base stays.
        clock.Report(12.5, playing: true, duration: 100, nowMs: 2000);

        Assert.False(clock.Rebased);
        Assert.Equal(13, clock.At(3000), 6);
    }

    [Fact]
    public void SeekClock_Jump_Rebases()
    {
        var clock = new SeekClock();
        clock.Report(10, playing: true, duration: 100, nowMs: 0);

        // Predicted 11 at 1000 ms; the report is 39 s away: the base moves to the report.
        clock.Report(50, playing: true, duration: 100, nowMs: 1000);

        Assert.True(clock.Rebased);
        Assert.Equal(50, clock.At(1000), 6);
        Assert.Equal(51, clock.At(2000), 6);
    }

    [Fact]
    public void SeekClock_FirstReport_Rebases()
    {
        var clock = new SeekClock();

        clock.Report(30, playing: false, duration: 100, nowMs: 500);

        Assert.True(clock.Rebased);
        Assert.Equal(30, clock.At(9000), 6);
    }

    [Fact]
    public void SeekClock_PauseFreezes()
    {
        var clock = new SeekClock();
        clock.Report(10, playing: true, duration: 100, nowMs: 0);

        // Paused at 2000 ms, where the predicted value is 12: the clock freezes there.
        clock.Report(12, playing: false, duration: 100, nowMs: 2000);

        Assert.Equal(12, clock.At(2000), 6);
        Assert.Equal(12, clock.At(5000), 6);
    }

    [Fact]
    public void SeekClock_ResumeContinues()
    {
        var clock = new SeekClock();
        clock.Report(10, playing: false, duration: 100, nowMs: 0);

        // Resumed at 5000 ms from the frozen value 10: the clock runs from there.
        clock.Report(10, playing: true, duration: 100, nowMs: 5000);

        Assert.Equal(10, clock.At(5000), 6);
        Assert.Equal(12, clock.At(7000), 6);
    }

    [Fact]
    public void SeekClock_DurationChange_Rebases()
    {
        var clock = new SeekClock();
        clock.Report(10, playing: true, duration: 100, nowMs: 0);

        clock.Report(10, playing: true, duration: 200, nowMs: 1000);

        Assert.True(clock.Rebased);
        Assert.Equal(10, clock.At(1000), 6);
        Assert.Equal(11, clock.At(2000), 6);
    }

    [Fact]
    public void SeekClock_ClampsToDuration()
    {
        var clock = new SeekClock();
        clock.Report(95, playing: true, duration: 100, nowMs: 0);

        Assert.Equal(100, clock.At(60_000), 6);
    }

    [Fact]
    public void SeekClock_ClampsToZero()
    {
        var clock = new SeekClock();
        clock.Report(-3, playing: false, duration: 100, nowMs: 0);

        Assert.Equal(0, clock.At(0), 6);
    }
}
