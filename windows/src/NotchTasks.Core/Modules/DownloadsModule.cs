using System.Runtime.InteropServices;
using NotchTasks.Core.Notch;

namespace NotchTasks.Core.Modules;

/// Which names in the Downloads folder are finished files (Mac: Downloads.finished). A browser's partial download has one of
/// the partial extensions, and a hidden file starts with a dot: neither is finished.
public static class DownloadNames
{
    private static readonly HashSet<string> PartialExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "crdownload", "download", "part", "partial", "tmp", "opdownload", "aria2",
    };

    /// The finished names of `names`. Windows file names are case-insensitive, so the set is too.
    public static IReadOnlySet<string> Finished(IEnumerable<string> names) =>
        new HashSet<string>(names.Where(IsFinished), StringComparer.OrdinalIgnoreCase);

    /// True when the name is neither hidden nor a partial download.
    public static bool IsFinished(string name)
    {
        if (name.Length == 0 || name[0] == '.')
        {
            return false;
        }
        var extension = Path.GetExtension(name);
        return !(extension.Length > 1 && PartialExtensions.Contains(extension[1..]));
    }
}

/// The Downloads folder of the current user, from the shell (SHGetKnownFolderPath). Null when the shell cannot give it.
public static class DownloadFolder
{
    private static readonly Guid DownloadsId = new("374DE290-123F-4565-9164-39C4925E467B");

    /// The full path of the Downloads folder, or null when it cannot be read.
    public static string? Locate()
    {
        var hr = NativeMethods.SHGetKnownFolderPath(DownloadsId, 0, 0, out var path);
        if (hr != 0 || path == 0)
        {
            return null;
        }
        try
        {
            return Marshal.PtrToStringUni(path);
        }
        finally
        {
            NativeMethods.CoTaskMemFree(path);
        }
    }
}

/// The downloads module (Mac: scanDownloads in Model.swift). The first scan is the baseline, with no toast. Each later scan
/// toasts, in name order, every finished file that was not in the previous scan and was made less than 30 minutes ago. The
/// new set always replaces the old one. Pure: the source lists the folder and passes the names and creation times. The
/// full path is Path.Combine(folder, name).
public sealed class DownloadsModule
{
    /// A file made longer ago than this was moved in from elsewhere, not a download that just finished.
    public static readonly TimeSpan RecentWindow = TimeSpan.FromMinutes(30);

    private readonly IAnnouncer _announce;
    private HashSet<string>? _known;

    /// The toasts go to the announcer. folder is the Downloads folder; the toasts' paths are inside it.
    public DownloadsModule(IAnnouncer announce, string folder)
    {
        _announce = announce;
        Folder = folder;
    }

    /// The folder that is watched.
    public string Folder { get; }

    /// Records a scan of the folder: the files with their creation times, at `now`. The first scan after a Reset is the
    /// baseline and makes no toast.
    public void Scan(IEnumerable<(string Name, DateTimeOffset Created)> entries, DateTimeOffset now)
    {
        var made = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, created) in entries)
        {
            if (DownloadNames.IsFinished(name))
            {
                made.TryAdd(name, created);
            }
        }

        var before = _known;
        _known = new HashSet<string>(made.Keys, StringComparer.OrdinalIgnoreCase);
        if (before is null)
        {
            return;
        }

        foreach (var name in made.Keys
                     .Where(name => !before.Contains(name))
                     .OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
        {
            if (now - made[name] >= RecentWindow)
            {
                continue;
            }
            _announce.Announce(
                new Toast(
                    Glyph: "download",
                    Tint: "#0A84FF",
                    Title: L10n.L("Download finished"),
                    Subtitle: name,
                    Event: "downloadDone",
                    Url: Path.Combine(Folder, name),
                    Action: L10n.L("Show in folder"),
                    Module: "downloads"),
                notify: true);
        }
    }

    /// Forgets the baseline without a toast: the module is off. The next scan is a baseline again.
    public void Reset() => _known = null;
}
