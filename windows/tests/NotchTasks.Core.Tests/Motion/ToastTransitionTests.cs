using NotchTasks.Core.Motion;
using Xunit;

namespace NotchTasks.Core.Tests.Motion;

/// The timing of a toast change: the old content fades out in 80 ms with the shape starting 40 ms in, the new content rises as the
/// shape passes 75% of its way, and a shape larger than the closed pill is never left without content for more than a frame.
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
        Assert.Equal(0.75, ToastTransition.RisesAt);
    }

    [Theory]
    [InlineData("smooth", true)]
    [InlineData("smooth", false)]
    [InlineData("bouncy", true)]
    [InlineData("bouncy", false)]
    [InlineData("snappy", true)]
    [InlineData("snappy", false)]
    public void TheNewContentRisesAfterTheOldIsGone_ForEveryMotion(string motion, bool opening)
    {
        var curve = SpringCurve.For(motion, opening, animationsOn: true);
        var rise = ToastTransition.MorphLead + ToastTransition.RiseTime(curve);

        // The new content starts once the old is gone (its rise is at or after OldOut), so the two are never both visible.
        Assert.True(rise >= ToastTransition.OldOut, $"{motion} opening={opening}: the new rises at {rise.TotalMilliseconds:0} ms, before the old is gone");
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
