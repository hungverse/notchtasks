using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NotchTasks.Core;

namespace NotchTasks.Pill;

/// The frames of one animation: a horizontal strip cut into equal frames (Mac Frames.strip), or the frames of a GIF with their own
/// delays (Mac Frames.gif). Frames step at FrameSeconds each, or at Delays (seconds, one per frame) when those are set. The loop
/// is the same as the Mac's Frames.image(at:): time wraps around the total.
internal sealed class SpriteSheet
{
    private readonly double[]? _delaysMs;
    private readonly double _loopMs;

    public SpriteSheet(IReadOnlyList<ImageSource> frames, double frameSeconds, bool pixel, IReadOnlyList<double>? delays = null)
    {
        Frames = frames;
        FrameSeconds = frameSeconds;
        Pixel = pixel;
        Delays = delays;
        if (delays is not null)
        {
            _delaysMs = delays.Select(seconds => seconds * 1000).ToArray();
            _loopMs = _delaysMs.Sum();
        }
    }

    public IReadOnlyList<ImageSource> Frames { get; }

    /// The frame time of a sheet with one speed (a strip); with Delays set, the first frame's time, for reference only.
    public double FrameSeconds { get; }

    public bool Pixel { get; }

    /// Each frame's time in seconds (a GIF's own delays), or null when every frame takes FrameSeconds.
    public IReadOnlyList<double>? Delays { get; }

    public int Count => Frames.Count;

    /// The frame that shows `elapsedMs` after the epoch (0 for a sheet with one frame or no speed).
    public int IndexAt(double elapsedMs) => Locate(elapsedMs).Index;

    /// Milliseconds from `elapsedMs` to the next frame change; double.MaxValue when the sheet does not change (one frame).
    public double MsUntilNextFrame(double elapsedMs) => Locate(elapsedMs).UntilNextMs;

    private (int Index, double UntilNextMs) Locate(double elapsedMs)
    {
        var count = Frames.Count;
        if (count <= 1)
        {
            return (0, double.MaxValue);
        }

        if (_delaysMs is null)
        {
            if (!(FrameSeconds > 0))
            {
                return (0, double.MaxValue);
            }
            var period = FrameSeconds * 1000;
            var frame = (long)Math.Floor(elapsedMs / period);
            return ((int)(((frame % count) + count) % count), (Math.Floor(elapsedMs / period) + 1) * period - elapsedMs);
        }

        if (!(_loopMs > 0))
        {
            return (0, double.MaxValue);
        }
        var t = elapsedMs % _loopMs;
        if (t < 0)
        {
            t += _loopMs;
        }
        for (var i = 0; i < _delaysMs.Length; i++)
        {
            if (t < _delaysMs[i])
            {
                return (i, _delaysMs[i] - t);
            }
            t -= _delaysMs[i];
        }
        return (_delaysMs.Length - 1, _delaysMs[^1]);
    }
}

/// Loads sprites from Sprites\catalog.json and Sprites\<id>.png, once per id. Null when the sprite is unknown or missing.
internal static class SpriteCache
{
    private static readonly Dictionary<string, SpriteSheet?> Cache = new(StringComparer.Ordinal);
    private static Dictionary<string, JsonObject>? _catalog;
    private static readonly List<string> CatalogOrder = new();

    /// The catalog entries in the file's order: id, category and title (the key of the name). Empty when the catalog is unreadable.
    public static IReadOnlyList<(string Id, string Category, string Title)> Entries()
    {
        Catalog();
        return CatalogOrder
            .Select(id => (Id: id, Category: ReadString(_catalog![id], "category") ?? string.Empty,
                           Title: ReadString(_catalog![id], "title") ?? id))
            .ToList();
    }

    public static SpriteSheet? Get(string id)
    {
        if (Cache.TryGetValue(id, out var cached))
        {
            return cached;
        }
        var sheet = Load(id);
        Cache[id] = sheet;
        return sheet;
    }

    /// Drops the cached sprites that are not in `keep`. The Control Panel's Icon Library loads sprites the pill does not show:
    /// when the Control Panel closes, only the pill's own sprites stay cached. A sprite loads again when it is asked for.
    public static void Retain(IReadOnlySet<string> keep)
    {
        foreach (var id in Cache.Keys.Where(id => !keep.Contains(id)).ToList())
        {
            Cache.Remove(id);
        }
    }

    private static SpriteSheet? Load(string id)
    {
        if (!Catalog().TryGetValue(id, out var entry))
        {
            return null;
        }
        var file = Path.Combine(SpriteFolder, id + ".png");
        if (!File.Exists(file))
        {
            return null;
        }

        var sheet = new BitmapImage();
        sheet.BeginInit();
        sheet.UriSource = new Uri(file, UriKind.Absolute);
        sheet.CacheOption = BitmapCacheOption.OnLoad;
        sheet.EndInit();
        sheet.Freeze();

        var count = ReadInt(entry, "frames") ?? Math.Max(1, sheet.PixelWidth / Math.Max(sheet.PixelHeight, 1));
        count = Math.Max(1, count);
        var width = sheet.PixelWidth / count;
        if (width < 1)
        {
            return null;
        }

        var frames = new List<ImageSource>(count);
        for (var i = 0; i < count; i++)
        {
            var frame = new CroppedBitmap(sheet, new System.Windows.Int32Rect(i * width, 0, width, sheet.PixelHeight));
            frame.Freeze();
            frames.Add(frame);
        }

        var fps = ReadDouble(entry, "fps") ?? 10;
        return new SpriteSheet(frames, 1 / Math.Max(fps, 1), ReadBool(entry, "pixel"));
    }

    private static Dictionary<string, JsonObject> Catalog()
    {
        if (_catalog is not null)
        {
            return _catalog;
        }
        _catalog = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        try
        {
            var text = AtomicFile.ReadAllTextOrNull(Path.Combine(SpriteFolder, "catalog.json"));
            if (text is not null && JsonNode.Parse(text) is JsonArray entries)
            {
                foreach (var node in entries)
                {
                    if (node is JsonObject entry && ReadString(entry, "id") is { } id)
                    {
                        if (!_catalog.ContainsKey(id))
                        {
                            CatalogOrder.Add(id);
                        }
                        _catalog[id] = entry;
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            System.Diagnostics.Debug.WriteLine("NotchTasks: sprite catalog unreadable: " + e);
        }
        return _catalog;
    }

    private static string SpriteFolder => Path.Combine(AppContext.BaseDirectory, "Sprites");

    private static string? ReadString(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    private static int? ReadInt(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue(out int i) ? i : null;

    private static double? ReadDouble(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue(out double d) ? d : null;

    private static bool ReadBool(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue(out bool b) && b;
}

/// One sprite on the pill (18 DIP square by default; the Icon Library draws bigger ones). It shows the frame that the shared
/// clock (SpriteClock.EpochMs) puts at this moment, so every sprite that started at the same Sync motion moment is in step.
/// It steps only while it is visible, and a step redraws the frame with InvalidateVisual, so no layout pass runs.
/// Animations run under reduced motion too, as the Mac's do (Animation.swift has no motion check).
public sealed class SpriteView : FrameworkElement
{
    private readonly SpriteSheet _sheet;
    private readonly double _size;
    private int _index;

    internal SpriteView(SpriteSheet sheet, double size = 18)
    {
        _sheet = sheet;
        _size = size;
        Width = size;
        Height = size;
        if (sheet.Pixel)
        {
            RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        }
        Loaded += (_, _) =>
        {
            Step();
            SpriteClock.Add(this);
        };
        Unloaded += (_, _) => SpriteClock.Remove(this);
        IsVisibleChanged += (_, _) => SpriteClock.Refresh();
    }

    internal double FrameSeconds => _sheet.FrameSeconds;

    /// How many frames the sprite has.
    internal int FrameCount => _sheet.Count;

    /// The frame on screen now.
    internal int CurrentIndex => _index;

    /// Milliseconds from `elapsedMs` (on the clock) to this sprite's next frame change; double.MaxValue when it does not change.
    internal double MsUntilNextFrame(double elapsedMs) => _sheet.MsUntilNextFrame(elapsedMs);

    protected override void OnRender(DrawingContext drawingContext)
    {
        var frame = _sheet.Frames[_index];
        // a frame keeps its shape inside the square, as the Mac's resizeAspect does (square sprites fill it, as before)
        var width = frame.Width;
        var height = frame.Height;
        if (!(width > 0) || !(height > 0))
        {
            drawingContext.DrawImage(frame, new Rect(0, 0, _size, _size));
            return;
        }
        var scale = Math.Min(_size / width, _size / height);
        var drawnWidth = width * scale;
        var drawnHeight = height * scale;
        drawingContext.DrawImage(frame, new Rect((_size - drawnWidth) / 2, (_size - drawnHeight) / 2, drawnWidth, drawnHeight));
    }

    /// Moves to the frame of the current moment. Frame 0 falls on the epoch and every loop after it.
    internal void Step()
    {
        var index = _sheet.IndexAt(SpriteClock.ElapsedMs());
        if (index != _index)
        {
            _index = index;
            InvalidateVisual();
        }
    }
}
