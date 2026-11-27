using NotchTasks.Core.Demo;
using Xunit;

namespace NotchTasks.Core.Tests.Demo;

/// The scripted demo: the frame at each moment of the loop, and the loop's length and frame count.
public sealed class DemoTimelineTests
{
    private static DemoFrame At(double seconds) => DemoTimeline.At(TimeSpan.FromSeconds(seconds));

    [Fact]
    public void Loop_IsSixteenSeconds_AtFifteenFrames_AFrameCountOf240()
    {
        Assert.Equal(TimeSpan.FromSeconds(16), DemoTimeline.Loop);
        Assert.Equal(15, DemoTimeline.Fps);
        Assert.Equal(240, DemoTimeline.FrameCount);
    }

    [Fact]
    public void ZeroSeconds_IsTheClosedPill_WithTheBuildRunning()
    {
        var frame = At(0);

        Assert.False(frame.AskPending);
        Assert.False(frame.AllowPressed);
        Assert.Equal(DemoToast.None, frame.Toast);
        Assert.False(frame.BuildDone);
    }

    [Fact]
    public void TwoSeconds_GrowsTheAskToast_AndItWaits()
    {
        Assert.Equal(DemoToast.None, At(1.99).Toast);
        Assert.Equal(DemoToast.Ask, At(2).Toast);
        Assert.True(At(2).AskPending);
        Assert.False(At(2).AllowPressed);
    }

    [Fact]
    public void FiveSeconds_HoldsAllowForFourTenths_ThenTheAskLeaves()
    {
        Assert.False(At(4.99).AllowPressed);
        Assert.True(At(5).AllowPressed);
        Assert.True(At(5).AskPending);
        Assert.True(At(5.39).AllowPressed);

        // The ask's toast morphs straight into the done toast: the done toast is up at once, with no closed pill between them.
        var after = At(5.4);
        Assert.False(after.AllowPressed);
        Assert.False(after.AskPending);
        Assert.Equal(DemoToast.Pushed, after.Toast);
    }

    [Fact]
    public void TheDoneToast_FollowsTheAskWithNoClosedFrame()
    {
        Assert.Equal(DemoToast.Ask, At(5.39).Toast);
        Assert.Equal(DemoToast.Pushed, At(5.4).Toast);
        Assert.Equal(DemoToast.Pushed, At(6).Toast);
        Assert.False(At(6).AskPending);
    }

    [Fact]
    public void EightSeconds_ShrinksTheDoneToast_AndTheBuildIsDone()
    {
        Assert.Equal(DemoToast.Pushed, At(7.99).Toast);
        Assert.False(At(7.99).BuildDone);

        var closed = At(8);
        Assert.Equal(DemoToast.None, closed.Toast);
        Assert.True(closed.BuildDone);
    }

    [Fact]
    public void TenSeconds_GrowsTheDeviceToast_ThenItShrinksAtThirteen()
    {
        Assert.Equal(DemoToast.None, At(9.99).Toast);
        Assert.Equal(DemoToast.Charge, At(10).Toast);
        Assert.Equal(DemoToast.Charge, At(12.99).Toast);
        Assert.Equal(DemoToast.None, At(13).Toast);
    }

    [Fact]
    public void EveryFrame_ShowsAtMostOneToast_AndNoCardStack()
    {
        // DemoFrame has no pill-open state at all: the card stack never opens. One toast at a time is the Toast value.
        for (var i = 0; i < DemoTimeline.FrameCount; i++)
        {
            var frame = DemoTimeline.At(TimeSpan.FromSeconds(i / (double)DemoTimeline.Fps));
            Assert.InRange((int)frame.Toast, 0, 3);
            Assert.False(frame.AllowPressed && frame.Toast != DemoToast.Ask);
        }
    }

    [Fact]
    public void TheLoop_RestartsAtSixteenSeconds()
    {
        Assert.Equal(At(0), At(16));
        Assert.Equal(At(2), At(18));
        Assert.Equal(At(5.2), DemoTimeline.At(TimeSpan.FromSeconds(21.2)));
        Assert.Equal(At(0), DemoTimeline.At(TimeSpan.FromSeconds(-16)));
    }
}
