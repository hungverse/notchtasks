using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using NotchTasks.Pill;
using Xunit;

namespace NotchTasks.AppTests;

public sealed class FrameLogTests
{
    [Fact]
    public void FrameLog_Off_NoHandlerNoFile()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var file = Path.Combine(root.Folder, "frames.log");
            try
            {
                FrameLog.Path = null;  // off: Begin attaches nothing
                FrameLog.PeriodMsOverride = 16.7;

                FrameLog.Begin("Open");
                PumpFor(TimeSpan.FromMilliseconds(100));
                FrameLog.End();

                Assert.False(FrameLog.IsRecording);
                Assert.False(File.Exists(file));
                Assert.Empty(Directory.GetFiles(root.Folder));
            }
            finally
            {
                FrameLog.Path = null;
                FrameLog.PeriodMsOverride = null;
            }
        });
    }

    [Fact]
    public void FrameLog_BeginEnd_WritesOneLine()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var file = Path.Combine(root.Folder, "frames.log");
            try
            {
                FrameLog.Path = file;
                FrameLog.PeriodMsOverride = 16.7;

                FrameLog.Begin("Open");
                Assert.True(FrameLog.IsRecording);
                PumpFor(TimeSpan.FromMilliseconds(200));
                FrameLog.End();

                Assert.False(FrameLog.IsRecording);
                var lines = File.ReadAllLines(file);
                var line = Assert.Single(lines);
                Assert.Matches(
                    @"^\d{4}-\d{2}-\d{2}T\S+ Open frames=\d+ p50=\d+\.\d p95=\d+\.\d max=\d+\.\d long=\d+ period=16\.7$",
                    line);
            }
            finally
            {
                FrameLog.Path = null;
                FrameLog.PeriodMsOverride = null;
            }
        });
    }

    /// Runs the dispatcher for a span, so CompositionTarget.Rendering can fire.
    private static void PumpFor(TimeSpan span)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = span };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}
