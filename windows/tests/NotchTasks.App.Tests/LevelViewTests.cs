using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using NotchTasks.Core.Levels;
using NotchTasks.Pill;
using Xunit;

namespace NotchTasks.AppTests;

/// The volume and brightness bar's look (the Mac's levelView): the symbol of the level, the fill at the value, and the percentage, or "Off"
/// with an empty fill when muted.
public sealed class LevelViewTests
{
    [Fact]
    public void Volume_ShowsItsSymbol_TheFillAtTheValue_AndThePercentage()
    {
        Sta.Run(() =>
        {
            var view = new LevelView();

            view.Show(new LevelHud(LevelKind.Volume, 0.42, false));

            Assert.Equal("42", Fixture.Named<TextBlock>(view, "PercentText").Text);
            Assert.Equal(ModuleGlyphs.Get("volume.mid").Glyph, Fixture.Named<TextBlock>(view, "IconText").Text);
            Assert.Equal(0.42, Fixture.Field<ColumnDefinition>(view, "FillColumn").Width.Value, 6);
        });
    }

    [Fact]
    public void Muted_ShowsOff_AndAnEmptyFill_WithTheOffSymbol()
    {
        Sta.Run(() =>
        {
            var view = new LevelView();

            view.Show(new LevelHud(LevelKind.Volume, 0.8, true));

            Assert.Equal("Off", Fixture.Named<TextBlock>(view, "PercentText").Text);
            Assert.Equal(ModuleGlyphs.Get("volume.off").Glyph, Fixture.Named<TextBlock>(view, "IconText").Text);
            Assert.Equal(0, Fixture.Field<ColumnDefinition>(view, "FillColumn").Width.Value, 6);
        });
    }

    [Fact]
    public void Brightness_ShowsTheSunSymbol_AndItsPercentage()
    {
        Sta.Run(() =>
        {
            var view = new LevelView();

            view.Show(new LevelHud(LevelKind.Brightness, 0.2));

            Assert.Equal("20", Fixture.Named<TextBlock>(view, "PercentText").Text);
            Assert.Equal(ModuleGlyphs.Get("brightness.low").Glyph, Fixture.Named<TextBlock>(view, "IconText").Text);
        });
    }
}
