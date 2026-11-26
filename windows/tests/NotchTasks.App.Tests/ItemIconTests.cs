using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NotchTasks.Core.Agents;
using NotchTasks.Pill;
using Xunit;

namespace NotchTasks.AppTests;

/// The icon lookup never reads the process table on the calling (UI) thread.
public sealed class ItemIconTests
{
    /// Records the thread each process-table read happens on; it has no processes.
    private sealed class ThreadRecordingProcesses : IProcessReader
    {
        public int? SnapshotThread { get; private set; }

        public ProcSnapshot Snapshot()
        {
            SnapshotThread = Thread.CurrentThread.ManagedThreadId;
            return new ProcSnapshot(Array.Empty<ProcInfo>());
        }

        public string? CommandLine(ProcInfo p) => null;

        public string? ImagePath(ProcInfo p) => null;
        public string? Cwd(ProcInfo p) => null;
    }

    [Fact]
    public void PathOf_LooksTheProcessUpOffTheCallingThread()
    {
        Sta.Run(() =>
        {
            var processes = new ThreadRecordingProcesses();
            // a pid no other test uses, so the cache starts empty for it
            var state = new ItemState(new WatchItem("app-off-ui", "App", WatchKind.App),
                new[] { new Job("987654321", "App", "3%", true) });

            var path = ItemIcon.PathOf(state, processes);

            Assert.Null(path);  // not known yet: the lookup runs in the background
            Fixture.PumpUntil(() => processes.SnapshotThread is not null);
            Assert.NotNull(processes.SnapshotThread);
            Assert.NotEqual(Thread.CurrentThread.ManagedThreadId, processes.SnapshotThread);
        });
    }

    /// A process table with one process whose path and start time the test sets.
    private sealed class OneProcess : IProcessReader
    {
        public ProcInfo? Info { get; set; }

        public string? Path { get; set; }

        public ProcSnapshot Snapshot() => new(Info is { } info ? new[] { info } : Array.Empty<ProcInfo>());

        public string? CommandLine(ProcInfo p) => null;

        public string? ImagePath(ProcInfo p) => Path;
        public string? Cwd(ProcInfo p) => null;
    }

    [Fact]
    public void PathOf_AFoundPathExpiresAfterTenMinutes_AndAReusedPidTakesTheNewProgram()
    {
        Sta.Run(() =>
        {
            var now = new DateTimeOffset(2026, 11, 20, 10, 0, 0, TimeSpan.Zero);
            var saved = ItemIcon.Clock;
            try
            {
                ItemIcon.Clock = () => now;
                // a pid no other test uses
                var processes = new OneProcess { Info = new ProcInfo(555001, 1, "a.exe", 1, 0), Path = @"C:\first\a.exe" };
                var state = new ItemState(new WatchItem("pid-expiry", "App", WatchKind.App),
                    new[] { new Job("555001", "App", "3%", true) });

                Assert.Null(ItemIcon.PathOf(state, processes));
                Fixture.PumpUntil(() => ItemIcon.PathOf(state, processes) is not null);
                Assert.Equal(@"C:\first\a.exe", ItemIcon.PathOf(state, processes));

                // eleven minutes later the expired path is still shown while its lookup runs: no flicker
                now = now.AddMinutes(11);
                processes.Info = new ProcInfo(555001, 1, "b.exe", 2, 0);
                processes.Path = @"C:\second\b.exe";
                Assert.Equal(@"C:\first\a.exe", ItemIcon.PathOf(state, processes));

                // and then the same pid is another program: the old path is not kept, and the new one is found
                Fixture.PumpUntil(() => ItemIcon.PathOf(state, processes) == @"C:\second\b.exe");
                Assert.Equal(@"C:\second\b.exe", ItemIcon.PathOf(state, processes));
            }
            finally
            {
                ItemIcon.Clock = saved;
            }
        });
    }

    [Fact]
    public void Element_AnItemWithAnIcon_DrawsItsBadge()
    {
        Sta.Run(() =>
        {
            // the item's own icon (set on the Watching pane) replaces the sparkles glyph on the pill
            var item = new WatchItem("icon-badge", "Mine", WatchKind.Process,
                Icon: "{\"symbol\":\"seg:E735\",\"shape\":\"rounded\"}");
            var element = ItemIcon.Element(new ItemState(item, Array.Empty<Job>()), 18, Colors.Gray, null);

            // a badge is a Grid: its fill, then its glyph
            var badge = Assert.IsType<Grid>(element);
            Assert.IsType<Border>(badge.Children[0]);
            Assert.IsType<TextBlock>(badge.Children[1]);
        });
    }

    [Fact]
    public void Element_AnInvalidIcon_FallsBackToTheSparkles()
    {
        Sta.Run(() =>
        {
            // text that is not a JSON object is not a badge: the item shows what it showed before
            foreach (var text in new[] { "not json", "[1,2]" })
            {
                var item = new WatchItem("icon-invalid", "Mine", WatchKind.Process, Icon: text);
                var element = ItemIcon.Element(new ItemState(item, Array.Empty<Job>()), 18, Colors.Gray, null);

                var glyph = Assert.IsType<TextBlock>(element);
                Assert.Equal(ModuleGlyphs.Get("sparkles").Glyph, glyph.Text);
            }
        });
    }

    [Fact]
    public void PathOf_AGonePid_LeavesNoCacheEntry()
    {
        Sta.Run(() =>
        {
            var now = new DateTimeOffset(2026, 11, 20, 11, 0, 0, TimeSpan.Zero);
            var saved = ItemIcon.Clock;
            try
            {
                ItemIcon.Clock = () => now;
                // a pid no other test uses
                var processes = new OneProcess { Info = new ProcInfo(555002, 1, "c.exe", 3, 0), Path = @"C:\third\c.exe" };
                var state = new ItemState(new WatchItem("pid-gone", "App", WatchKind.App),
                    new[] { new Job("555002", "App", "3%", true) });
                Fixture.PumpUntil(() => ItemIcon.PathOf(state, processes) is not null);
                Assert.True(ItemIcon.CachedEntryCount(555002) > 0);

                // the program exits and the cached path expires: the next snapshot shows the pid gone and drops its entries
                processes.Info = null;
                now = now.AddMinutes(11);
                Fixture.PumpUntil(() =>
                {
                    ItemIcon.PathOf(state, processes);
                    return ItemIcon.CachedEntryCount(555002) == 0;
                });
                Assert.Equal(0, ItemIcon.CachedEntryCount(555002));
            }
            finally
            {
                ItemIcon.Clock = saved;
            }
        });
    }
}
