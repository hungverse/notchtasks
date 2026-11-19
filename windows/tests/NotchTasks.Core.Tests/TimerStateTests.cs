using NotchTasks.Core.Notch;
using Xunit;

namespace NotchTasks.Core.Tests;

public class TimerStateTests
{
    [Theory]
    [InlineData(0.0, "0:00")]
    [InlineData(-5.0, "0:00")]
    [InlineData(0.2, "0:01")]
    [InlineData(1.0, "0:01")]
    [InlineData(59.0, "0:59")]
    [InlineData(60.0, "1:00")]
    [InlineData(299.2, "5:00")]
    [InlineData(599.0, "9:59")]
    [InlineData(3599.5, "1:00:00")]
    [InlineData(3600.0, "1:00:00")]
    [InlineData(3725.0, "1:02:05")]
    public void Mmss_RoundsUpToTheWholeSecond(double seconds, string expected)
    {
        Assert.Equal(expected, TimerState.Mmss(TimeSpan.FromSeconds(seconds)));
    }
}
