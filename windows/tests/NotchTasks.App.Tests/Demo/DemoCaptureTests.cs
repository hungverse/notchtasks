using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using NotchTasks.Core.Demo;
using NotchTasks.Core.Motion;
using NotchTasks.Demo;
using NotchTasks.Pill;
using Xunit;

namespace NotchTasks.AppTests.Demo;

/// The frame capture of the demo run (NT_FRAMES): one PNG per frame, and the pixel size.
public sealed class DemoCaptureTests
{
    [Fact]
    public void Capture_WritesEveryFrameOfTheLoop_AndTheSizeDoubled()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = DemoSession.NewState();
            var pill = new PillWindow(state);
            try
            {
                // Not shown: the window's content is measured and arranged at the window's size, as the pill tests do.
                var content = Assert.IsAssignableFrom<FrameworkElement>(pill.Content);
                content.Measure(new Size(PillGeometry.WindowWidth, PillGeometry.WindowHeight));
                content.Arrange(new Rect(0, 0, PillGeometry.WindowWidth, PillGeometry.WindowHeight));
                var folder = Path.Combine(root.Folder, "frames");
                var times = new List<TimeSpan>();

                // The README's copy: 2x the monitor's scale. Unpaced here, so the whole loop takes a few seconds.
                DemoCapture.Run(content, folder, DemoTimeline.FrameCount, DemoTimeline.Fps,
                    apply: t => times.Add(t), scale: 2.0, paced: false);

                var expected = Enumerable.Range(0, DemoTimeline.FrameCount).Select(i => $"frame-{i:0000}.png").ToArray();
                Assert.Equal(240, expected.Length);
                Assert.Equal(expected, Directory.GetFiles(folder, "*.png").Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal));
                Assert.Equal(TimeSpan.Zero, times[0]);
                Assert.Equal(TimeSpan.FromSeconds(239.0 / 15), times[^1]);

                var width = (int)Math.Round(PillGeometry.WindowWidth * 2);
                var height = (int)Math.Round(PillGeometry.WindowHeight * 2);
                Assert.Equal($"{width}x{height}", File.ReadAllText(Path.Combine(folder, "size.txt")).Trim());

                var frame = BitmapFrame.Create(new Uri(Path.Combine(folder, "frame-0000.png")), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                Assert.Equal(width, frame.PixelWidth);
                Assert.Equal(height, frame.PixelHeight);
            }
            finally
            {
                pill.Close();
            }
        });
    }
}
