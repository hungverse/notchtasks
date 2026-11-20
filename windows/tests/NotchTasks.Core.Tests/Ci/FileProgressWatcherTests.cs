using NotchTasks.Core.Ci;
using Xunit;

namespace NotchTasks.Core.Tests.Ci;

// Ported from electron/test/devscan.test.js: the FileProgressWatcher case, plus the Windows cases from the Phase 3c brief.
// Temp folders only, and a manual clock. Never the real Downloads or Desktop.
[Collection("L10n")]
public sealed class FileProgressWatcherTests : IDisposable
{
    private readonly string dir;

    public FileProgressWatcherTests()
    {
        L10n.Init(AppContext.BaseDirectory, "en");
        dir = Path.Combine(Path.GetTempPath(), "nt-dl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
    }

    public void Dispose()
    {
        Directory.Delete(dir, recursive: true);
    }

    // ---- helpers -------------------------------------------------------------

    private static void Grow(string file, int bytes)
    {
        using var stream = File.Open(file, FileMode.Append, FileAccess.Write);
        stream.Write(new byte[bytes]);
    }

    private string Make(string name, int bytes)
    {
        var file = Path.Combine(dir, name);
        File.WriteAllBytes(file, new byte[bytes]);
        return file;
    }

    // ---- the Electron case ---------------------------------------------------

    [Fact]
    public void PartialDownload_AppearsWithSpeedFromGrowth_AndIsGoneWhenStalled()
    {
        var file = Make("setup.exe.crdownload", 6_000_000);
        long t = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var watcher = new FileProgressWatcher(new[] { dir }, () => t);

        var first = watcher.Jobs();
        Assert.Single(first);                                   // a 6 MB partial file is a transfer
        Assert.Equal("Downloads · setup.exe.crdownload", first[0].Title);
        Assert.Equal("6.0 MB", first[0].Detail);

        Grow(file, 1_000_000);
        t += 1000;
        var growing = watcher.Jobs();
        Assert.Equal("7.0 MB · 1.0 MB/s", growing[0].Detail);

        t += 9000;                                              // no change for 9 s: stalled, hidden
        Assert.Empty(watcher.Jobs());

        File.Move(file, Path.Combine(dir, "setup.exe"));        // a finished file is not a transfer any more
        Assert.Empty(watcher.Jobs());
    }

    // ---- the rest of the port -----------------------------------------------

    [Fact]
    public void Job_HasIdSymbolAndToolOfTheTransferFile()
    {
        var file = Make("setup.exe.part", 6_000_000);
        long t = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var watcher = new FileProgressWatcher(new[] { dir }, () => t);

        var job = Assert.Single(watcher.Jobs());
        Assert.Equal("file:" + file, job.Id);
        Assert.Equal("arrow.down.circle.fill", job.Symbol);
        Assert.Equal("Tải file", job.Tool);
        Assert.True(job.Working);
        Assert.Null(job.Url);
        Assert.Null(job.Progress);
    }

    [Fact]
    public void PlainFile_GrowingPastFiveMegabytes_IsACopy()
    {
        var file = Make("video.mp4", 6_000_000);
        long t = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var watcher = new FileProgressWatcher(new[] { dir }, () => t);

        Assert.Empty(watcher.Jobs());                           // a plain file is only seen once it grows

        Grow(file, 1_000_000);
        t += 1000;
        var job = Assert.Single(watcher.Jobs());
        Assert.Equal("Copy · video.mp4", job.Title);
        Assert.Equal("7.0 MB · 1.0 MB/s", job.Detail);
        Assert.Equal("doc.on.doc.fill", job.Symbol);
    }

    [Fact]
    public void PartialExtensions_AreCaseInsensitive()
    {
        Make("SETUP.EXE.CRDOWNLOAD", 6_000_000);
        long t = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var watcher = new FileProgressWatcher(new[] { dir }, () => t);

        var job = Assert.Single(watcher.Jobs());
        Assert.Equal("Downloads · SETUP.EXE.CRDOWNLOAD", job.Title);
    }

    [Fact]
    public void SmallGrowingPartialFile_IsIgnored()
    {
        var file = Make("small.part", 1_000_000);
        long t = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var watcher = new FileProgressWatcher(new[] { dir }, () => t);

        Assert.Empty(watcher.Jobs());
        Grow(file, 1_000_000);
        t += 1000;
        Assert.Empty(watcher.Jobs());
    }

    [Fact]
    public void Jobs_ManyFiles_ReportsOnlyTheGrowingBigOne()
    {
        for (var i = 0; i < 2000; i++)
        {
            Make($"small-{i}.txt", 10);
        }
        var file = Make("big.crdownload", 6_000_000);
        long t = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var watcher = new FileProgressWatcher(new[] { dir }, () => t);

        watcher.Jobs();
        Grow(file, 1_000_000);
        t += 1000;
        var jobs = watcher.Jobs();
        var job = Assert.Single(jobs);
        Assert.Equal("file:" + file, job.Id);
    }

    [Fact]
    public void Jobs_FolderDeleted_IsEmpty()
    {
        var gone = Path.Combine(dir, "not-there");
        long t = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var watcher = new FileProgressWatcher(new[] { gone }, () => t);

        Assert.Empty(watcher.Jobs());
    }

    [Fact]
    public void DefaultFolders_AreTheSixUsualFoldersUnderHome()
    {
        var home = Path.Combine("home", "someone");
        var folders = FileProgressWatcher.DefaultFolders(home);

        Assert.Equal(
            new[]
            {
                Path.Combine(home, "Downloads"),
                Path.Combine(home, "Desktop"),
                Path.Combine(home, "Documents"),
                Path.Combine(home, "Videos"),
                Path.Combine(home, "Pictures"),
                Path.Combine(home, "Music"),
            },
            folders);
    }

    [Theory]
    [InlineData(1_200_000_000d, "1.2 GB")]
    [InlineData(3_400_000d, "3.4 MB")]
    [InlineData(12_000d, "12 KB")]
    [InlineData(512d, "512 B")]
    public void SizeText_FormatsLikeTheElectronPort(double bytes, string expected)
    {
        Assert.Equal(expected, FileProgressWatcher.SizeText(bytes));
    }
}
