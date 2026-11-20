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
}
