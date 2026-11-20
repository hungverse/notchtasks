using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NotchTasks.Pill;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace NotchTasks.Services;

/// The pictures of the shelf's tiles. Get returns the large icon of the path's type at once (cached by extension), with no disk access on
/// the UI thread. In the background the path is checked (a file or a folder, its modified time), and the picture that fits is handed to
/// ready on the UI thread: the cached thumbnail when it is current, else the folder's icon for a folder, else the WinRT thumbnail when
/// Windows makes one. Thumbnails are cached by path and modified time, the last 64. An offline share therefore cannot freeze the pill.
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

    /// What the disk says about a path: whether it is a folder, and its modified time in ticks (0 when it is not there now). Read in the
    /// background. A test replaces it, to show that no disk read runs on the UI thread.
    public static Func<string, (bool Folder, long Modified)> Probe { get; set; } = DiskProbe;

    /// The picture of a path now: the large icon of its type, with no disk access. Then, in the background, the path is checked and
    /// ready is called on the UI thread with the picture that fits (see the class comment). Null when Windows gives no icon.
    public static ImageSource? Get(string path, Action<ImageSource> ready)
    {
        var icon = IconFor(Path.GetExtension(path).ToLowerInvariant(), "file" + Path.GetExtension(path).ToLowerInvariant(), FileAttributeNormal);
        _ = Resolve(path, ready);
        return icon;
    }

    /// The background part of Get: the disk check, then the cached thumbnail, the folder's icon, or the WinRT thumbnail. Runs its
    /// continuations on the UI thread, where ready is called.
    private static async Task Resolve(string path, Action<ImageSource> ready)
    {
        (bool Folder, long Modified) probe;
        try
        {
            probe = await Task.Run(() => Probe(path)).ConfigureAwait(true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return;  // the disk cannot say: the tile keeps the type icon
        }
        if (probe.Modified == 0)
        {
            return;  // not there now: the tile keeps the type icon
        }
        if (Thumb(path, probe.Modified) is { } cached)
        {
            ready(cached);
            return;
        }
        if (probe.Folder && IconFor(FolderKey, "dir", FileAttributeDirectory) is { } folderIcon)
        {
            ready(folderIcon);
        }
        await LoadThumbAsync(path, probe.Folder, probe.Modified, ready).ConfigureAwait(true);
    }

    /// The default probe: a folder or a file, and its modified time. Missing paths read as 0.
    private static (bool Folder, long Modified) DiskProbe(string path)
    {
        var folder = Directory.Exists(path);
        var present = folder || File.Exists(path);
        var modified = !present ? 0 : (folder ? Directory.GetLastWriteTimeUtc(path) : File.GetLastWriteTimeUtc(path)).Ticks;
        return (folder, modified);
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
    private static async Task LoadThumbAsync(string path, bool folder, long modified, Action<ImageSource> ready)
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
