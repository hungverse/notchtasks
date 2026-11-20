using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using NotchTasks.Core.Motion;

namespace NotchTasks.Pill;

/// Frame statistics for the pill's animations. When NOTCHTASKS_FRAMELOG names a file, each animation appends one line to
/// it. Off (no path) costs nothing: no Rendering handler is attached.
public static class FrameLog
{
    private const double FallbackPeriodMs = 16.7;
    private const int DevModeSize = 220;  // sizeof(DEVMODEW)
    private const int DmSizeOffset = 68;  // dmSize (WORD)
    private const int DmDisplayFrequencyOffset = 184;  // dmDisplayFrequency (DWORD), in Hz

    private static readonly List<double> Intervals = new();
    private static string? _label;
    private static TimeSpan? _lastTime;

    /// The file the lines go to. Read from NOTCHTASKS_FRAMELOG at startup; null means off.
    public static string? Path { get; set; } = NormalizePath(Environment.GetEnvironmentVariable("NOTCHTASKS_FRAMELOG"));

    /// The frame period to use instead of the display's (a test seam). Null reads the display.
    public static double? PeriodMsOverride { get; set; }

    /// True between a Begin and the End that ends it.
    public static bool IsRecording { get; private set; }

    /// Starts recording the frame intervals of one animation. A Begin while recording first ends the previous recording.
    public static void Begin(string label)
    {
        if (Path is null)
        {
            return;
        }
        if (IsRecording)
        {
            End();
        }
        _label = label;
        Intervals.Clear();
        _lastTime = null;
        IsRecording = true;
        CompositionTarget.Rendering += OnRendering;
    }

    /// Stops recording and appends the line of statistics to Path. Write failures are ignored.
    public static void End()
    {
        if (!IsRecording)
        {
            return;
        }
        CompositionTarget.Rendering -= OnRendering;
        IsRecording = false;

        var path = Path;
        var period = PeriodMsOverride ?? DisplayPeriodMs();
        var summary = FrameStats.Summarize(Intervals, period);
        Intervals.Clear();
        if (path is null)
        {
            return;
        }

        var line = FormattableString.Invariant(
            $"{DateTimeOffset.Now:O} {_label} frames={summary.Frames} p50={summary.P50:F1} p95={summary.P95:F1} max={summary.Max:F1} long={summary.Long} period={period:F1}");
        try
        {
            File.AppendAllText(path, line + Environment.NewLine);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Debug.WriteLine("NotchTasks: could not write the frame log: " + e);
        }
    }

    /// The Rendering deltas: each interval from the previous frame's time, skipping a repeat of the same time.
    private static void OnRendering(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (_lastTime is { } last)
        {
            if (now == last)
            {
                return;
            }
            Intervals.Add((now - last).TotalMilliseconds);
        }
        _lastTime = now;
    }

    /// 1000 / the primary display's refresh rate in Hz, or 16.7 when the display cannot be read.
    private static double DisplayPeriodMs()
    {
        var devMode = Marshal.AllocHGlobal(DevModeSize);
        try
        {
            Marshal.Copy(new byte[DevModeSize], 0, devMode, DevModeSize);
            Marshal.WriteInt16(devMode, DmSizeOffset, (short)DevModeSize);
            if (Win32.EnumDisplaySettings(0, Win32.EnumCurrentSettings, devMode))
            {
                var hz = Marshal.ReadInt32(devMode, DmDisplayFrequencyOffset);
                if (hz > 1)
                {
                    return 1000.0 / hz;
                }
            }
            return FallbackPeriodMs;
        }
        finally
        {
            Marshal.FreeHGlobal(devMode);
        }
    }

    private static string? NormalizePath(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
