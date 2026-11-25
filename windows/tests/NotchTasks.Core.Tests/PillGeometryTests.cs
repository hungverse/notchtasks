using NotchTasks.Core.Motion;
using Xunit;

namespace NotchTasks.Core.Tests;

public sealed class PillGeometryTests
{
    [Fact]
    public void Place_TopCentre_WholePixels()
    {
        // 1920x1032 work area at 125%: the 560x420 DIP window is 700x525 device pixels.
        var (x, y, w, h) = PillGeometry.Place((0, 0, 1920, 1032), 560, 420, 1.25);

        Assert.Equal(700, w);
        Assert.Equal(525, h);
        Assert.Equal((1920 - 700) / 2, x);
        Assert.Equal(5, y); // IslandTop 4 DIP at 125%
    }

    [Fact]
    public void Place_FloatsIslandTopBelowWorkArea()
    {
        var (_, y, _, _) = PillGeometry.Place((0, 0, 1920, 1032), 560, 420, 1.0);

        Assert.Equal(4, y);
    }

    [Fact]
    public void Place_OffsetWorkArea_CentresInsideIt()
    {
        var (x, y, w, h) = PillGeometry.Place((100, 40, 1001, 900), 560, 420, 1.0);

        Assert.Equal(560, w);
        Assert.Equal(420, h);
        Assert.Equal(100 + (1001 - 560) / 2, x);
        Assert.Equal(40 + 4, y);
    }

    [Fact]
    public void OneIcon_FitsACircle()
    {
        // 20 DIP icon: padding 6 each side; the height is the floor, so the width is the circle's 32.
        Assert.Equal(32, PillGeometry.ClosedContentWidth(20, 0, PillGeometry.IconSizeFor("regular"), single: false));
        // compact 16 DIP icon: padding 8 each side, still 32.
        Assert.Equal(32, PillGeometry.ClosedContentWidth(16, 0, PillGeometry.IconSizeFor("compact"), single: false));
        // a lone right-wing icon is the same.
        Assert.Equal(32, PillGeometry.ClosedContentWidth(0, 20, PillGeometry.IconSizeFor("regular"), single: false));
    }

    [Fact]
    public void Wings_TwoWide_GapTen()
    {
        Assert.Equal(102, PillGeometry.ClosedContentWidth(50, 30, 20, single: false));  // 6 + 50 + 10 + 30 + 6
    }

    [Fact]
    public void SingleChip_GapFive()
    {
        Assert.Equal(77, PillGeometry.ClosedContentWidth(20, 40, 20, single: true));  // 6 + 20 + 5 + 40 + 6
    }

    [Fact]
    public void OnlyRightWing_NoGap()
    {
        Assert.Equal(56, PillGeometry.ClosedContentWidth(0, 44, 20, single: false));  // 6 + 44 + 6
    }

    [Fact]
    public void IdleWidth_IsTwoPaddings()
    {
        Assert.Equal(12, PillGeometry.IdleWidth(20));
        Assert.Equal(16, PillGeometry.IdleWidth(16));
        Assert.Equal(PillGeometry.ClosedHeight, PillGeometry.ClosedContentWidth(0, 0, 20, single: false));
    }

    [Fact]
    public void IconSize_CompactIs16_OtherwiseIs20()
    {
        Assert.Equal(16, PillGeometry.IconSizeFor("compact"));
        Assert.Equal(20, PillGeometry.IconSizeFor("regular"));
    }
}
