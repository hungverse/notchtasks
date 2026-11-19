using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NotchTasks.Core.Agents;
using NotchTasks.Services;

namespace NotchTasks.Pill;

/// The icon of an agent item (Mac ItemIcon). An app or process item shows its program's own small icon: the image of the process
/// it matched (its pid is the first job's Id), else the item's exe when that is a file. The process table is read off the UI
/// thread: a pid's path is looked up in the background and shows on the next publish; a missing path is looked up again after a
/// while. Any other item, and any icon that cannot be read, shows the sparkles glyph in the tint.
public static class ItemIcon
{
    /// How long a pid with no path is kept before it is looked up again.
    private static readonly TimeSpan NoPathAgain = TimeSpan.FromSeconds(30);
    private const string SegoeFont = "Segoe Fluent Icons, Segoe MDL2 Assets";
    private static readonly FontFamily SegoeFamily = new(SegoeFont);
    private static readonly object Gate = new();
    private static readonly Dictionary<string, ImageSource?> IconsByPath = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<int, (string? Path, DateTimeOffset At)> PathsByPid = new();
    private static readonly HashSet<int> Resolving = new();
    private static readonly Dictionary<string, bool> FileExists = new(StringComparer.OrdinalIgnoreCase);

    /// The icon as an element of size by size: the program's icon as an Image, else the sparkles glyph in the tint.
    public static FrameworkElement Element(ItemState state, double size, Color tint, IProcessReader? processes)
    {
        if (Source(state, processes) is { } source)
        {
            return new Image
            {
                Source = source,
                Width = size,
                Height = size,
                VerticalAlignment = VerticalAlignment.Center,
                SnapsToDevicePixels = true,
                Focusable = false,
            };
        }
        return new TextBlock
        {
            Text = ModuleGlyphs.Get("sparkles"),
            FontFamily = SegoeFamily,
            FontSize = size * 0.8,
            Foreground = TintBrushes.Of(tint),
            Width = size,
            Height = size,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Focusable = false,
        };
    }

    /// The image path an item's icon comes from, or null. Cheap on every call: the lookups are cached, and a pid not yet known
    /// starts a background lookup. Shows the item's exe only when it is a file.
    public static string? PathOf(ItemState state, IProcessReader? processes)
    {
        if (state.Item.Kind is not (WatchKind.App or WatchKind.Process))
        {
            return null;
        }
        if (Pid(state) is { } pid && processes is not null && PidPath(pid, processes) is { } path)
        {
            return path;
        }
        return ExeFile(state.Item);
    }

    /// The program's icon for an item, or null: read once per path.
    internal static ImageSource? Source(ItemState state, IProcessReader? processes)
    {
        var path = PathOf(state, processes);
        if (path is null)
        {
            return null;
        }
        lock (Gate)
        {
            if (IconsByPath.TryGetValue(path, out var cached))
            {
                return cached;
            }
        }
        var icon = ShellIcon(path);
        lock (Gate)
        {
            IconsByPath[path] = icon;
        }
        return icon;
    }

    /// The pid of an app or process item: its first job's Id. Null when that is not a number.
    internal static int? Pid(ItemState state) =>
        state.Jobs.Count > 0 && int.TryParse(state.Jobs[0].Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid)
            ? pid
            : null;

    /// The item's exe as a file path, when it names one and the file exists. The existence check is made once per name.
    private static string? ExeFile(WatchItem item)
    {
        var exe = WatchItems.AppExe(item);
        if (exe is null || !Path.IsPathRooted(exe))
        {
            return null;
        }
        lock (Gate)
        {
            if (FileExists.TryGetValue(exe, out var known))
            {
                return known ? exe : null;
            }
        }
        var exists = File.Exists(exe);
        lock (Gate)
        {
            FileExists[exe] = exists;
        }
        return exists ? exe : null;
    }

    /// The image path of a pid from the cache. A path is kept for good; a missing one is kept for a while, and the lookup for an
    /// unknown pid runs in the background, so the calling (UI) thread never reads the process table.
    private static string? PidPath(int pid, IProcessReader processes)
    {
        lock (Gate)
        {
            if (PathsByPid.TryGetValue(pid, out var known))
            {
                if (known.Path is not null || DateTimeOffset.UtcNow - known.At < NoPathAgain)
                {
                    return known.Path;
                }
            }
            if (!Resolving.Add(pid))
            {
                return null;
            }
        }
        _ = Task.Run(() => Resolve(pid, processes));
        return null;
    }

    /// Looks a pid up in the process table and its image path, off the UI thread, and caches the result.
    private static void Resolve(int pid, IProcessReader processes)
    {
        string? path = null;
        try
        {
            if (processes.Snapshot().ById.TryGetValue(pid, out var info))
            {
                path = processes.ImagePath(info);
            }
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or ArgumentException)
        {
            ErrorLog.Write(e);
        }
        finally
        {
            lock (Gate)
            {
                Resolving.Remove(pid);
                PathsByPid[pid] = (path, DateTimeOffset.UtcNow);
            }
        }
    }

    /// The small icon of a file through SHGetFileInfo, as an image. Null when Windows gives none.
    private static ImageSource? ShellIcon(string path)
    {
        var info = new byte[ShellInfoSize];
        var pinned = GCHandle.Alloc(info, GCHandleType.Pinned);
        try
        {
            var address = pinned.AddrOfPinnedObject();
            if (ShellNative.SHGetFileInfo(path, 0, address, (uint)info.Length, ShgfiIcon | ShgfiSmallIcon) == 0)
            {
                return null;
            }
            var icon = Marshal.ReadIntPtr(address);  // SHFILEINFO.hIcon is the first field
            if (icon == 0)
            {
                return null;
            }
            try
            {
                var source = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally
            {
                ShellNative.DestroyIcon(icon);
            }
        }
        catch (Exception e) when (e is Win32Exception or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
        finally
        {
            pinned.Free();
        }
    }

    private const uint ShgfiIcon = 0x100;
    private const uint ShgfiSmallIcon = 0x1;

    /// sizeof(SHFILEINFOW) on 64-bit Windows: the icon, the index, the attributes, and two wide-char name buffers.
    private const int ShellInfoSize = 696;
}

/// One frozen brush per colour, so a card or a row reuses the brush of its tint instead of making one each publish.
public static class TintBrushes
{
    private static readonly Dictionary<Color, SolidColorBrush> Brushes = new();
    private static readonly object Gate = new();

    public static SolidColorBrush Of(Color color)
    {
        lock (Gate)
        {
            if (!Brushes.TryGetValue(color, out var brush))
            {
                brush = new SolidColorBrush(color);
                brush.Freeze();
                Brushes[color] = brush;
            }
            return brush;
        }
    }
}

/// The two shell calls the item icon needs. LibraryImport generates the marshalling.
internal static partial class ShellNative
{
    [LibraryImport("shell32.dll", EntryPoint = "SHGetFileInfoW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint SHGetFileInfo(string path, uint attributes, nint info, uint infoSize, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyIcon(nint icon);
}
