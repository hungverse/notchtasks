using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NotchTasks.Pill;
using Xunit;
using Path = System.Windows.Shapes.Path;

namespace NotchTasks.AppTests;

/// A badge naming a Mac symbol with a drawn mark draws that mark, not the star (P3c M11).
public sealed class BadgeGlyphTests
{
    private static JsonObject Badge(string symbol) =>
        new() { ["symbol"] = symbol, ["color"] = "#30D158" };

    [Fact]
    public void HammerSymbol_DrawsTheHammerMark_NotTheStar()
    {
        Sta.Run(() =>
        {
            var glyph = Assert.IsType<Grid>(BadgeGlyph.Create(Badge("hammer.fill"), 18, Colors.Gray));

            // children: the fill, then the icon
            var icon = glyph.Children[1];
            Assert.IsType<Path>(icon);
        });
    }

    [Fact]
    public void UnknownSymbol_DrawsTheStar()
    {
        Sta.Run(() =>
        {
            var glyph = Assert.IsType<Grid>(BadgeGlyph.Create(Badge("no.such.symbol"), 18, Colors.Gray));

            var text = Assert.IsType<TextBlock>(glyph.Children[1]);
            Assert.Equal(new Rune(Convert.ToInt32(BadgeGlyph.StarCode, 16)).ToString(), text.Text);
        });
    }
}
