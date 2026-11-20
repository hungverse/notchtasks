using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using NotchTasks.Pill;
using NotchTasks.Services;
using Xunit;

namespace NotchTasks.AppTests;

/// The once-a-minute usage line in the frame log: its form, that nothing is kept or run while the log is off, and that a flush writes the
/// counts and starts them again.
public sealed class UsageCountersTests
{
    [Fact]
    public void Line_HasTheFrameLogForm()
    {
        var line = UsageCounters.Line(new DateTimeOffset(2026, 11, 20, 9, 30, 0, TimeSpan.Zero),
            new UsageTotals(4, 2, 1, 3, 7));

        Assert.Matches(
            @"^\d{4}-\d{2}-\d{2}T\S+ counts publishes=\d+ mediaReads=\d+ deviceChanged=\d+ deviceReads=\d+ chipRebuilds=\d+$",
            line);
    }

    [Fact]
    public void Off_NoTimerNoCounts()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var file = Path.Combine(root.Folder, "frames.log");
            try
            {
                FrameLog.Path = null;
                UsageCounters.Start();
                Assert.False(UsageCounters.Running);

                UsageCounters.Publish();
                FrameLog.Path = file;
                UsageCounters.Flush(DateTimeOffset.Now);

                var line = Assert.Single(File.ReadAllLines(file));
                Assert.Matches(@"publishes=0 ", line);  // the publish made while off was not kept
            }
            finally
            {
                UsageCounters.Stop();
                FrameLog.Path = null;
            }
        });
    }

    [Fact]
    public void On_TimerRuns_AndFlushWritesThenResets()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var file = Path.Combine(root.Folder, "frames.log");
            try
            {
                FrameLog.Path = file;
                UsageCounters.Start();
                Assert.True(UsageCounters.Running);

                UsageCounters.Publish();
                UsageCounters.Publish();
                UsageCounters.ChipRebuild();
                UsageCounters.Flush(new DateTimeOffset(2026, 11, 20, 9, 31, 0, TimeSpan.Zero));
                UsageCounters.Flush(new DateTimeOffset(2026, 11, 20, 9, 32, 0, TimeSpan.Zero));

                var lines = File.ReadAllLines(file);
                Assert.Equal(2, lines.Length);
                Assert.Matches(new Regex(@"publishes=2 mediaReads=0 deviceChanged=0 deviceReads=0 chipRebuilds=1$"), lines[0]);
                Assert.Matches(new Regex(@"publishes=0 mediaReads=0 deviceChanged=0 deviceReads=0 chipRebuilds=0$"), lines[1]);
            }
            finally
            {
                UsageCounters.Stop();
                FrameLog.Path = null;
            }
        });
    }
}
