using NotchTasks.Core.Motion;
using Xunit;

namespace NotchTasks.Core.Tests.Motion;

public sealed class PillGeometryTests
{
    [Fact]
    public void LayoutWidth_IsTheWiderOfOpenAndAskToast_WithTheirPeakOvershootAndTheGlow()
    {
        var widest = Math.Max(PillGeometry.OpenWidth, PillGeometry.AskToastWidth);
        Assert.Equal(520, widest);
        Assert.True(PillGeometry.LayoutWidth >= widest * (1 + PillGeometry.PeakOvershoot) + 2 * PillGeometry.GlowReach,
            $"the layout is {PillGeometry.LayoutWidth} wide, short of the widest shape at its peak");
    }

    [Theory]
    [InlineData("smooth")]
    [InlineData("bouncy")]
    [InlineData("snappy")]
    [InlineData("reduced")]
    public void WindowCoversEveryShapeAtItsPeakOvershoot_OnEverySide(string motion)
    {
        // Each motion, opening and closing: its peak over the target is within PeakOvershoot, so the layout holds it.
        foreach (var opening in new[] { true, false })
        {
            var curve = SpringCurve.For(motion, opening, animationsOn: true);
            Assert.True(curve.Peak - 1 <= PillGeometry.PeakOvershoot + 1e-9, $"{motion} opening={opening} overshoots {curve.Peak - 1:0.000}");
        }

        var widest = Math.Max(PillGeometry.OpenWidth, PillGeometry.AskToastWidth);
        var reach = PillGeometry.GlowReach + 1.2;  // the glow's blur and the ring's half stroke
        var overshootSide = widest * PillGeometry.PeakOvershoot / 2;
        // Sides: the layout's room beyond the widest shape holds its overshoot, and the window's margin holds the glow.
        Assert.True((PillGeometry.LayoutWidth - widest) / 2 >= overshootSide);
        Assert.True((PillGeometry.WindowWidth - widest) / 2 >= overshootSide + reach);
        // Top: the room above the shape holds the glow. Bottom: the layout under the tallest shape holds its overshoot, and the margin the glow.
        Assert.True(PillGeometry.TopRoom >= reach);
        var overshootDown = PillGeometry.MaxOpenHeight * PillGeometry.PeakOvershoot;
        Assert.True(PillGeometry.LayoutHeight - PillGeometry.MaxOpenHeight >= overshootDown);
        Assert.True(PillGeometry.WindowHeight - PillGeometry.TopRoom - PillGeometry.MaxOpenHeight >= overshootDown + reach);
    }

    [Fact]
    public void ShapeRect_CentredInLayout()
    {
        // The closed pill (200 wide) is centred in the layout; a shape as wide as the widest toast sits at the layout's edge margin.
        var centred = (PillGeometry.LayoutWidth - 200) / 2;
        Assert.Equal((centred, 0.0, 200.0, 32.0), PillGeometry.ShapeRect(200, 32));
        Assert.Equal(((PillGeometry.LayoutWidth - 520) / 2, 0.0, 520.0, 400.0), PillGeometry.ShapeRect(520, 400));
    }

    [Fact]
    public void WingOffset_ClosedAndOpen()
    {
        // The left wing moves right by the offset and the right wing moves left by it: the layout's half of what the shape leaves.
        Assert.Equal((PillGeometry.LayoutWidth - 200) / 2, PillGeometry.WingOffset(200));
        Assert.Equal((PillGeometry.LayoutWidth - 520) / 2, PillGeometry.WingOffset(520));
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
        // The overshoot is inside the layout now (LayoutWidth, LayoutHeight), so the margin only has to hold the glow and the ring.
        var widthGrow = PillGeometry.LayoutWidth - PillGeometry.ClosedHeight;
        var heightGrow = PillGeometry.MaxOpenHeight - PillGeometry.ClosedHeight;
        var needed = PillGeometry.GlowReach + 1.2;  // the ring's half stroke
        Assert.True(peak <= PillGeometry.PeakOvershoot + 1e-9, $"{motion}: the peak {peak:0.000} is past the layout's room");
        Assert.True(PillGeometry.HostMargin >= needed, $"{motion}: the margin {PillGeometry.HostMargin} is short of {needed:0.0}");
        Assert.True(widthGrow > 0 && heightGrow > 0);
        Assert.Equal(PillGeometry.LayoutWidth + 2 * PillGeometry.HostMargin, PillGeometry.WindowWidth);
        Assert.Equal(PillGeometry.TopRoom + PillGeometry.LayoutHeight + PillGeometry.HostMargin, PillGeometry.WindowHeight);
    }
}
