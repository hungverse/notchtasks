using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using NotchTasks.Core.Motion;
using NotchTasks.Demo;
using NotchTasks.Pill;
using Xunit;

namespace NotchTasks.AppTests.Demo;

/// The frame capture of the demo run (NT_FRAMES): one PNG per frame, and the pixel size.
public sealed class DemoCaptureTests
{
    [Fact]
    public void Capture_WritesOnePngPerFrame_AndTheSize_ForAShortLoop()
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

                DemoCapture.Run(content, folder, frames: 3, fps: 12, apply: t => times.Add(t), scale: 1.0);

                Assert.Equal(
                    new[] { "frame-0000.png", "frame-0001.png", "frame-0002.png" },
                    Directory.GetFiles(folder, "*.png").Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal));
                Assert.Equal(new[] { TimeSpan.Zero, TimeSpan.FromSeconds(1.0 / 12), TimeSpan.FromSeconds(2.0 / 12) }, times);

                var width = (int)Math.Round(PillGeometry.WindowWidth);
                var height = (int)Math.Round(PillGeometry.WindowHeight);
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
