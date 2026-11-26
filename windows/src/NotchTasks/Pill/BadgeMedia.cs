using System.Globalization;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NotchTasks.Pill;

/// The pictures and animations a badge draws, decoded once and kept (the Mac's Animations cache). A picture is decoded at about twice
/// the badge's size, with OnLoad, so the file is read in full and no handle stays open on it. A GIF's frames are decoded and scaled
/// to that size once, with the delay of each frame. Each cache is keyed by file and size, so a badge drawn at another size keeps its
/// own copy. A file that cannot be read gives null: the badge then shows what it has without it, as the Mac does.
internal static class BadgeMedia
{
    /// The delay a GIF frame without one takes (seconds), as the Mac's Frames.gif and browsers take it.
    private const double DefaultDelay = 0.1;

    private static readonly Dictionary<string, BitmapSource?> Stills = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, SpriteSheet?> Animations = new(StringComparer.Ordinal);

    /// The width a picture is decoded to for a badge of `size` DIP: about twice its size, so it stays sharp on the notch.
    public static int DecodeWidth(double size) => Math.Max(1, (int)Math.Round(size * 2));

    /// The picture at `path`, decoded for a badge of `size` DIP. Null when the file is missing or is not a picture.
    public static BitmapSource? Still(string path, double size)
    {
        var width = DecodeWidth(size);
        var key = width.ToString(CultureInfo.InvariantCulture) + "|" + path;
        if (!Stills.TryGetValue(key, out var image))
        {
            image = DecodeStill(path, width);
            Stills[key] = image;
        }
        return image;
    }

    /// The frames of an animated picture: a GIF (its own frames and delays), or a PNG spritesheet cut into `frames` square frames at
    /// `fps` (Mac Frames.strip). Null when it cannot be read; the caller shows the still picture then.
    public static SpriteSheet? Animation(string path, int? frames, double? fps, double size)
    {
        var width = DecodeWidth(size);
        var key = width.ToString(CultureInfo.InvariantCulture) + "|" + (frames?.ToString(CultureInfo.InvariantCulture) ?? "-")
            + "|" + (fps?.ToString("R", CultureInfo.InvariantCulture) ?? "-") + "|" + path;
        if (!Animations.TryGetValue(key, out var sheet))
        {
            sheet = IsGif(path) ? DecodeGif(path, width) : DecodeStrip(path, frames, fps);
            Animations[key] = sheet;
        }
        return sheet;
    }

    private static bool IsGif(string path) => string.Equals(Path.GetExtension(path), ".gif", StringComparison.OrdinalIgnoreCase);

    private static BitmapSource? DecodeStill(string path, int width)
    {
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = width;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception)
        {
            // a damaged or unsupported file shows as missing: the pill must not fail on it
            return null;
        }
    }

    private static SpriteSheet? DecodeGif(string path, int width)
    {
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            using var stream = File.OpenRead(path);
            var decoder = new GifBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var frames = new List<ImageSource>(decoder.Frames.Count);
            var delays = new List<double>(decoder.Frames.Count);
            foreach (var frame in decoder.Frames)
            {
                frames.Add(Scaled(frame, width));
                delays.Add(DelayOf(frame));
            }
            return frames.Count == 0 ? null : new SpriteSheet(frames, delays[0], pixel: false, delays);
        }
        catch (Exception)
        {
            // a damaged GIF shows as missing: the still picture or the symbol is drawn instead
            return null;
        }
    }

    private static SpriteSheet? DecodeStrip(string path, int? frames, double? fps)
    {
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            var sheet = new BitmapImage();
            sheet.BeginInit();
            sheet.UriSource = new Uri(path, UriKind.Absolute);
            sheet.CacheOption = BitmapCacheOption.OnLoad;
            sheet.EndInit();
            sheet.Freeze();

            var count = Math.Max(1, frames ?? Math.Max(1, sheet.PixelWidth / Math.Max(sheet.PixelHeight, 1)));
            var width = sheet.PixelWidth / count;
            if (width < 1)
            {
                return null;
            }
            var cut = new List<ImageSource>(count);
            for (var i = 0; i < count; i++)
            {
                var frame = new CroppedBitmap(sheet, new System.Windows.Int32Rect(i * width, 0, width, sheet.PixelHeight));
                frame.Freeze();
                cut.Add(frame);
            }
            return new SpriteSheet(cut, 1 / Math.Max(fps ?? 10, 1), pixel: false);
        }
        catch (Exception)
        {
            // a file that is not a spritesheet shows as missing
            return null;
        }
    }

    /// A GIF frame scaled to `width` pixels wide (its height follows the frame's shape), as a frozen bitmap in one pixel format.
    private static ImageSource Scaled(BitmapSource frame, int width)
    {
        var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var scale = width / (double)converted.PixelWidth;
        var scaled = new TransformedBitmap(converted, new ScaleTransform(scale, scale));
        scaled.Freeze();
        return scaled;
    }

    /// A GIF frame's delay in seconds: its own, or the default when it has none or it is zero (the Mac's rule: under 11 ms is 0.1 s).
    private static double DelayOf(BitmapFrame frame)
    {
        var ticks = frame.Metadata is BitmapMetadata metadata ? metadata.GetQuery("/grctlext/Delay") : null;
        if (ticks is null)
        {
            return DefaultDelay;
        }
        var seconds = Convert.ToDouble(ticks, CultureInfo.InvariantCulture) / 100.0;
        return seconds < 0.011 ? DefaultDelay : seconds;
    }
}
