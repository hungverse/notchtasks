using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NotchTasks.Pill;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace NotchTasks.Services;

/// The pictures of the shelf's tiles. Get returns the Windows icon at once: a file type's icon, cached by extension, or the
/// folder icon. Then, for a file, the WinRT thumbnail is read in the background and handed to ready on the UI thread. Thumbnails
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
    private const uint ShgfiSmallIcon = 0x1;

    private static readonly Dictionary<string, ImageSource?> IconsByKey = new(StringComparer.OrdinalIgnoreCase);
    /// The thumbnails, least recently used first.
    private static readonly List<(string Path, long Modified, ImageSource Image)> Thumbs = new();

    /// The picture of a path now: a cached thumbnail when it is current, else the icon. For a file that has no current thumbnail,
    /// ready is called later, on the UI thread, with the thumbnail (when Windows makes one). Null when Windows gives no icon.
    public static ImageSource? Get(string path, Action<ImageSource> ready)
    {
        if (Directory.Exists(path))
        {
            return IconFor(FolderKey, "dir", FileAttributeDirectory);
        }
        var modified = File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : 0;
        if (modified != 0 && Thumb(path, modified) is { } cached)
        {
            return cached;
        }
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var icon = IconFor(extension, "file" + extension, FileAttributeNormal);
        if (modified != 0)
        {
            LoadThumbAsync(path, modified, ready);
        }
        return icon;
    }

    /// The icon of a file type, read through its name alone (SHGFI_USEFILEATTRIBUTES), once per key.
    private static ImageSource? IconFor(string key, string nameForType, uint attributes)
    {
        if (IconsByKey.TryGetValue(key, out var known))
        {
            return known;
        }
        var icon = ItemIcon.ShellIcon(nameForType, attributes, ShgfiIcon | ShgfiSmallIcon | ShgfiUseFileAttributes);
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

    /// Reads the file's WinRT thumbnail on the UI thread's await, so ready runs there. A file Windows cannot make a thumbnail of
    /// keeps its icon.
    private static async void LoadThumbAsync(string path, long modified, Action<ImageSource> ready)
    {
        var image = await ReadThumbAsync(path);
        if (image is null)
        {
            return;
        }
        Remember(path, modified, image);
        ready(image);
    }

    /// The thumbnail of a file as a frozen image, or null when Windows makes none. Outside the callback, so a failing ready is not
    /// mistaken for a missing thumbnail.
    private static async Task<ImageSource?> ReadThumbAsync(string path)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, ThumbSize);
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
            return null;  // no thumbnail for this file: the tile keeps its icon
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
