using NotchTasks.Core.Motion;
using Xunit;

namespace NotchTasks.Core.Tests.Motion;

/// The order of a toast change: the old content leaves completely before the shape moves and the new content rises, so two contents
/// are never visible at once and the shape never shrinks round content still on screen.
public sealed class ToastTransitionTests
{
    [Fact]
    public void ToastToToast_FadesTheOldOut_ThenMorphs_ThenFadesTheNewIn()
    {
        Assert.Equal(
            new[] { ToastStep.FadeOutOld, ToastStep.ShowNew, ToastStep.Morph, ToastStep.FadeInNew },
            ToastTransition.Plan(oldShown: true, newShown: true));
    }

    [Fact]
    public void ToastToClosed_FadesTheOldOut_BeforeTheShapeShrinks()
    {
        Assert.Equal(new[] { ToastStep.FadeOutOld, ToastStep.Morph }, ToastTransition.Plan(oldShown: true, newShown: false));
    }

    [Fact]
    public void ClosedToToast_LaysTheNewOut_MorphsThenFadesItIn()
    {
        Assert.Equal(new[] { ToastStep.ShowNew, ToastStep.Morph, ToastStep.FadeInNew }, ToastTransition.Plan(oldShown: false, newShown: true));
    }

    [Fact]
    public void NothingToChange_HasNoSteps()
    {
        Assert.Empty(ToastTransition.Plan(oldShown: false, newShown: false));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void OldContentIsGoneBeforeNewContentRises_AndNewlyShownContentIsLaidOutHidden(bool oldShown, bool newShown)
    {
        var plan = ToastTransition.Plan(oldShown, newShown);
        if (oldShown)
        {
            Assert.Equal(ToastStep.FadeOutOld, plan[0]);
        }
        var fadeIn = plan.ToList().IndexOf(ToastStep.FadeInNew);
        if (fadeIn >= 0)
        {
            // The new content's opacity stays 0 until the old one has faded out and the shape has moved.
            Assert.True(plan.ToList().IndexOf(ToastStep.Morph) < fadeIn);
            Assert.True(plan.ToList().IndexOf(ToastStep.ShowNew) < fadeIn);
        }
        if (oldShown && newShown)
        {
            Assert.True(plan.ToList().IndexOf(ToastStep.FadeOutOld) < plan.ToList().IndexOf(ToastStep.ShowNew));
        }
    }
}
