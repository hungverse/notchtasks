using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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

            var star = Assert.IsType<FluentGlyph>(glyph.Children[1]);
            Assert.Equal(new Rune(Convert.ToInt32(BadgeGlyph.StarCode, 16)).ToString(), star.Code);
        });
    }

    private static JsonObject TextBadge(string text) => new() { ["text"] = text, ["color"] = "#30D158" };

    private static Size Measure(FrameworkElement element)
    {
        element.Measure(new Size(1000, 1000));
        return element.DesiredSize;
    }

    [Fact]
    public void Text_ShowsTheLetters_NotTheStar()
    {
        Sta.Run(() =>
        {
            var glyph = Assert.IsType<Grid>(BadgeGlyph.Create(TextBadge("AB"), 18, Colors.Gray));

            var text = Assert.IsType<TextBlock>(glyph.Children[1]);
            Assert.Equal("AB", text.Text);
        });
    }

    [Fact]
    public void Text_OfTwoLetters_IsTheBadgeSize_AndMoreWidensIntoAPill()
    {
        Sta.Run(() =>
        {
            var two = Measure(BadgeGlyph.Create(TextBadge("AB"), 18, Colors.Gray));
            var three = Measure(BadgeGlyph.Create(TextBadge("ABC"), 18, Colors.Gray));

            Assert.Equal(18, two.Width, precision: 6);
            Assert.Equal(18, three.Height, precision: 6);
            Assert.True(three.Width > 18, "three letters widen the badge");
        });
    }

    [Fact]
    public async Task Picture_FillsTheBadge_AtTwiceTheSize_AndWinsOverText()
    {
        using var root = new TempRoot();
        var picture = BadgeMediaTests.WritePng(root.Folder, "pic.png", 400, 300);
        await BadgeMedia.StillAsync(picture, 18);  // decoded on the background thread before the badge is drawn

        Sta.Run(() =>
        {
            var badge = new JsonObject { ["image"] = picture, ["text"] = "Hi" };
            var glyph = Assert.IsType<Grid>(BadgeGlyph.Create(badge, 18, Colors.Gray));

            var fill = Assert.IsType<Border>(glyph.Children[0]);
            var brush = Assert.IsType<ImageBrush>(fill.Background);
            Assert.Equal(36, Assert.IsAssignableFrom<BitmapSource>(brush.ImageSource).PixelWidth);
            Assert.Single(glyph.Children);  // no letters and no star over the picture
        });
    }

    [Fact]
    public async Task Picture_MissingFile_ShowsTheSymbolInItsPlace()
    {
        const string missing = @"C:\no\such\badge.png";
        await BadgeMedia.StillAsync(missing, 18);

        Sta.Run(() =>
        {
            var badge = new JsonObject { ["image"] = missing, ["symbol"] = "no.such.symbol" };
            var glyph = Assert.IsType<Grid>(BadgeGlyph.Create(badge, 18, Colors.Gray));

            var fill = Assert.IsType<Border>(glyph.Children[0]);
            Assert.IsType<SolidColorBrush>(fill.Background);
            var star = Assert.IsType<FluentGlyph>(glyph.Children[1]);
            Assert.Equal(new Rune(Convert.ToInt32(BadgeGlyph.StarCode, 16)).ToString(), star.Code);
        });
    }

    [Fact]
    public async Task Gif_AnimatesWithItsFrames_AndFillsTheBadgeWhenNoFillIsChosen()
    {
        using var root = new TempRoot();
        var gif = BadgeMediaTests.WriteGif(root.Folder, "cat.gif", Colors.Red, Colors.Blue);
        await BadgeMedia.AnimationAsync(gif, null, null, 18);

        Sta.Run(() =>
        {
            var plain = Assert.IsType<Grid>(BadgeGlyph.Create(new JsonObject { ["image"] = gif, ["plain"] = true }, 18, Colors.Gray));
            var sprite = Assert.IsType<SpriteView>(plain.Children[1]);
            Assert.Equal(2, sprite.FrameCount);
            Assert.Equal(18, sprite.Width, precision: 6);

            Assert.Null(plain.Clip);  // a plain animation is not cut to any shape

            var onFill = Assert.IsType<Grid>(BadgeGlyph.Create(new JsonObject { ["image"] = gif }, 18, Colors.Gray));
            Assert.Equal(18 * 0.78, Assert.IsType<SpriteView>(onFill.Children[1]).Width, precision: 6);
            Assert.NotNull(onFill.Clip);  // a filled animation keeps its shape
        });
    }

    [Fact]
    public void PlainSymbol_HasNoBackground_AndWhiteInk()
    {
        Sta.Run(() =>
        {
            var badge = new JsonObject { ["symbol"] = "hammer.fill", ["color"] = "#30D158", ["plain"] = true };
            var glyph = Assert.IsType<Grid>(BadgeGlyph.Create(badge, 18, Colors.Gray));

            var fill = Assert.IsType<Border>(glyph.Children[0]);
            Assert.Same(Brushes.Transparent, fill.Background);
            Assert.Null(glyph.Clip);
            var ink = Assert.IsType<Path>(glyph.Children[1]);
            Assert.Equal(Colors.White, Assert.IsType<SolidColorBrush>(ink.Stroke).Color);
        });
    }

    [Fact]
    public void PlainSymbol_WithAFontGlyph_DrawsWhite()
    {
        Sta.Run(() =>
        {
            var badge = new JsonObject { ["symbol"] = "no.such.symbol", ["color"] = "#30D158", ["plain"] = true };
            var glyph = Assert.IsType<Grid>(BadgeGlyph.Create(badge, 18, Colors.Gray));

            Assert.Same(Brushes.Transparent, Assert.IsType<Border>(glyph.Children[0]).Background);
            var star = Assert.IsType<FluentGlyph>(glyph.Children[1]);
            Assert.Equal(Colors.White, Assert.IsType<SolidColorBrush>(star.Foreground).Color);
        });
    }

    [Fact]
    public void FilledSymbol_HasTheTintBackground_AndWhiteInk()
    {
        Sta.Run(() =>
        {
            var badge = new JsonObject { ["symbol"] = "no.such.symbol", ["color"] = "#30D158" };
            var glyph = Assert.IsType<Grid>(BadgeGlyph.Create(badge, 18, Colors.Gray));

            var fill = Assert.IsType<SolidColorBrush>(Assert.IsType<Border>(glyph.Children[0]).Background);
            Assert.Equal(Color.FromRgb(0x30, 0xD1, 0x58), fill.Color);
            var star = Assert.IsType<FluentGlyph>(glyph.Children[1]);
            Assert.Equal(Colors.White, Assert.IsType<SolidColorBrush>(star.Foreground).Color);
        });
    }

    [Fact]
    public void ExplicitInk_WinsOverTheWhiteDefault()
    {
        Sta.Run(() =>
        {
            var badge = new JsonObject { ["symbol"] = "no.such.symbol", ["color"] = "#30D158", ["plain"] = true, ["fg"] = "#FF0000" };
            var glyph = Assert.IsType<Grid>(BadgeGlyph.Create(badge, 18, Colors.Gray));

            var star = Assert.IsType<FluentGlyph>(glyph.Children[1]);
            Assert.Equal(Color.FromRgb(0xFF, 0, 0), Assert.IsType<SolidColorBrush>(star.Foreground).Color);
        });
    }

    [Fact]
    public async Task PlainPicture_IsDrawnWhole_OnATransparentBackground_WithNoClip()
    {
        using var root = new TempRoot();
        var picture = BadgeMediaTests.WritePng(root.Folder, "plain.png", 400, 300);
        await BadgeMedia.StillAsync(picture, 18);

        Sta.Run(() =>
        {
            var badge = new JsonObject { ["image"] = picture, ["plain"] = true };
            var glyph = Assert.IsType<Grid>(BadgeGlyph.Create(badge, 18, Colors.Gray));

            var fill = Assert.IsType<Border>(glyph.Children[0]);
            Assert.Same(Brushes.Transparent, fill.Background);
            Assert.Equal(0, fill.CornerRadius.TopLeft);
            Assert.Null(glyph.Clip);
            var whole = Assert.IsType<System.Windows.Controls.Image>(glyph.Children[1]);
            Assert.Equal(Stretch.Uniform, whole.Stretch);
        });
    }

    [Fact]
    public async Task PlainPicture_WithTheBorder_KeepsOnlyTheBorder()
    {
        using var root = new TempRoot();
        var picture = BadgeMediaTests.WritePng(root.Folder, "bordered.png", 400, 300);
        await BadgeMedia.StillAsync(picture, 18);

        Sta.Run(() =>
        {
            var badge = new JsonObject { ["image"] = picture, ["plain"] = true, ["border"] = true };
            var glyph = Assert.IsType<Grid>(BadgeGlyph.Create(badge, 18, Colors.Gray));

            var fill = Assert.IsType<Border>(glyph.Children[0]);
            Assert.Equal(1, fill.BorderThickness.Left);
            Assert.Same(Brushes.Transparent, fill.Background);
        });
    }
}
