using NotchTasks.Core.Demo;
using Xunit;

namespace NotchTasks.Core.Tests.Demo;

/// The scripted demo: the frame at each moment of the loop, and the loop's length and frame count.
public sealed class DemoTimelineTests
{
    private static DemoFrame At(double seconds) => DemoTimeline.At(TimeSpan.FromSeconds(seconds));

    [Fact]
    public void Loop_IsFourteenSeconds_AtTwelveFrames_ARate()
    {
        Assert.Equal(TimeSpan.FromSeconds(14), DemoTimeline.Loop);
        Assert.Equal(12, DemoTimeline.Fps);
        Assert.Equal(168, DemoTimeline.FrameCount);
    }

    [Fact]
    public void ZeroSeconds_IsAClosedPillWithNothingToShow()
    {
        var frame = At(0);

        Assert.False(frame.PillOpen);
        Assert.False(frame.AskPending);
        Assert.False(frame.AllowPressed);
        Assert.Equal(DemoToast.None, frame.Toast);
    }

    [Fact]
    public void OneAndAHalfSeconds_OpensThePill()
    {
        Assert.False(At(1.49).PillOpen);
        Assert.True(At(1.5).PillOpen);
        Assert.True(At(5.9).PillOpen);
        Assert.Equal(DemoToast.None, At(5.9).Toast);
    }

    [Fact]
    public void SixSeconds_ClosesThePill_AndAnAskDrops()
    {
        var frame = At(6);

        Assert.False(frame.PillOpen);
        Assert.True(frame.AskPending);
        Assert.Equal(DemoToast.Ask, frame.Toast);
        Assert.False(frame.AllowPressed);
    }

    [Fact]
    public void NineSeconds_PressesAllow_AndTheDoneToastFollowsAfterTheHold()
    {
        Assert.False(At(8.99).AllowPressed);

        var pressed = At(9);
        Assert.True(pressed.AllowPressed);
        Assert.True(pressed.AskPending);
        Assert.Equal(DemoToast.Ask, pressed.Toast);

        var done = At(9.4);
        Assert.False(done.AllowPressed);
        Assert.False(done.AskPending);
        Assert.Equal(DemoToast.Pushed, done.Toast);
    }

    [Fact]
    public void ElevenSeconds_ShowsTheDeviceToast_WithTheClosedPill()
    {
        var frame = At(11);

        Assert.False(frame.PillOpen);
        Assert.False(frame.AskPending);
        Assert.Equal(DemoToast.Charge, frame.Toast);
    }

    [Fact]
    public void TheLoop_RestartsAtFourteenSeconds()
    {
        Assert.Equal(At(0), At(14));
        Assert.Equal(At(1.5), At(15.5));
        Assert.Equal(At(9), DemoTimeline.At(TimeSpan.FromSeconds(23)));
        Assert.Equal(At(0), DemoTimeline.At(TimeSpan.FromSeconds(-14)));
    }
}
