using NotchTasks.Core.Motion;
using Xunit;

namespace NotchTasks.Core.Tests.Motion;

public sealed class PillGeometryTests
{
    [Fact]
    public void LayoutWidth_IsTheWiderOfOpenAndAskToast()
    {
        Assert.Equal(Math.Max(PillGeometry.OpenWidth, PillGeometry.AskToastWidth), PillGeometry.LayoutWidth);
        Assert.Equal(520, PillGeometry.LayoutWidth);
    }

    [Fact]
    public void ShapeRect_CentredInLayout()
    {
        // The closed pill (200 wide) is centred in the 520 layout; the open pill fills it.
        Assert.Equal((160.0, 0.0, 200.0, 32.0), PillGeometry.ShapeRect(200, 32));
        Assert.Equal((0.0, 0.0, 520.0, 400.0), PillGeometry.ShapeRect(520, 400));
    }

    [Fact]
    public void WingOffset_ClosedAndOpen()
    {
        // The left wing moves right by the offset and the right wing moves left by it.
        Assert.Equal(160.0, PillGeometry.WingOffset(200));
        Assert.Equal(0.0, PillGeometry.WingOffset(520));
    }

    [Theory]
    [InlineData("smooth")]
    [InlineData("snappy")]
    [InlineData("bouncy")]
    public void HostMargin_HoldsTheGlowAndTheSpringOvershootOfEveryOpen(string motion)
    {
        // The open overshoots its target by the curve's peak, on the width and the height. The largest grow is from the smallest
        // closed shape (ClosedHeight) to the layout's width, or to the open height from the same circle.
        var curve = SpringCurve.For(motion, opening: true, animationsOn: true);
        var peak = 0.0;
        for (var t = 0.0; t <= curve.Settle; t += 0.001)
        {
            peak = Math.Max(peak, curve.At(t) - 1);
        }
        var widthGrow = PillGeometry.LayoutWidth - PillGeometry.ClosedHeight;
        var heightGrow = PillGeometry.MaxOpenHeight - PillGeometry.ClosedHeight;
        var needed = peak * Math.Max(widthGrow, heightGrow) + PillGeometry.GlowReach + 1.2;  // the ring's half stroke

        Assert.True(PillGeometry.HostMargin >= needed, $"{motion}: the margin {PillGeometry.HostMargin} is short of {needed:0.0}");
        Assert.Equal(PillGeometry.LayoutWidth + 2 * PillGeometry.HostMargin, PillGeometry.WindowWidth);
        Assert.Equal(PillGeometry.MaxOpenHeight + PillGeometry.HostMargin, PillGeometry.WindowHeight);
    }
}
