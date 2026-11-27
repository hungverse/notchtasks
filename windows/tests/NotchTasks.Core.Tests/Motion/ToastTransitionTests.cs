using NotchTasks.Core.Motion;
using Xunit;

namespace NotchTasks.Core.Tests.Motion;

/// The timing of a toast change: the old content fades out in 80 ms with the shape starting 40 ms in, the new content rises as the
/// shape is 45% of its way (or the old content is gone, whichever is later), and a shape larger than the closed pill is never left
/// without content for more than a frame.
public sealed class ToastTransitionTests
{
    [Fact]
    public void ToastToToast_MorphsStraightToTheNewSize_NoClosedPillBetween()
    {
        Assert.Equal(
            new[] { ToastStep.FadeOutOld, ToastStep.Morph, ToastStep.ShowNew, ToastStep.FadeInNew },
            ToastTransition.Plan(oldShown: true, newShown: true));
    }

    [Fact]
    public void ToastToClosed_FadesTheOldOut_WhileTheShapeMoves()
    {
        Assert.Equal(new[] { ToastStep.FadeOutOld, ToastStep.Morph }, ToastTransition.Plan(oldShown: true, newShown: false));
    }

    [Fact]
    public void ClosedToToast_MorphsAndTheContentRisesWithIt()
    {
        Assert.Equal(new[] { ToastStep.Morph, ToastStep.ShowNew, ToastStep.FadeInNew }, ToastTransition.Plan(oldShown: false, newShown: true));
    }

    [Fact]
    public void NothingToChange_HasNoSteps()
    {
        Assert.Empty(ToastTransition.Plan(oldShown: false, newShown: false));
    }

    [Fact]
    public void Timing_TheShapeMovesInsideTheOldFade_AndTheOldIsGoneBeforeTheNewShows()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(80), ToastTransition.OldOut);
        Assert.Equal(TimeSpan.FromMilliseconds(40), ToastTransition.MorphLead);
        Assert.Equal(TimeSpan.FromMilliseconds(120), ToastTransition.NewIn);
        Assert.True(ToastTransition.MorphLead < ToastTransition.OldOut);
        Assert.Equal(0.45, ToastTransition.RisesAt);
    }

    [Theory]
    [InlineData("smooth", true)]
    [InlineData("smooth", false)]
    [InlineData("bouncy", true)]
    [InlineData("bouncy", false)]
    [InlineData("snappy", true)]
    [InlineData("snappy", false)]
    public void TheNewContentRisesAtTheLaterOfOldGoneAndTheSpringAt45Percent_ForEveryMotion(string motion, bool opening)
    {
        var curve = SpringCurve.For(motion, opening, animationsOn: true);
        var springAt45 = ToastTransition.MorphLead + ToastTransition.RiseTime(curve);
        var later = springAt45 > ToastTransition.OldOut ? springAt45 : ToastTransition.OldOut;

        // The new content's rise is the later of the two, so it is never visible while the old content still is.
        Assert.Equal(later, ToastTransition.OldOut + ToastTransition.RisePause(curve, ToastTransition.MorphLead, ToastTransition.OldOut));
        Assert.True(later >= ToastTransition.OldOut);
    }

    [Fact]
    public void RisePause_WaitsForTheRisePoint_OrIsZeroWhenItHasPassed()
    {
        var curve = SpringCurve.For("bouncy", opening: true, animationsOn: true);
        var rise = ToastTransition.RiseTime(curve);

        Assert.Equal(rise, ToastTransition.RisePause(curve, TimeSpan.Zero, TimeSpan.Zero));
        Assert.Equal(TimeSpan.Zero, ToastTransition.RisePause(curve, TimeSpan.Zero, rise + TimeSpan.FromSeconds(1)));
        Assert.Equal(ToastTransition.MorphLead + rise - ToastTransition.OldOut,
            ToastTransition.RisePause(curve, ToastTransition.MorphLead, ToastTransition.OldOut));
    }

    [Fact]
    public void RiseTime_IsWhenTheSpringFirstReachesTheRisePoint()
    {
        var curve = SpringCurve.For("bouncy", opening: true, animationsOn: true);
        var rise = ToastTransition.RiseTime(curve);

        Assert.True(curve.At(rise.TotalSeconds) >= ToastTransition.RisesAt);
        Assert.True(curve.At(rise.TotalSeconds - 0.002) < ToastTransition.RisesAt);
    }
}
