using System.IO;
using System.Threading;
using System.Windows.Threading;
using NotchTasks.Pill;

namespace NotchTasks.Services;

/// What the app did in the last minute, for the P3d measurements: the publishes, the media and device reads, the device changes, and the
/// chip panels rebuilt. Written to the frame log (NOTCHTASKS_FRAMELOG, FrameLog.Path) once a minute, as one line, and the counts reset
/// then. With no log path the counts are not kept and no timer runs, so nothing costs anything.
public static class UsageCounters
{
    private static long _publishes;
    private static long _mediaReads;
    private static long _deviceChanged;
    private static long _deviceReads;
    private static long _chipRebuilds;
    private static DispatcherTimer? _timer;

    /// The period of the line.
    public static readonly TimeSpan Period = TimeSpan.FromMinutes(1);

    /// Whether the once-a-minute timer runs.
    public static bool Running => _timer is not null;

    public static void Publish() => Count(ref _publishes);

    public static void MediaRead() => Count(ref _mediaReads);

    public static void DeviceChanged() => Count(ref _deviceChanged);

    public static void DeviceRead() => Count(ref _deviceReads);

    public static void ChipRebuild() => Count(ref _chipRebuilds);

    /// Starts the once-a-minute line when the frame log is on. Off, it does nothing; a second Start does nothing.
    public static void Start()
    {
        if (FrameLog.Path is null || _timer is not null)
        {
            return;
        }
        _timer = new DispatcherTimer { Interval = Period };
        _timer.Tick += (_, _) => Flush(DateTimeOffset.Now);
        _timer.Start();
    }

    /// Stops the timer. The counts kept so far are left for the next Flush.
    public static void Stop()
    {
        _timer?.Stop();
        _timer = null;
    }

    /// The line for a minute's counts: the time, then the five counts, in the frame log's form.
    public static string Line(DateTimeOffset now, UsageTotals totals) => FormattableString.Invariant(
        $"{now:O} counts publishes={totals.Publishes} mediaReads={totals.MediaReads} deviceChanged={totals.DeviceChanged} deviceReads={totals.DeviceReads} chipRebuilds={totals.ChipRebuilds}");

    /// Takes the counts (resetting them) and appends their line to the frame log. Nothing is written with no log path. Write failures
    /// are ignored, as the frame log's are.
    public static void Flush(DateTimeOffset now)
    {
        var path = FrameLog.Path;
        var totals = new UsageTotals(
            Interlocked.Exchange(ref _publishes, 0),
            Interlocked.Exchange(ref _mediaReads, 0),
            Interlocked.Exchange(ref _deviceChanged, 0),
            Interlocked.Exchange(ref _deviceReads, 0),
            Interlocked.Exchange(ref _chipRebuilds, 0));
        if (path is null)
        {
            return;
        }
        try
        {
            File.AppendAllText(path, Line(now, totals) + Environment.NewLine);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            System.Diagnostics.Debug.WriteLine("NotchTasks: could not write the usage counts: " + e);
        }
    }

    /// Adds one to a count, when the frame log is on.
    private static void Count(ref long counter)
    {
        if (FrameLog.Path is not null)
        {
            Interlocked.Increment(ref counter);
        }
    }
}

/// The counts of one minute.
public readonly record struct UsageTotals(long Publishes, long MediaReads, long DeviceChanged, long DeviceReads, long ChipRebuilds);
