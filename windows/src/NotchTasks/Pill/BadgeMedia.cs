using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NotchTasks.Core.Settings;

namespace NotchTasks.Pill;

/// The pictures and animations a badge draws. They are decoded on a background thread and kept (the Mac's Animations cache), and
/// the badge is drawn when they are ready. A GIF is checked from its bytes first (its logical screen and its frame count, without
/// decoding a frame), then composed frame by frame onto its screen as a browser does. Every frame is scaled to about twice the
/// badge's size before it is kept, and no full-size frame is kept. Untrusted files are capped: a GIF over MaxGifSide or MaxGifFrames
/// and a picture over MaxPictureSide on either side are refused (null), and the badge shows its symbol. A failure is not kept in the
/// cache, and an entry follows its file: a replaced file decodes again.
internal static class BadgeMedia
{
    /// The largest side of a GIF's logical screen this build will decode.
    public const int MaxGifSide = 1024;

    /// The most frames of a GIF this build will decode.
    public const int MaxGifFrames = 300;

    /// The largest side of a picture or spritesheet this build will decode.
    public const int MaxPictureSide = 8192;

    /// How many decoded entries each cache keeps; the cache is emptied when it would grow past this.
    public const int MaxCacheEntries = 64;

    /// The delay a GIF frame without one takes (seconds), as the Mac's Frames.gif and browsers take it.
    private const double DefaultDelay = 0.1;

    private static readonly object Gate = new();
    private static readonly Dictionary<string, Task<BitmapSource?>> Stills = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Task<SpriteSheet?>> Animations = new(StringComparer.Ordinal);

    /// The logical screen size and frame count of a GIF, read from its bytes.
    internal readonly record struct GifShape(int Width, int Height, int Frames);

    /// The width a picture is decoded to, and the size a frame is scaled to, for a badge of `size` DIP: about twice its size.
    public static int DecodeWidth(double size) => Math.Max(1, (int)Math.Round(size * 2));

    /// The picture at `path`, decoded for a badge of `size` DIP on a background thread. The result is null when the file is missing,
    /// is not a picture or is over MaxPictureSide.
    public static Task<BitmapSource?> StillAsync(string path, double size)
    {
        var width = DecodeWidth(size);
        var stamp = FileStamp(path);
        if (stamp is null)
        {
            return Task.FromResult<BitmapSource?>(null);
        }
        return Cached(Stills, width.ToString(CultureInfo.InvariantCulture) + "|" + stamp + "|" + path, () => DecodeStill(path, width));
    }

    /// The frames of an animated picture: a GIF (composed, with its own delays), or a PNG spritesheet cut into `frames` square frames
    /// at `fps` (Mac Frames.strip). The result is null when it cannot be read or is over the caps.
    public static Task<SpriteSheet?> AnimationAsync(string path, int? frames, double? fps, double size)
    {
        var width = DecodeWidth(size);
        var stamp = FileStamp(path);
        if (stamp is null)
        {
            return Task.FromResult<SpriteSheet?>(null);
        }
        var key = width.ToString(CultureInfo.InvariantCulture) + "|" + (frames?.ToString(CultureInfo.InvariantCulture) ?? "-")
            + "|" + (fps?.ToString("R", CultureInfo.InvariantCulture) ?? "-") + "|" + stamp + "|" + path;
        return Cached(Animations, key, () => IsGif(path) ? DecodeGif(path, width) : DecodeStrip(path, frames, fps, width));
    }

    /// The task that decodes an entry, kept under its key. A task that found nothing (or failed) is dropped from the cache, so the next
    /// request decodes again. The cache is emptied when it is full, so it holds at most MaxCacheEntries.
    private static Task<T?> Cached<T>(Dictionary<string, Task<T?>> cache, string key, Func<T?> decode) where T : class
    {
        lock (Gate)
        {
            if (cache.TryGetValue(key, out var existing))
            {
                return existing;
            }
            if (cache.Count >= MaxCacheEntries)
            {
                cache.Clear();
            }
            var task = Task.Run(decode);
            cache[key] = task;
            _ = task.ContinueWith(done =>
            {
                if (done.IsCompletedSuccessfully && done.Result is not null)
                {
                    return;
                }
                lock (Gate)
                {
                    if (cache.TryGetValue(key, out var stored) && stored == done)
                    {
                        cache.Remove(key);
                    }
                }
            }, TaskScheduler.Default);
            return task;
        }
    }

    /// The file's identity for the caches: its length and last write time. Null when the file is not there.
    private static string? FileStamp(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length.ToString(CultureInfo.InvariantCulture) + "@" + info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture) : null;
        }
        catch (Exception)
        {
            // a path that cannot be read is treated as missing
            return null;
        }
    }

    private static bool IsGif(string path) => string.Equals(Path.GetExtension(path), ".gif", StringComparison.OrdinalIgnoreCase);

    private static BitmapSource? DecodeStill(string path, int width)
    {
        if (!File.Exists(path) || !HeaderFits(path))
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

    /// Whether the picture's own size (read from its header, before any pixel is decoded) is within MaxPictureSide.
    private static bool HeaderFits(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var header = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
            return header.PixelWidth <= MaxPictureSide && header.PixelHeight <= MaxPictureSide;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// Reads a GIF's logical screen size and its frame count from its bytes: the header, then each block, skipping every extension and
    /// sub-block. No frame is decoded. Counting stops one past MaxGifFrames, so the walk is bounded. Null when the bytes are not a GIF
    /// or do not parse.
    internal static GifShape? ReadGifShape(byte[] bytes)
    {
        if (bytes.Length < 13 || !(bytes[0] == 'G' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == '8'))
        {
            return null;
        }
        var width = bytes[6] | (bytes[7] << 8);
        var height = bytes[8] | (bytes[9] << 8);
        var pos = 13;
        if ((bytes[10] & 0x80) != 0)
        {
            pos += 3 * (1 << ((bytes[10] & 0x07) + 1));  // the global colour table
        }
        var frames = 0;
        while (pos < bytes.Length)
        {
            var block = bytes[pos++];
            if (block == 0x3B)
            {
                break;  // trailer
            }
            if (block == 0x21)
            {
                pos++;  // the extension's label
                if (!SkipSubBlocks(bytes, ref pos))
                {
                    return null;
                }
            }
            else if (block == 0x2C)
            {
                if (pos + 9 > bytes.Length)
                {
                    return null;
                }
                var packed = bytes[pos + 8];
                pos += 9;
                if ((packed & 0x80) != 0)
                {
                    pos += 3 * (1 << ((packed & 0x07) + 1));  // a local colour table
                }
                pos++;  // the LZW minimum code size
                if (pos > bytes.Length || !SkipSubBlocks(bytes, ref pos))
                {
                    return null;
                }
                frames++;
                if (frames > MaxGifFrames)
                {
                    break;  // enough to refuse it
                }
            }
            else
            {
                return null;
            }
            if (pos > bytes.Length)
            {
                return null;
            }
        }
        return new GifShape(width, height, frames);
    }

    /// Steps over a run of data sub-blocks (a size byte, then that many bytes) up to the zero that ends them.
    private static bool SkipSubBlocks(byte[] bytes, ref int pos)
    {
        while (true)
        {
            if (pos >= bytes.Length)
            {
                return false;
            }
            var size = bytes[pos++];
            if (size == 0)
            {
                return true;
            }
            pos += size;
            if (pos > bytes.Length)
            {
                return false;
            }
        }
    }

    /// Composes a GIF onto its logical screen and keeps each frame scaled to `target` pixels on its longer side. The file is checked
    /// from its bytes first, so a GIF over the caps is refused before any frame is decoded.
    private static SpriteSheet? DecodeGif(string path, int target)
    {
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            var info = new FileInfo(path);
            if (info.Length > BadgeImages.MaxBytes)
            {
                return null;
            }
            var bytes = File.ReadAllBytes(path);
            var shape = ReadGifShape(bytes);
            if (shape is not { } gif || gif.Frames == 0 || gif.Frames > MaxGifFrames
                || gif.Width < 1 || gif.Height < 1 || gif.Width > MaxGifSide || gif.Height > MaxGifSide)
            {
                return null;
            }
            using var stream = new MemoryStream(bytes, writable: false);
            var decoder = new GifBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count != gif.Frames)
            {
                return null;
            }
            return Compose(decoder.Frames, gif.Width, gif.Height, target);
        }
        catch (Exception)
        {
            // a damaged GIF shows as missing: the symbol is drawn instead
            return null;
        }
    }

    /// The GIF's frames on the logical screen (browser rules): each frame is drawn at its offset, after the disposal of the frame
    /// before it (2 clears its rectangle, 3 restores the screen from before that frame).
    private static SpriteSheet? Compose(IReadOnlyList<BitmapFrame> frames, int width, int height, int target)
    {
        var stride = width * 4;
        var canvas = new byte[stride * height];
        byte[]? beforeDisposal = null;
        var previousDisposal = 0;
        int previousLeft = 0, previousTop = 0, previousWidth = 0, previousHeight = 0;
        var images = new List<ImageSource>(frames.Count);
        var delays = new List<double>(frames.Count);

        foreach (var frame in frames)
        {
            var meta = frame.Metadata as BitmapMetadata;
            var left = IntQuery(meta, "/imgdesc/Left") ?? 0;
            var top = IntQuery(meta, "/imgdesc/Top") ?? 0;
            var disposal = IntQuery(meta, "/grctlext/Disposal") ?? 0;

            if (previousDisposal == 2)
            {
                ClearRect(canvas, width, height, previousLeft, previousTop, previousWidth, previousHeight);
            }
            else if (previousDisposal == 3 && beforeDisposal is not null)
            {
                Array.Copy(beforeDisposal, canvas, canvas.Length);
            }
            if (disposal == 3)
            {
                beforeDisposal = (byte[])canvas.Clone();
            }

            var source = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            var frameWidth = source.PixelWidth;
            var frameHeight = source.PixelHeight;
            var pixels = new byte[frameWidth * frameHeight * 4];
            source.CopyPixels(pixels, frameWidth * 4, 0);
            Draw(canvas, width, height, pixels, frameWidth, frameHeight, left, top);

            images.Add(Scaled(Snapshot(canvas, width, height), target));
            delays.Add(DelayOf(meta));

            previousDisposal = disposal;
            previousLeft = left;
            previousTop = top;
            previousWidth = frameWidth;
            previousHeight = frameHeight;
        }
        return images.Count == 0 ? null : new SpriteSheet(images, delays[0], pixel: false, delays);
    }

    /// Copies the frame's visible pixels onto the canvas. GIF transparency is alpha 0, so those pixels leave the canvas as it was.
    private static void Draw(byte[] canvas, int canvasWidth, int canvasHeight, byte[] pixels, int frameWidth, int frameHeight,
        int left, int top)
    {
        for (var y = 0; y < frameHeight; y++)
        {
            var cy = top + y;
            if (cy < 0 || cy >= canvasHeight)
            {
                continue;
            }
            for (var x = 0; x < frameWidth; x++)
            {
                var cx = left + x;
                if (cx < 0 || cx >= canvasWidth)
                {
                    continue;
                }
                var s = (y * frameWidth + x) * 4;
                if (pixels[s + 3] == 0)
                {
                    continue;
                }
                var d = (cy * canvasWidth + cx) * 4;
                canvas[d] = pixels[s];
                canvas[d + 1] = pixels[s + 1];
                canvas[d + 2] = pixels[s + 2];
                canvas[d + 3] = pixels[s + 3];
            }
        }
    }

    /// Makes a rectangle of the canvas transparent (disposal 2).
    private static void ClearRect(byte[] canvas, int canvasWidth, int canvasHeight, int left, int top, int width, int height)
    {
        for (var y = Math.Max(0, top); y < Math.Min(canvasHeight, top + height); y++)
        {
            for (var x = Math.Max(0, left); x < Math.Min(canvasWidth, left + width); x++)
            {
                Array.Clear(canvas, (y * canvasWidth + x) * 4, 4);
            }
        }
    }

    /// A frozen copy of the canvas, so later drawing does not change it.
    private static BitmapSource Snapshot(byte[] canvas, int width, int height)
    {
        var copy = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, (byte[])canvas.Clone(), width * 4);
        copy.Freeze();
        return copy;
    }

    /// A frame scaled so its longer side is `target` pixels. It is copied out of the transform, so the full frame is not kept.
    private static ImageSource Scaled(BitmapSource full, int target)
    {
        var scale = target / (double)Math.Max(full.PixelWidth, full.PixelHeight);
        var transformed = new TransformedBitmap(full, new ScaleTransform(scale, scale));
        var width = Math.Max(1, transformed.PixelWidth);
        var height = Math.Max(1, transformed.PixelHeight);
        var pixels = new byte[width * height * 4];
        transformed.CopyPixels(pixels, width * 4, 0);
        var result = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        result.Freeze();
        return result;
    }

    private static SpriteSheet? DecodeStrip(string path, int? frames, double? fps, int target)
    {
        if (!File.Exists(path) || !HeaderFits(path))
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
                var crop = new CroppedBitmap(sheet, new System.Windows.Int32Rect(i * width, 0, width, sheet.PixelHeight));
                crop.Freeze();
                cut.Add(Scaled(crop, target));
            }
            return new SpriteSheet(cut, 1 / Math.Max(fps ?? 10, 1), pixel: false);
        }
        catch (Exception)
        {
            // a file that is not a spritesheet shows as missing
            return null;
        }
    }

    private static int? IntQuery(BitmapMetadata? metadata, string query)
    {
        var value = metadata?.GetQuery(query);
        return value is null ? null : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    /// A GIF frame's delay in seconds: its own, or the default when it has none or it is zero (the Mac's rule: under 11 ms is 0.1 s).
    private static double DelayOf(BitmapMetadata? metadata)
    {
        var ticks = IntQuery(metadata, "/grctlext/Delay");
        if (ticks is null)
        {
            return DefaultDelay;
        }
        var seconds = ticks.Value / 100.0;
        return seconds < 0.011 ? DefaultDelay : seconds;
    }
}
