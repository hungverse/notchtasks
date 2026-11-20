using System.Windows;
using NotchTasks.Pill;
using Xunit;

namespace NotchTasks.AppTests;

public sealed class PillHitTestTests
{
    // A 200-wide pill whose top is at y = 0, with the 4 DIP island gap above it.
    private const double X = 100, Y = 0, W = 200, Gap = 4;

    [Fact]
    public void InGapAbove_InsideTheBand_IsTrue()
    {
        Assert.True(PillHitTest.InGapAbove(new Point(200, -2), X, Y, W, Gap));
        Assert.True(PillHitTest.InGapAbove(new Point(200, -4), X, Y, W, Gap)); // the screen's top edge
    }

    [Fact]
    public void InGapAbove_LeftOrRightOfThePill_IsFalse()
    {
        Assert.False(PillHitTest.InGapAbove(new Point(99, -2), X, Y, W, Gap));
        Assert.False(PillHitTest.InGapAbove(new Point(301, -2), X, Y, W, Gap));
    }

    [Fact]
    public void InGapAbove_AboveTheBand_IsFalse()
    {
        Assert.False(PillHitTest.InGapAbove(new Point(200, -5), X, Y, W, Gap));
    }

    [Fact]
    public void InGapAbove_InsideThePill_IsFalse()
    {
        Assert.False(PillHitTest.InGapAbove(new Point(200, 0), X, Y, W, Gap));
        Assert.False(PillHitTest.InGapAbove(new Point(200, 10), X, Y, W, Gap));
    }
}
