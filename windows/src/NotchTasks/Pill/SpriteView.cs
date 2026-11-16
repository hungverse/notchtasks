using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NotchTasks.Core;

namespace NotchTasks.Pill;

/// The frames of one sprite: a horizontal strip cut into equal frames (Mac Frames.strip).
internal sealed class SpriteSheet
{
    public SpriteSheet(IReadOnlyList<ImageSource> frames, double frameSeconds, bool pixel)
    {
        Frames = frames;
        FrameSeconds = frameSeconds;
        Pixel = pixel;
    }

    public IReadOnlyList<ImageSource> Frames { get; }

    public double FrameSeconds { get; }

    public bool Pixel { get; }
}

/// Loads sprites from Sprites\catalog.json and Sprites\<id>.png, once per id. Null when the sprite is unknown or missing.
internal static class SpriteCache
{
    private static readonly Dictionary<string, SpriteSheet?> Cache = new(StringComparer.Ordinal);
    private static Dictionary<string, JsonObject>? _catalog;

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

/// One sprite on the pill. It steps through its frames when SpriteClock ticks, and only while it is visible.
public sealed class SpriteView : Image
{
    private readonly SpriteSheet _sheet;
    private int _index;

    internal SpriteView(SpriteSheet sheet)
    {
        _sheet = sheet;
        Width = 18;
        Height = 18;
        Stretch = Stretch.Uniform;
        Source = sheet.Frames[0];
        if (sheet.Pixel)
        {
            RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        }
        Loaded += (_, _) => SpriteClock.Add(this);
        Unloaded += (_, _) => SpriteClock.Remove(this);
        IsVisibleChanged += (_, _) => SpriteClock.Refresh();
    }

    internal double FrameSeconds => _sheet.FrameSeconds;

    /// Clock ticks per frame: the shared clock runs at the fastest visible sprite's frame time.
    internal int Stride { get; set; } = 1;

    internal int TicksSinceStep { get; set; }

    internal void Step()
    {
        if (_sheet.Frames.Count <= 1)
        {
            return;
        }
        _index = (_index + 1) % _sheet.Frames.Count;
        Source = _sheet.Frames[_index];
    }
}
