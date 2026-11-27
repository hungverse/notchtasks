using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace NotchTasks.Demo;

/// The frame capture of the demo run (`NT_FRAMES=<folder>`): the pill's root, drawn at the monitor's DPI and transparent outside the
/// pill, is written to frame-0000.png, frame-0001.png and so on, one per frame, and size.txt gets the pixel size ("WIDTHxHEIGHT").
/// Frame i is the timeline at i / fps seconds. The animations of the pill are not stepped: they run on the render clock, so each frame
/// waits until the clock has reached its time (i / fps seconds from the start), is applied there, and is captured after the render
/// that shows it. A frame that is late is captured at once: no frame is skipped or doubled. The thread pumps its dispatcher while it
/// waits, so the pill's animations run on.
public static class DemoCapture
{
    private static readonly TimeSpan RenderWait = TimeSpan.FromMilliseconds(100);  // a pass that draws nothing new comes back after this
    private static readonly TimeSpan MinPump = TimeSpan.FromMilliseconds(1);

    /// Captures `frames` frames of `root` into `folder`. apply is called with each frame's time before it is drawn. scale is the
    /// capture's pixels per DIP (the monitor's DPI over 96, times 2 for the README's sharp copy). The root's render size is the capture's
    /// size, so the window's whole client area is in the frames. paced false draws each frame as soon as it is applied (a test); the
    /// default waits for each frame's time. Returns the largest lateness of a frame against its time (zero when unpaced).
    public static TimeSpan Run(FrameworkElement root, string folder, int frames, int fps, Action<TimeSpan> apply, double scale, bool paced = true)
    {
        Directory.CreateDirectory(folder);
        var width = (int)Math.Round(root.RenderSize.Width * scale);
        var height = (int)Math.Round(root.RenderSize.Height * scale);
        var clock = Stopwatch.StartNew();
        var worst = TimeSpan.Zero;
        for (var i = 0; i < frames; i++)
        {
            var at = TimeSpan.FromSeconds(i / (double)fps);
            if (paced)
            {
                PumpUntil(clock, at);
                var late = clock.Elapsed - at;
                if (late > worst)
                {
                    worst = late;
                }
            }
            apply(at);
            root.UpdateLayout();
            if (paced)
            {
                WaitForRender();
            }
            var bitmap = Render(root, width, height, scale);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(folder, $"frame-{i:0000}.png"));
            encoder.Save(file);
        }
        File.WriteAllText(Path.Combine(folder, "size.txt"), $"{width}x{height}{Environment.NewLine}");
        return worst;
    }

    /// The root drawn into a transparent bitmap of width x height pixels.
    private static RenderTargetBitmap Render(FrameworkElement root, int width, int height, double scale)
    {
        var dpi = 96 * scale;
        var target = new RenderTargetBitmap(width, height, dpi, dpi, PixelFormats.Pbgra32);
        // The bitmap's DPI (96 x scale) maps the root's DIP area onto its pixels, so the root is drawn in DIP, once, at its own size: the
        // vector content comes out at the bitmap's resolution. A transform here would scale it a second time.
        var size = root.RenderSize;
        var brush = new VisualBrush(root)
        {
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, size.Width, size.Height),
            ViewportUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, size.Width, size.Height),
            Stretch = Stretch.Fill,
        };
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(brush, null, new Rect(0, 0, size.Width, size.Height));
        }
        target.Render(visual);
        return target;
    }

    /// Pumps the dispatcher until the clock reaches a time.
    private static void PumpUntil(Stopwatch clock, TimeSpan at)
    {
        while (clock.Elapsed < at)
        {
            var remaining = at - clock.Elapsed;
            Pump(remaining < MinPump ? MinPump : remaining);
        }
    }

    /// Waits for the next render pass, or for RenderWait when nothing is drawn.
    private static void WaitForRender()
    {
        var frame = new DispatcherFrame();
        void OnRendering(object? sender, EventArgs e)
        {
            CompositionTarget.Rendering -= OnRendering;
            frame.Continue = false;
        }
        CompositionTarget.Rendering += OnRendering;
        var timer = new DispatcherTimer { Interval = RenderWait };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            CompositionTarget.Rendering -= OnRendering;
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    /// Runs the dispatcher for `for` (or until something else ends the frame).
    private static void Pump(TimeSpan @for)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = @for };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}
