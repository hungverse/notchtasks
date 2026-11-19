using System.Threading;
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
}
