using System.ComponentModel;
using System.Diagnostics;
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
    /// How long a found path is kept before its pid is looked up again: a pid is reused once its program has exited.
    private static readonly TimeSpan FoundPathAgain = TimeSpan.FromMinutes(10);
    private const string SegoeFont = "Segoe Fluent Icons, Segoe MDL2 Assets";
    private static readonly FontFamily SegoeFamily = new(SegoeFont);
    private static readonly object Gate = new();
    private static readonly Dictionary<string, ImageSource?> IconsByPath = new(StringComparer.OrdinalIgnoreCase);
    /// The path a pid's program had, keyed by the pid and the program's start time (ProcInfo.StartTicks), so a reused pid never
    /// takes the path of the program that had it before.
    private static readonly Dictionary<(int Pid, long Start), (string? Path, DateTimeOffset At)> PathsByPid = new();
    /// The start time of the program a pid was last looked up as.
    private static readonly Dictionary<int, long> StartByPid = new();
    private static readonly HashSet<int> Resolving = new();

    /// The clock the path cache reads. Tests set it to run the expiry without waiting.
    public static Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;
    private static readonly Dictionary<string, bool> FileExists = new(StringComparer.OrdinalIgnoreCase);
    /// The icon of a program by its exe name, read by LoadExeIconAsync (a null is kept: no running process).
    private static readonly Dictionary<string, ImageSource?> IconsByExe = new(StringComparer.OrdinalIgnoreCase);

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

    /// The icon of a program by its exe name (the Now Playing card and chip use the media app's), or null until LoadExeIconAsync has read
    /// it. A lookup in a table: it never reads the process table.
    public static ImageSource? ExeIcon(string exe)
    {
        lock (Gate)
        {
            return IconsByExe.TryGetValue(exe, out var icon) ? icon : null;
        }
    }

    /// True once an exe's icon has been read (a null icon counts: a program with no running process has none).
    public static bool HasExeIcon(string exe)
    {
        lock (Gate)
        {
            return IconsByExe.ContainsKey(exe);
        }
    }

    /// Stores an exe's icon. LoadExeIconAsync calls it; a test seeds it.
    public static void CacheExeIcon(string exe, ImageSource? icon)
    {
        lock (Gate)
        {
            IconsByExe[exe] = icon;
        }
    }

    /// Reads an exe's icon from its running process (the first process of that name with a path), on the background, and caches it.
    public static Task LoadExeIconAsync(string exe) => Task.Run(() =>
    {
        var path = RunningPath(exe);
        CacheExeIcon(exe, path is null ? null : ShellIcon(path));
    });

    /// The image path of the first running process with the exe's name, or null when none can be read (no process, or access denied).
    private static string? RunningPath(string exe)
    {
        var processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe));
        try
        {
            foreach (var process in processes)
            {
                try
                {
                    if (process.MainModule?.FileName is { } file)
                    {
                        return file;
                    }
                }
                catch (Exception e) when (e is Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    // the process exited, or its path is not ours to read: the next one
                }
            }
            return null;
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
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

    /// The image path of a pid from the cache. A found path is kept for ten minutes and a missing one for a while; after that the
    /// lookup runs again. The lookup for an unknown pid runs in the background, so the calling (UI) thread never reads the process
    /// table. A pid reused by another program keeps its old path only until that path expires and the pid is looked up again.
    private static string? PidPath(int pid, IProcessReader processes)
    {
        // an expired path is shown until its lookup lands, so the icon does not flicker while it refreshes
        string? last = null;
        lock (Gate)
        {
            if (StartByPid.TryGetValue(pid, out var start) && PathsByPid.TryGetValue((pid, start), out var known))
            {
                var age = Clock() - known.At;
                if (known.Path is not null ? age < FoundPathAgain : age < NoPathAgain)
                {
                    return known.Path;
                }
                last = known.Path;
            }
            if (!Resolving.Add(pid))
            {
                return last;
            }
        }
        _ = Task.Run(() => Resolve(pid, processes));
        return last;
    }

    /// Looks a pid up in the process table, off the UI thread, and caches its image path under the pid and the program's start
    /// time. The snapshot also prunes every entry of a pid it no longer shows, so the cache does not grow with every pid ever
    /// seen. A pid that is gone leaves no entry.
    private static void Resolve(int pid, IProcessReader processes)
    {
        string? path = null;
        long start = 0;
        var running = false;
        try
        {
            var snapshot = processes.Snapshot();
            if (snapshot.ById.TryGetValue(pid, out var info))
            {
                running = true;
                start = info.StartTicks;
                path = processes.ImagePath(info);
            }
            lock (Gate)
            {
                foreach (var key in PathsByPid.Keys.Where(k => !snapshot.ById.ContainsKey(k.Pid)).ToList())
                {
                    PathsByPid.Remove(key);
                }
                foreach (var gone in StartByPid.Keys.Where(p => !snapshot.ById.ContainsKey(p)).ToList())
                {
                    StartByPid.Remove(gone);
                }
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
                // a pid now belongs to this start time: the entries of an earlier program with this pid go
                foreach (var key in PathsByPid.Keys.Where(k => k.Pid == pid && (!running || k.Start != start)).ToList())
                {
                    PathsByPid.Remove(key);
                }
                if (running)
                {
                    PathsByPid[(pid, start)] = (path, Clock());
                    StartByPid[pid] = start;
                }
                else
                {
                    StartByPid.Remove(pid);
                }
            }
        }
    }

    /// The number of cached entries for a pid (tests: a gone pid leaves none).
    public static int CachedEntryCount(int pid)
    {
        lock (Gate)
        {
            return PathsByPid.Keys.Count(k => k.Pid == pid) + (StartByPid.ContainsKey(pid) ? 1 : 0);
        }
    }

    /// The small icon of a file through SHGetFileInfo, as an image. Null when Windows gives none. attributes and flags are
    /// SHGetFileInfo's (the shelf asks for a file type's icon by name, with SHGFI_USEFILEATTRIBUTES, and no file is read).
    internal static ImageSource? ShellIcon(string path, uint attributes = 0, uint flags = ShgfiIcon | ShgfiSmallIcon)
    {
        var info = new byte[ShellInfoSize];
        var pinned = GCHandle.Alloc(info, GCHandleType.Pinned);
        try
        {
            var address = pinned.AddrOfPinnedObject();
            if (ShellNative.SHGetFileInfo(path, attributes, address, (uint)info.Length, flags) == 0)
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
