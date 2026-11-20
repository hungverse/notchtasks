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
}
