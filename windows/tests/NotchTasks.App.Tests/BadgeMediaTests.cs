using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NotchTasks.Pill;
using Xunit;

namespace NotchTasks.AppTests;

/// Pictures and animations for badges: decoded on a background thread and kept, a GIF composed onto its screen as a browser does,
/// frames scaled to about twice the badge size, and untrusted files capped.
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

    /// One frame of a hand-built GIF: where it sits, its colour indexes (0 red, 1 blue), its disposal and its delay in hundredths.
    internal sealed record GifFrame(int Left, int Top, int Width, int Height, byte[] Indexes, int Disposal = 0, int DelayCs = 10);

    /// Writes a GIF89a with a two-colour table (0 red, 1 blue) and the given frames. GifBitmapEncoder cannot write offsets or
    /// disposal, so the bytes are written here. Each pixel is its own LZW run (a clear code before it), so no dictionary grows.
    internal static string WriteGifFrames(string folder, string name, int width, int height, params GifFrame[] frames)
    {
        Directory.CreateDirectory(folder);
        var bytes = new List<byte>();
        bytes.AddRange("GIF89a"u8.ToArray());
        Le16(bytes, width);
        Le16(bytes, height);
        bytes.AddRange([0xF0, 0x00, 0x00]);  // global colour table of two colours, background 0, no aspect
        bytes.AddRange([255, 0, 0, 0, 0, 255]);
        foreach (var frame in frames)
        {
            bytes.AddRange([0x21, 0xF9, 0x04, (byte)(frame.Disposal << 2)]);  // graphic control: disposal, no transparency
            Le16(bytes, frame.DelayCs);
            bytes.AddRange([0x00, 0x00]);  // transparent index (unused), block end
            bytes.Add(0x2C);
            Le16(bytes, frame.Left);
            Le16(bytes, frame.Top);
            Le16(bytes, frame.Width);
            Le16(bytes, frame.Height);
            bytes.Add(0x00);  // no local table, not interlaced
            bytes.Add(0x02);  // LZW minimum code size
            var data = Lzw(frame.Indexes);
            for (var i = 0; i < data.Length; i += 255)
            {
                var n = Math.Min(255, data.Length - i);
                bytes.Add((byte)n);
                bytes.AddRange(data.Skip(i).Take(n));
            }
            bytes.Add(0x00);
        }
        bytes.Add(0x3B);
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, bytes.ToArray());
        return path;
    }

    private static void Le16(List<byte> bytes, int value)
    {
        bytes.Add((byte)(value & 0xFF));
        bytes.Add((byte)((value >> 8) & 0xFF));
    }

    /// LZW codes of 3 bits (minimum code size 2): a clear code (4), then the pixel (0 or 1), for each pixel, then the end code (5).
    private static byte[] Lzw(byte[] indexes)
    {
        var output = new List<byte>();
        var accumulator = 0;
        var filled = 0;
        void Put(int code)
        {
            accumulator |= code << filled;
            filled += 3;
            while (filled >= 8)
            {
                output.Add((byte)(accumulator & 0xFF));
                accumulator >>= 8;
                filled -= 8;
            }
        }
        foreach (var index in indexes)
        {
            Put(4);
            Put(index);
        }
        Put(5);
        if (filled > 0)
        {
            output.Add((byte)(accumulator & 0xFF));
        }
        return output.ToArray();
    }

    /// The colour of one pixel of a frame: blue, green, red and alpha.
    private static (byte B, byte G, byte R, byte A) PixelAt(ImageSource source, int x, int y)
    {
        var converted = new FormatConvertedBitmap((BitmapSource)source, PixelFormats.Bgra32, null, 0);
        var buffer = new byte[4];
        converted.CopyPixels(new System.Windows.Int32Rect(x, y, 1, 1), buffer, 4, 0);
        return (buffer[0], buffer[1], buffer[2], buffer[3]);
    }

    private static byte[] Solid(int count, byte index) => Enumerable.Repeat(index, count).ToArray();

    [Fact]
    public async Task Gif_DecodesEveryFrame_AndKeepsThemForTheNextBadge()
    {
        using var root = new TempRoot();
        var gif = WriteGif(root.Folder, "cat.gif", Colors.Red, Colors.Blue, Colors.Green);

        var first = BadgeMedia.AnimationAsync(gif, frames: null, fps: null, size: 18);
        var second = BadgeMedia.AnimationAsync(gif, frames: null, fps: null, size: 18);

        Assert.Same(first, second);  // decoded once
        Assert.Equal(3, (await first)!.Count);
    }

    [Fact]
    public async Task Gif_TakesEachFramesDelay_TenthOfASecondWhenNoneIsSet()
    {
        using var root = new TempRoot();
        var gif = WriteGif(root.Folder, "two.gif", Colors.Red, Colors.Blue);

        var sheet = Assert.IsType<SpriteSheet>(await BadgeMedia.AnimationAsync(gif, frames: null, fps: null, size: 18));

        Assert.NotNull(sheet.Delays);
        Assert.All(sheet.Delays!, delay => Assert.Equal(0.1, delay, precision: 6));
    }

    [Fact]
    public async Task Gif_PatchFrame_IsComposedOntoTheScreen()
    {
        using var root = new TempRoot();
        var gif = WriteGifFrames(root.Folder, "patch.gif", 4, 4,
            new GifFrame(0, 0, 4, 4, Solid(16, 0)),                 // red everywhere
            new GifFrame(2, 1, 2, 2, Solid(4, 1)));                 // a 2x2 blue patch at (2,1)

        var sheet = Assert.IsType<SpriteSheet>(await BadgeMedia.AnimationAsync(gif, null, null, size: 18));

        Assert.Equal(2, sheet.Count);
        // the frame is 36 px for the 4 px screen: 9 px to a screen pixel
        var second = sheet.Frames[1];
        Assert.Equal(36, second.Width, precision: 6);
        Assert.Equal((0, 0, 255, 255), PixelAt(second, 4, 4));    // (0,0): red, kept from the first frame
        Assert.Equal((255, 0, 0, 255), PixelAt(second, 31, 13));  // (3,1): blue patch (blue, green, red, alpha)
    }

    [Fact]
    public async Task Gif_DisposalToBackground_ClearsTheFramesRectangle()
    {
        using var root = new TempRoot();
        var gif = WriteGifFrames(root.Folder, "dispose.gif", 4, 4,
            new GifFrame(0, 0, 4, 4, Solid(16, 0), Disposal: 2),    // red, then cleared once shown
            new GifFrame(0, 0, 2, 2, Solid(4, 1)));                 // a 2x2 blue patch in the corner

        var sheet = Assert.IsType<SpriteSheet>(await BadgeMedia.AnimationAsync(gif, null, null, size: 18));

        var second = sheet.Frames[1];
        Assert.Equal((255, 0, 0, 255), PixelAt(second, 4, 4));    // (0,0): the blue patch
        Assert.Equal((0, 0, 0, 0), PixelAt(second, 31, 31));      // (3,3): cleared by the disposal, transparent
    }

    [Fact]
    public async Task Gif_IsScaledToTwiceTheBadge_AndNoFullSizeFrameIsKept()
    {
        using var root = new TempRoot();
        var gif = WriteGifFrames(root.Folder, "wide.gif", 200, 100, new GifFrame(0, 0, 200, 100, Solid(20000, 0)));

        var sheet = Assert.IsType<SpriteSheet>(await BadgeMedia.AnimationAsync(gif, null, null, size: 18));

        Assert.Equal(36, sheet.Frames[0].Width, precision: 6);   // the longer side is 36 px
        Assert.Equal(18, sheet.Frames[0].Height, precision: 6);
    }

    [Fact]
    public async Task Gif_OverTheCanvasLimit_IsRefused()
    {
        using var root = new TempRoot();
        var gif = WriteGifFrames(root.Folder, "huge.gif", BadgeMedia.MaxGifSide + 1, 2, new GifFrame(0, 0, 1, 1, Solid(1, 0)));

        Assert.Null(await BadgeMedia.AnimationAsync(gif, null, null, size: 18));
    }

    [Fact]
    public async Task Gif_OverTheFrameLimit_IsRefused_ButTheLimitItselfIsKept()
    {
        using var root = new TempRoot();
        var atLimit = WriteGifFrames(root.Folder, "limit.gif", 1, 1,
            Enumerable.Range(0, BadgeMedia.MaxGifFrames).Select(_ => new GifFrame(0, 0, 1, 1, Solid(1, 0))).ToArray());
        var over = WriteGifFrames(root.Folder, "over.gif", 1, 1,
            Enumerable.Range(0, BadgeMedia.MaxGifFrames + 1).Select(_ => new GifFrame(0, 0, 1, 1, Solid(1, 0))).ToArray());

        Assert.Equal(BadgeMedia.MaxGifFrames, (await BadgeMedia.AnimationAsync(atLimit, null, null, size: 18))!.Count);
        Assert.Null(await BadgeMedia.AnimationAsync(over, null, null, size: 18));
    }

    [Fact]
    public async Task Spritesheet_CutsTheStripIntoItsFrames_AtTheFramesRate()
    {
        using var root = new TempRoot();
        var sheet = WritePng(root.Folder, "strip.png", 32, 8);

        var strip = Assert.IsType<SpriteSheet>(await BadgeMedia.AnimationAsync(sheet, frames: 4, fps: 10, size: 18));

        Assert.Equal(4, strip.Count);
        Assert.Equal(0.1, strip.FrameSeconds, precision: 6);
        Assert.Null(strip.Delays);
    }

    [Fact]
    public async Task Still_DecodesAtTwiceTheBadgeSize_AndLeavesTheFileFree()
    {
        using var root = new TempRoot();
        var picture = WritePng(root.Folder, "big.png", 400, 300);

        var image = Assert.IsAssignableFrom<BitmapSource>(await BadgeMedia.StillAsync(picture, 18));

        Assert.Equal(36, image.PixelWidth);
        Assert.True(image.IsFrozen);
        File.Delete(picture);  // not locked by the decoder
    }

    [Fact]
    public async Task Still_OverThePictureLimit_IsRefused()
    {
        using var root = new TempRoot();
        var wide = WritePng(root.Folder, "wide.png", BadgeMedia.MaxPictureSide + 1, 1);

        Assert.Null(await BadgeMedia.StillAsync(wide, 18));
    }

    [Fact]
    public async Task Still_IsNullForAMissingFile()
    {
        using var root = new TempRoot();

        Assert.Null(await BadgeMedia.StillAsync(Path.Combine(root.Folder, "gone.png"), 18));
    }

    [Fact]
    public async Task Still_IsNullForAFileThatIsNotAPicture()
    {
        using var root = new TempRoot();
        Directory.CreateDirectory(root.Folder);
        var text = Path.Combine(root.Folder, "notes.png");
        File.WriteAllText(text, "not a picture at all");

        Assert.Null(await BadgeMedia.StillAsync(text, 18));
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
