using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using NotchTasks.Core.Settings;
using NotchTasks.Pill;
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

    [Fact]
    public void ArrangedInABiggerBox_IsDrawnCentredInThatBox()
    {
        Sta.Run(() =>
        {
            // the glyph is 12 DIP but the element is laid out 40 x 24: the drawn outline is centred in the arranged box
            var glyph = new FluentGlyph { Code = "F8B2", Size = 12, Width = 40, Height = 24 };
            glyph.Measure(new Size(100, 100));
            glyph.Arrange(new Rect(0, 0, 100, 100));

            var drawn = glyph.DrawnBounds;
            Assert.InRange(drawn.X + drawn.Width / 2, 20 - 0.5, 20 + 0.5);
            Assert.InRange(drawn.Y + drawn.Height / 2, 12 - 0.5, 12 + 0.5);
        });
    }

    [Fact]
    public void EveryModuleAndCatalogGlyph_DrawsAtLeastEightDipLongAndThreeShortAt16()
    {
        // A glyph that draws as a dot or a blank is a codepoint the font does not give a shape for: the Network icon was one.
        Sta.Run(() =>
        {
            var failures = new List<string>();
            foreach (var id in ModuleCatalog.All)
            {
                Check(failures, "module " + id, ModuleCatalog.Glyph(id), ModuleCatalog.GlyphFilled(id));
            }
            foreach (var name in ModuleGlyphs.Names)
            {
                Check(failures, "name " + name, ModuleGlyphs.Get(name).Glyph, ModuleGlyphs.IsFilled(name));
            }
            Assert.Empty(failures);
        });
    }

    private static void Check(List<string> failures, string label, string code, bool filled)
    {
        var outline = FluentGlyph.CentredOutline(code, filled, 16);
        var bounds = outline?.Bounds ?? Rect.Empty;
        // a dot or a blank is caught: the longer side must be 8 DIP, the shorter 3 (a thermometer or a phone is narrow, not a dot)
        if (bounds.IsEmpty || Math.Max(bounds.Width, bounds.Height) < 8 || Math.Min(bounds.Width, bounds.Height) < 3)
        {
            var family = filled ? "Filled" : "Regular";
            var point = code.Length > 0 ? char.ConvertToUtf32(code, 0) : 0;
            failures.Add(label + ": U+" + point.ToString("X4") + " (" + family + ") draws " + bounds.Width.ToString("0.0") + "x" + bounds.Height.ToString("0.0") + " DIP");
        }
    }
}
