using System.Globalization;
using NotchTasks.Core.Agents;

namespace NotchTasks.Core.Ci;

/// Downloads and copies. Port of electron/src/core/devscan.js FileProgressWatcher (lines 565-622). The Mac reads
/// NSProgress; Windows has no such feed, so this polls the top level of the usual folders from the directory
/// listing only: partial downloads (.crdownload/.part/.download/.partial) and big files that keep growing.
/// No total is known, so no percentage. Subfolders are not watched.
public sealed class FileProgressWatcher
{
    private const long MinTransfer = 5_000_000;   // small files finish before anyone looks
    private const long StallMs = 8000;
    private const string TransferTool = "Tải file";
    private static readonly string[] PartialExtensions = { ".crdownload", ".part", ".download", ".partial" };

    private readonly IReadOnlyList<string> folders;
    private readonly Func<long> nowMs;
    private readonly Dictionary<string, Seen> seen = new(StringComparer.Ordinal);

    /// folders: the directories to poll. nowMs: the clock in milliseconds (a manual clock in tests).
    public FileProgressWatcher(IReadOnlyList<string> folders, Func<long> nowMs)
    {
        this.folders = folders;
        this.nowMs = nowMs;
    }

    /// Downloads, Desktop, Documents, Videos, Pictures and Music under the home folder.
    public static IReadOnlyList<string> DefaultFolders(string home) =>
        new[] { "Downloads", "Desktop", "Documents", "Videos", "Pictures", "Music" }
            .Select(folder => Path.Combine(home, folder))
            .ToList();

    /// Active transfers now. Call once per tick. A folder that cannot be listed is skipped.
    public IReadOnlyList<Job> Jobs()
    {
        var t = nowMs();
        var result = new List<Job>();
        var live = new HashSet<string>(StringComparer.Ordinal);

        foreach (var dir in folders)
        {
            List<FileInfo> files;
            try
            {
                files = new DirectoryInfo(dir).EnumerateFiles().ToList();
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                var full = file.FullName;
                var name = file.Name;
                live.Add(full);
                var partial = IsPartial(name);
                var size = file.Length;
                var mtime = new DateTimeOffset(file.LastWriteTimeUtc).ToUnixTimeMilliseconds();

                var hadPrev = seen.TryGetValue(full, out var prev);
                var changed = hadPrev && prev.Size != size;
                long? lastChange = hadPrev
                    ? (changed ? t : prev.LastChange)
                    : (partial ? mtime : null);
                double? speed = changed
                    ? (size - prev.Size) / Math.Max(0.001, (t - prev.At) / 1000.0)
                    : (hadPrev ? prev.Speed : null);
                seen[full] = new Seen(size, t, lastChange, speed);

                if (size < MinTransfer || lastChange is null || t - lastChange.Value >= StallMs)
                {
                    continue;
                }

                var detail = SizeText(size) + (speed is > 0 ? " · " + SizeText(speed.Value) + "/s" : string.Empty);
                result.Add(new Job(
                    Id: "file:" + full,
                    Title: (partial ? L10n.L("Downloads") : L10n.L("Copy")) + " · " + name,
                    Detail: detail,
                    Working: true,
                    Symbol: partial ? "arrow.down.circle.fill" : "doc.on.doc.fill",
                    Tool: TransferTool));
            }
        }

        foreach (var key in seen.Keys.ToList())
        {
            if (!live.Contains(key))
            {
                seen.Remove(key);
            }
        }

        return result;
    }

    /// Forgets every file seen so far.
    public void Stop() => seen.Clear();

    /// Bytes as "1.2 GB", "3.4 MB", "12 KB" or "512 B" (invariant culture).
    public static string SizeText(double bytes)
    {
        if (bytes >= 1e9)
        {
            return (bytes / 1e9).ToString("F1", CultureInfo.InvariantCulture) + " GB";
        }
        if (bytes >= 1e6)
        {
            return (bytes / 1e6).ToString("F1", CultureInfo.InvariantCulture) + " MB";
        }
        if (bytes >= 1e3)
        {
            return (bytes / 1e3).ToString("F0", CultureInfo.InvariantCulture) + " KB";
        }
        return bytes.ToString("0.###", CultureInfo.InvariantCulture) + " B";
    }

    private static bool IsPartial(string name)
    {
        var ext = Path.GetExtension(name);
        foreach (var partial in PartialExtensions)
        {
            if (string.Equals(ext, partial, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// What was seen of one file on the last poll. LastChange is null for a plain file not yet seen growing.
    private readonly record struct Seen(long Size, long At, long? LastChange, double? Speed);
}
