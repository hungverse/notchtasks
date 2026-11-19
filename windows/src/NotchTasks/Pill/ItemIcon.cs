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

namespace NotchTasks.Pill;

/// The icon of an agent item (Mac ItemIcon). An app or process item shows its program's own small icon: the exe of the process
/// it matched (its pid is the first job's Id), else the item's Exe file. The icons are cached by path. Any other item, and any
/// icon that cannot be read, shows the sparkles glyph in the tint.
public static class ItemIcon
{
    private const string SegoeFont = "Segoe Fluent Icons, Segoe MDL2 Assets";
    private static readonly FontFamily SegoeFamily = new(SegoeFont);
    private static readonly object Gate = new();
    private static readonly Dictionary<string, ImageSource?> IconsByPath = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<int, string?> PathsByPid = new();

    /// The icon as an element of size × size: the program's icon as an Image, else the sparkles glyph in the tint.
    public static FrameworkElement Element(ItemState state, double size, Color tint, IProcessReader? processes)
    {
        if (state.Item.Kind is WatchKind.App or WatchKind.Process && Source(state, processes) is { } source)
        {
            return new Image
            {
                Source = source,
                Width = size,
                Height = size,
                VerticalAlignment = VerticalAlignment.Center,
                SnapsToDevicePixels = true,
            };
        }
        return new TextBlock
        {
            Text = ModuleGlyphs.Get("sparkles"),
            FontFamily = SegoeFamily,
            FontSize = size * 0.8,
            Foreground = new SolidColorBrush(tint),
            Width = size,
            Height = size,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    /// The program's icon for an item, or null: the matched process's image, else the item's Exe, read once per path.
    internal static ImageSource? Source(ItemState state, IProcessReader? processes)
    {
        var path = ProgramPath(state, processes);
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

    /// The image path of the item's process, else its Exe when that file exists.
    private static string? ProgramPath(ItemState state, IProcessReader? processes)
    {
        if (Pid(state) is { } pid && processes is not null)
        {
            var path = ImagePathOf(pid, processes);
            if (path is not null)
            {
                return path;
            }
        }
        return state.Item.Exe is { Length: > 0 } exe && File.Exists(exe) ? exe : null;
    }

    /// The pid of an app or process item: its first job's Id. Null when that is not a number.
    internal static int? Pid(ItemState state) =>
        state.Jobs.Count > 0 && int.TryParse(state.Jobs[0].Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid)
            ? pid
            : null;

    /// The image path of a process, looked up in the process table once per pid (null when the process is gone or unreadable).
    private static string? ImagePathOf(int pid, IProcessReader processes)
    {
        lock (Gate)
        {
            if (PathsByPid.TryGetValue(pid, out var known))
            {
                return known;
            }
        }
        string? path = null;
        if (processes.Snapshot().ById.TryGetValue(pid, out var info))
        {
            path = processes.ImagePath(info);
        }
        lock (Gate)
        {
            PathsByPid[pid] = path;
        }
        return path;
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

/// The two shell calls the item icon needs. LibraryImport generates the marshalling.
internal static partial class ShellNative
{
    [LibraryImport("shell32.dll", EntryPoint = "SHGetFileInfoW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint SHGetFileInfo(string path, uint attributes, nint info, uint infoSize, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyIcon(nint icon);
}
