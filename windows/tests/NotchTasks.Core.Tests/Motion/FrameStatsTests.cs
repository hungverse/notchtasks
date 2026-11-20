using NotchTasks.Core.Motion;
using Xunit;

namespace NotchTasks.Core.Tests.Motion;

public sealed class FrameStatsTests
{
    [Fact]
    public void Summarize_Empty_AllZero()
    {
        var summary = FrameStats.Summarize(Array.Empty<double>(), 16.7);

        Assert.Equal(new FrameSummary(0, 0, 0, 0, 0), summary);
    }

    [Fact]
    public void Summarize_Steady60Hz_NoLong()
    {
        var intervals = Enumerable.Repeat(16.7, 60).ToArray();

        var summary = FrameStats.Summarize(intervals, 16.7);

        Assert.Equal(60, summary.Frames);
        Assert.Equal(16.7, summary.P50, precision: 6);
        Assert.Equal(0, summary.Long);
    }

    [Fact]
    public void Summarize_TwoHitches_CountsLong()
    {
        var intervals = Enumerable.Repeat(16.7, 10).Concat(new[] { 40.0, 50.0 }).ToArray();

        var summary = FrameStats.Summarize(intervals, 16.7);

        Assert.Equal(12, summary.Frames);
        Assert.Equal(2, summary.Long);
        Assert.Equal(50, summary.Max, precision: 6);
        Assert.Equal(50, summary.P95, precision: 6);  // nearest-rank: ceil(0.95 * 12) = 12
    }
}
