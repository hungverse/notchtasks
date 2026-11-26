using System.Windows;
using System.Windows.Media;
using Xunit;

namespace NotchTasks.AppTests;

/// FluentGlyph draws a glyph's outline centred in its box, so an icon sits in the optical centre of a tile or a badge (the
/// baseline of a TextBlock does not).
public sealed class FluentGlyphTests
{
    // a few glyphs of both families: the Regular wifi, the Regular more, the Regular timer and the Filled arrow-circle-down
    private static readonly (string Code, bool Filled)[] Samples =
    {
        ("", false),
        ("", false),
        ("", false),
        ("", true),
    };

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Outline_IsCentredInItsBox_WithinHalfADip(int index)
    {
        var (code, filled) = Samples[index];
        const double box = 16;
        Sta.Run(() =>
        {
            var outline = FluentGlyph.CentredOutline(code, filled, box);

            Assert.NotNull(outline);
            var bounds = outline!.Bounds;
            Assert.InRange(bounds.X + bounds.Width / 2, box / 2 - 0.5, box / 2 + 0.5);
            Assert.InRange(bounds.Y + bounds.Height / 2, box / 2 - 0.5, box / 2 + 0.5);
        });
    }

    [Fact]
    public void Control_MeasuresToItsSize_AndFillsWithItsForeground()
    {
        Sta.Run(() =>
        {
            var glyph = new FluentGlyph { Code = "", Size = 12, Foreground = Brushes.Red };
            glyph.Measure(new Size(100, 100));

            Assert.Equal(new Size(12, 12), glyph.DesiredSize);
            Assert.NotNull(glyph.Outline);
            Assert.Same(Brushes.Red, glyph.Foreground);
        });
    }

    [Fact]
    public void Control_IsWhiteByDefault_AndAnEmptyCodeDrawsNothing()
    {
        Sta.Run(() =>
        {
            var glyph = new FluentGlyph { Code = "", Size = 12 };
            Assert.Equal(Brushes.White, glyph.Foreground);

            var empty = new FluentGlyph { Size = 12 };
            Assert.Null(empty.Outline);
        });
    }

    [Fact]
    public void Outline_IsCachedPerCodeFamilyAndSize()
    {
        Sta.Run(() =>
        {
            var first = FluentGlyph.CentredOutline("", false, 14);
            Assert.Same(first, FluentGlyph.CentredOutline("", false, 14));
            Assert.NotSame(first, FluentGlyph.CentredOutline("", false, 15));
        });
    }
}
