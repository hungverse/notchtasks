using NotchTasks.Core.Motion;
using Xunit;

namespace NotchTasks.Core.Tests;

public sealed class SpringCurveTests
{
    [Fact]
    public void Bouncy_Overshoots_AndSettlesAt1()
    {
        var curve = SpringCurve.For("bouncy", opening: true, animationsOn: true);

        var max = 0.0;
        for (var t = 0.0; t <= curve.Settle; t += 0.001)
        {
            max = Math.Max(max, curve.At(t));
        }

        Assert.True(max > 1.0, $"expected overshoot, max was {max}");
        Assert.InRange(curve.At(curve.Settle), 0.999, 1.001);
    }

    [Fact]
    public void CriticallyDamped_NeverOvershoots()
    {
        var curve = SpringCurve.For("bouncy", opening: false, animationsOn: true);

        var max = 0.0;
        for (var t = 0.0; t <= curve.Settle * 1.5; t += 0.001)
        {
            max = Math.Max(max, curve.At(t));
        }

        Assert.True(max <= 1.0 + 1e-9, $"critically damped overshot to {max}");
        Assert.InRange(curve.At(curve.Settle), 0.999, 1.001);
    }

    [Fact]
    public void Reduced_Is015s()
    {
        Assert.Equal(0.15, SpringCurve.For("reduced", opening: true, animationsOn: true).Settle, 6);
        Assert.Equal(0.15, SpringCurve.For("reduced", opening: false, animationsOn: true).Settle, 6);
        Assert.Equal(0.15, SpringCurve.For("bouncy", opening: true, animationsOn: false).Settle, 6);
    }
}
