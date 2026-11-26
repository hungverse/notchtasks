using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NotchTasks.Core.Settings;
using NotchTasks.Pill;
using Xunit;

namespace NotchTasks.AppTests;

/// Pictures and animations for badges: a GIF is decoded once and kept, its frames run at their own speed, a picture is decoded
/// at about twice the badge size and no file stays open after it is read.
public sealed class BadgeMediaTests
{
    /// A PNG of one colour, `width` by `height` pixels.
    internal static string WritePng(string folder, string name, int width, int height)
    {
        Directory.CreateDirectory(folder);
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 0x30;
            pixels[i + 1] = 0x80;
            pixels[i + 2] = 0xF0;
            pixels[i + 3] = 0xFF;
        }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        var path = Path.Combine(folder, name);
        using var stream = File.Create(path);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        encoder.Save(stream);
        return path;
    }

    /// A GIF with one solid frame per colour (no delay metadata, so each frame takes the default tenth of a second).
    internal static string WriteGif(string folder, string name, params Color[] frames)
    {
        Directory.CreateDirectory(folder);
        var encoder = new GifBitmapEncoder();
        foreach (var color in frames)
        {
            var pixels = new byte[4 * 4 * 4];
            for (var i = 0; i < pixels.Length; i += 4)
            {
                pixels[i] = color.B;
                pixels[i + 1] = color.G;
                pixels[i + 2] = color.R;
                pixels[i + 3] = 0xFF;
            }
            var bitmap = BitmapSource.Create(4, 4, 96, 96, PixelFormats.Bgra32, null, pixels, 16);
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
        }
        var path = Path.Combine(folder, name);
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }

    [Fact]
    public void Gif_DecodesEveryFrame_AndKeepsThemForTheNextBadge()
    {
        using var root = new TempRoot();
        var gif = WriteGif(root.Folder, "cat.gif", Colors.Red, Colors.Blue, Colors.Green);

        Sta.Run(() =>
        {
            var first = BadgeMedia.Animation(gif, frames: null, fps: null, size: 18);
            var second = BadgeMedia.Animation(gif, frames: null, fps: null, size: 18);

            Assert.NotNull(first);
            Assert.Equal(3, first!.Count);
            Assert.Same(first, second);  // decoded once
        });
    }

    [Fact]
    public void Gif_TakesEachFramesDelay_TenthOfASecondWhenNoneIsSet()
    {
        using var root = new TempRoot();
        var gif = WriteGif(root.Folder, "two.gif", Colors.Red, Colors.Blue);

        Sta.Run(() =>
        {
            var sheet = Assert.IsType<SpriteSheet>(BadgeMedia.Animation(gif, frames: null, fps: null, size: 18));

            Assert.NotNull(sheet.Delays);
            Assert.All(sheet.Delays!, delay => Assert.Equal(0.1, delay, precision: 6));
        });
    }

    [Fact]
    public void Spritesheet_CutsTheStripIntoItsFrames_AtTheFramesRate()
    {
        using var root = new TempRoot();
        var sheet = WritePng(root.Folder, "strip.png", 32, 8);

        Sta.Run(() =>
        {
            var strip = Assert.IsType<SpriteSheet>(BadgeMedia.Animation(sheet, frames: 4, fps: 10, size: 18));

            Assert.Equal(4, strip.Count);
            Assert.Equal(0.1, strip.FrameSeconds, precision: 6);
            Assert.Null(strip.Delays);
        });
    }

    [Fact]
    public void Still_DecodesAtTwiceTheBadgeSize_AndLeavesTheFileFree()
    {
        using var root = new TempRoot();
        var picture = WritePng(root.Folder, "big.png", 400, 300);

        Sta.Run(() =>
        {
            var image = Assert.IsAssignableFrom<BitmapSource>(BadgeMedia.Still(picture, 18));

            Assert.Equal(36, image.PixelWidth);
            Assert.True(image.IsFrozen);
        });

        File.Delete(picture);  // not locked by the decoder
    }

    [Fact]
    public void Still_IsNullForAMissingFile()
    {
        using var root = new TempRoot();

        Sta.Run(() => Assert.Null(BadgeMedia.Still(Path.Combine(root.Folder, "gone.png"), 18)));
    }

    [Fact]
    public void Still_IsNullForAFileThatIsNotAPicture()
    {
        using var root = new TempRoot();
        Directory.CreateDirectory(root.Folder);
        var text = Path.Combine(root.Folder, "notes.png");
        File.WriteAllText(text, "not a picture at all");

        Sta.Run(() => Assert.Null(BadgeMedia.Still(text, 18)));
    }

    [Fact]
    public void SpriteSheet_WithOneSpeed_StepsOnItsFramePeriod()
    {
        var sheet = new SpriteSheet([null!, null!], frameSeconds: 0.5, pixel: false);

        Assert.Equal(0, sheet.IndexAt(0));
        Assert.Equal(0, sheet.IndexAt(499));
        Assert.Equal(1, sheet.IndexAt(500));
        Assert.Equal(0, sheet.IndexAt(1000));
        Assert.Equal(200, sheet.MsUntilNextFrame(300), precision: 6);  // the next frame is 500 ms from the epoch
    }

    [Fact]
    public void SpriteSheet_WithDelays_StepsThroughEachFramesOwnTime()
    {
        var sheet = new SpriteSheet([null!, null!, null!], frameSeconds: 0.1, pixel: false, delays: [0.1, 0.3, 0.2]);

        Assert.Equal(0, sheet.IndexAt(0));
        Assert.Equal(0, sheet.IndexAt(99));
        Assert.Equal(1, sheet.IndexAt(100));
        Assert.Equal(1, sheet.IndexAt(399));
        Assert.Equal(2, sheet.IndexAt(400));
        Assert.Equal(0, sheet.IndexAt(600));  // the loop is 600 ms
        Assert.Equal(100, sheet.MsUntilNextFrame(0), precision: 6);
        Assert.Equal(250, sheet.MsUntilNextFrame(150), precision: 6);
    }

    [Fact]
    public void SpriteSheet_WithOneFrame_NeverNeedsATick()
    {
        var sheet = new SpriteSheet([null!], frameSeconds: 0.1, pixel: false);

        Assert.Equal(double.MaxValue, sheet.MsUntilNextFrame(0));
    }
}
