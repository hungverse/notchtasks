using NotchTasks.Core.Modules;
using NotchTasks.Core.Notch;
using Xunit;

namespace NotchTasks.Core.Tests;

/// The downloads module ports the Mac's scanDownloads (Model.swift): the first scan is the baseline, each later scan toasts
/// every finished file that is new and was made less than 30 minutes ago, in name order. Pure: the tests give explicit times
/// and names, and no real files.
public class DownloadsModuleTests
{
    /// An announcer that keeps every toast with its notify flag.
    private sealed class RecordingAnnouncer : IAnnouncer
    {
        public List<(Toast Toast, bool Notify)> Calls { get; } = new();

        public void Announce(Toast t, bool notify = true) => Calls.Add((t, notify));
    }

    private const string Folder = @"C:\Users\Test\Downloads";

    private static readonly DateTimeOffset Now = new(2026, 11, 19, 10, 0, 0, TimeSpan.Zero);

    private static (string Name, DateTimeOffset Created) Recent(string name) => (name, Now.AddMinutes(-1));

    private static (string Name, DateTimeOffset Created) Old(string name) => (name, Now.AddMinutes(-45));

    [Fact]
    public void Finished_SkipsPartialAndHidden()
    {
        var finished = DownloadNames.Finished(new[]
        {
            "setup.exe",
            "movie.mp4.crdownload",
            "file.download",
            "a.part",
            "b.partial",
            "c.tmp",
            "d.opdownload",
            "e.aria2",
            ".hidden.pdf",
            "notes.txt",
        });

        Assert.Equal(new[] { "notes.txt", "setup.exe" }, finished.OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void FirstScan_IsBaseline_NoToast()
    {
        var announcer = new RecordingAnnouncer();
        var module = new DownloadsModule(announcer, Folder);

        module.Scan(new[] { Recent("a.pdf") }, Now);

        Assert.Empty(announcer.Calls);
    }

    [Fact]
    public void NewFinishedFile_RecentlyMade_Announces()
    {
        var announcer = new RecordingAnnouncer();
        var module = new DownloadsModule(announcer, Folder);
        module.Scan(Array.Empty<(string, DateTimeOffset)>(), Now);

        module.Scan(new[] { Recent("report.pdf") }, Now);

        var (toast, notify) = Assert.Single(announcer.Calls);
        Assert.True(notify);
        Assert.Equal("download", toast.Glyph);
        Assert.Equal("#0A84FF", toast.Tint);
        Assert.Equal("Download finished", toast.Title);
        Assert.Equal("report.pdf", toast.Subtitle);
        Assert.Equal("downloadDone", toast.Event);
        Assert.Equal(Path.Combine(Folder, "report.pdf"), toast.Url);
        Assert.Equal("Show in folder", toast.Action);
        Assert.Equal("downloads", toast.Module);
        Assert.False(toast.Compact);
    }

    [Fact]
    public void OldFileMovedIn_Over30Min_NoToast()
    {
        var announcer = new RecordingAnnouncer();
        var module = new DownloadsModule(announcer, Folder);
        module.Scan(Array.Empty<(string, DateTimeOffset)>(), Now);

        module.Scan(new[] { Old("dragged-in.zip") }, Now);

        Assert.Empty(announcer.Calls);
    }

    [Fact]
    public void Rename_FromCrdownload_AnnouncesOnce()
    {
        var announcer = new RecordingAnnouncer();
        var module = new DownloadsModule(announcer, Folder);
        module.Scan(Array.Empty<(string, DateTimeOffset)>(), Now);
        module.Scan(new[] { Recent("big.iso.crdownload") }, Now);
        Assert.Empty(announcer.Calls);

        module.Scan(new[] { Recent("big.iso") }, Now);
        module.Scan(new[] { Recent("big.iso") }, Now);

        var (toast, _) = Assert.Single(announcer.Calls);
        Assert.Equal("big.iso", toast.Subtitle);
    }

    [Fact]
    public void Burst_OfTwentyFiles_TwentyToastsInNameOrder()
    {
        var announcer = new RecordingAnnouncer();
        var module = new DownloadsModule(announcer, Folder);
        module.Scan(Array.Empty<(string, DateTimeOffset)>(), Now);

        var names = Enumerable.Range(1, 20).Select(i => $"file{i:00}.txt").Reverse().ToList();
        module.Scan(names.Select(Recent), Now);

        Assert.Equal(20, announcer.Calls.Count);
        var subtitles = announcer.Calls.Select(c => c.Toast.Subtitle).ToList();
        Assert.Equal(
            Enumerable.Range(1, 20).Select(i => $"file{i:00}.txt").ToList(),
            subtitles);
    }

    [Fact]
    public void CaseOnlyRename_NoToast()
    {
        var announcer = new RecordingAnnouncer();
        var module = new DownloadsModule(announcer, Folder);
        module.Scan(new[] { Recent("Report.PDF") }, Now);

        module.Scan(new[] { Recent("report.pdf") }, Now);

        Assert.Empty(announcer.Calls);
    }

    [Fact]
    public void Reset_ForgetsBaseline_NextScanIsBaselineAgain()
    {
        var announcer = new RecordingAnnouncer();
        var module = new DownloadsModule(announcer, Folder);
        module.Scan(new[] { Recent("a.pdf") }, Now);

        module.Reset();
        module.Scan(new[] { Recent("a.pdf"), Recent("b.pdf") }, Now);
        Assert.Empty(announcer.Calls);

        module.Scan(new[] { Recent("a.pdf"), Recent("b.pdf"), Recent("c.pdf") }, Now);
        var (toast, _) = Assert.Single(announcer.Calls);
        Assert.Equal("c.pdf", toast.Subtitle);
    }
}
