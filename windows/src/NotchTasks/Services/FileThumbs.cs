using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NotchTasks.Pill;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace NotchTasks.Services;

/// The pictures of the shelf's tiles. Get returns the Windows icon at once: the large icon of a file type (cached by extension) or of
/// a folder. Then the WinRT thumbnail of a file or a folder is read in the background and handed to ready on the UI thread. Thumbnails
/// are cached by path and modified time, the last 64. Everything runs on the UI thread.
public static class FileThumbs
{
    private const int ThumbSize = 80;
    private const int Capacity = 64;
    private const string FolderKey = "<dir>";
    private const uint FileAttributeNormal = 0x80;
    private const uint FileAttributeDirectory = 0x10;
    private const uint ShgfiUseFileAttributes = 0x10;
    private const uint ShgfiIcon = 0x100;
    // No SHGFI_SMALLICON: the large icon (32 px), which a 40 px tile shows at its size rather than stretched from 16 px.
    private const uint ShgfiLargeIconFlags = ShgfiIcon | ShgfiUseFileAttributes;

    private static readonly Dictionary<string, ImageSource?> IconsByKey = new(StringComparer.OrdinalIgnoreCase);
    /// The thumbnails, least recently used first.
    private static readonly List<(string Path, long Modified, ImageSource Image)> Thumbs = new();

    /// Reads the WinRT thumbnail of a path (the bool is true for a folder). The default reads it from Windows; a test replaces it.
    public static Func<string, bool, Task<ImageSource?>> ReadThumbnail { get; set; } = ReadThumbAsync;

    /// The picture of a path now: a cached thumbnail when it is current, else the large icon. For a file or a folder that has no
    /// current thumbnail, ready is called later, on the UI thread, with the thumbnail (when Windows makes one). Null when Windows gives no icon.
    public static ImageSource? Get(string path, Action<ImageSource> ready)
    {
        var folder = Directory.Exists(path);
        var present = folder || File.Exists(path);
        var modified = !present ? 0 : (folder ? Directory.GetLastWriteTimeUtc(path) : File.GetLastWriteTimeUtc(path)).Ticks;
        if (modified != 0 && Thumb(path, modified) is { } cached)
        {
            return cached;
        }
        var icon = folder
            ? IconFor(FolderKey, "dir", FileAttributeDirectory)
            : IconFor(Path.GetExtension(path).ToLowerInvariant(), "file" + Path.GetExtension(path).ToLowerInvariant(), FileAttributeNormal);
        if (modified != 0)
        {
            LoadThumbAsync(path, folder, modified, ready);
        }
        return icon;
    }

    /// The large icon of a file type or a folder, read through its name alone (SHGFI_USEFILEATTRIBUTES), once per key.
    private static ImageSource? IconFor(string key, string nameForType, uint attributes)
    {
        if (IconsByKey.TryGetValue(key, out var known))
        {
            return known;
        }
        var icon = ItemIcon.ShellIcon(nameForType, attributes, ShgfiLargeIconFlags);
        IconsByKey[key] = icon;
        return icon;
    }

    private static ImageSource? Thumb(string path, long modified)
    {
        var index = Thumbs.FindIndex(t => t.Path == path && t.Modified == modified);
        if (index < 0)
        {
            return null;
        }
        var entry = Thumbs[index];
        Thumbs.RemoveAt(index);
        Thumbs.Add(entry);  // the most recently used moves last
        return entry.Image;
    }

    /// Reads the thumbnail and hands it to ready. The await resumes on the UI thread, where ready runs. A path Windows cannot make a
    /// thumbnail of keeps its icon.
    private static async void LoadThumbAsync(string path, bool folder, long modified, Action<ImageSource> ready)
    {
        var image = await ReadThumbnail(path, folder);
        if (image is null)
        {
            return;
        }
        Remember(path, modified, image);
        ready(image);
    }

    /// The thumbnail of a file or folder as a frozen image, or null when Windows makes none. Outside the callback, so a failing ready is
    /// not mistaken for a missing thumbnail.
    private static async Task<ImageSource?> ReadThumbAsync(string path, bool folder)
    {
        try
        {
            using var thumbnail = folder
                ? await (await StorageFolder.GetFolderFromPathAsync(path)).GetThumbnailAsync(ThumbnailMode.SingleItem, ThumbSize)
                : await (await StorageFile.GetFileFromPathAsync(path)).GetThumbnailAsync(ThumbnailMode.SingleItem, ThumbSize);
            if (thumbnail is null)
            {
                return null;
            }
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = thumbnail.AsStream();
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or IOException or UnauthorizedAccessException
            or InvalidOperationException or NotSupportedException or ArgumentException)
        {
            return null;  // no thumbnail for this path: the tile keeps its icon
        }
    }

    private static void Remember(string path, long modified, ImageSource image)
    {
        Thumbs.RemoveAll(t => t.Path == path);
        Thumbs.Add((path, modified, image));
        while (Thumbs.Count > Capacity)
        {
            Thumbs.RemoveAt(0);
        }
    }
}
