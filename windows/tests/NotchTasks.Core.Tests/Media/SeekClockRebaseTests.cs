using NotchTasks.Core.Media;
using Xunit;

namespace NotchTasks.Core.Tests.Media;

/// A play-state change rebases the seek clock from the reported position when that position is more than 1.5 s from the
/// prediction (a seek made while paused, then resume), and freezes or resumes from the prediction otherwise.
public class SeekClockRebaseTests
{
    [Fact]
    public void SeekWhilePaused_ThenResume_RebasesFromTheReport()
    {
        var clock = new SeekClock();
        clock.Report(10, playing: true, duration: 100, nowMs: 0);
        clock.Report(12, playing: false, duration: 100, nowMs: 2000);  // paused, frozen at 12

        // The user seeks to 50 s while paused; the resume report comes at 3000 ms.
        clock.Report(50, playing: true, duration: 100, nowMs: 3000);

        Assert.True(clock.Rebased);
        Assert.Equal(50, clock.At(3000), 6);
        Assert.Equal(51, clock.At(4000), 6);
    }

    [Fact]
    public void ResumeWithoutSeek_ContinuesFromThePrediction()
    {
        var clock = new SeekClock();
        clock.Report(10, playing: true, duration: 100, nowMs: 0);
        clock.Report(12, playing: false, duration: 100, nowMs: 2000);

        clock.Report(12.2, playing: true, duration: 100, nowMs: 3000);

        Assert.False(clock.Rebased);
        Assert.Equal(13, clock.At(4000), 6);
    }
}
