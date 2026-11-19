using System.Globalization;
using System.Runtime.CompilerServices;
using NotchTasks.Core.Notch;

namespace NotchTasks.Core.Modules;

/// The cumulative CPU times of the whole computer, in 100 ns ticks, as GetSystemTimes reports them. On Windows the kernel
/// time includes the idle time.
public readonly record struct CpuTimes(ulong Idle, ulong Kernel, ulong User);

/// The health module (Mac: checkDisk in Model.swift, and the CPU and RAM meters of NotchView.swift). The disk warns once when
/// the free space is under 10 GB, and again only after it has been above 15 GB. The load is the CPU between two samples and the
/// memory in use. The warned flag lives here, so a source that stops and starts does not warn twice. No timers and no OS
/// calls: the source reads the disk, the CPU times and the memory, and feeds them in.
public sealed class HealthModule
{
    private const double WarnBelowGb = 10;
    private const double RearmAboveGb = 15;

    private readonly IAnnouncer _announce;
    private bool _diskWarned;
    private CpuTimes? _last;

    /// The toasts go to the announcer.
    public HealthModule(IAnnouncer announce)
    {
        _announce = announce;
    }

    /// The last load reading: CPU and memory, each a fraction 0..1. Null until two samples have been taken, and after ClearLoad.
    public SystemLoad? Load { get; private set; }

    /// Judges the free space of the disk, in bytes. Below 10 GB the warning goes out once; it is armed again only after the
    /// free space goes above 15 GB.
    public void Disk(long freeBytes)
    {
        var gb = freeBytes / 1e9;
        if (gb < WarnBelowGb)
        {
            if (!_diskWarned)
            {
                _diskWarned = true;
                _announce.Announce(new Toast(
                    Glyph: "drive",
                    Tint: "#FF9F0A",
                    Title: L10n.L("Disk almost full"),
                    Subtitle: L10n.L("%.1f GB free").Replace("%.1f", gb.ToString("F1", CultureInfo.CurrentCulture), StringComparison.Ordinal),
                    Event: "warning",
                    Url: "ms-settings:storagesense",
                    Action: L10n.L("Free up space"),
                    Module: "health"),
                    notify: true);
            }
        }
        else if (gb > RearmAboveGb)
        {
            _diskWarned = false;
        }
    }

    /// The rounded percent of a 0..1 fraction, as the meters show it (half up).
    public static int LoadPercent(double fraction) =>
        (int)Math.Round(Math.Clamp(fraction, 0, 1) * 100, MidpointRounding.AwayFromZero);

    /// Takes a CPU reading with the memory in use (dwMemoryLoad / 100). The first sample after a reset (or the first ever) only
    /// stores the times, so Load keeps the value it shows: a reopened pill shows its last meters at once. A later sample gives
    /// the CPU as 1 - idle / total over the time since the previous sample, where the total is the kernel plus the user time
    /// delta. Nothing elapsed (total 0) keeps the previous Load.
    public void Sample(CpuTimes now, double memoryLoad)
    {
        var last = _last;
        _last = now;
        if (last is not { } prev || now.Idle < prev.Idle || now.Kernel < prev.Kernel || now.User < prev.User)
        {
            return;
        }

        var total = (now.Kernel - prev.Kernel) + (now.User - prev.User);
        if (total == 0)
        {
            return;
        }
        var idle = now.Idle - prev.Idle;
        var cpu = Math.Clamp(1.0 - (double)idle / total, 0, 1);
        Load = new SystemLoad(cpu, Math.Clamp(memoryLoad, 0, 1));
    }

    /// Forgets the CPU baseline only, when the pill closes: Load stays, so the meters show at once on reopen. The next sample
    /// is a baseline, and the one after it gives a new CPU reading.
    public void ResetLoad() => _last = null;

    /// Forgets the baseline and the load: the load is off (the module stopped, or SystemLoad is off). Returns true when Load was set.
    public bool ClearLoad()
    {
        var changed = Load is not null;
        _last = null;
        Load = null;
        return changed;
    }
}

/// The Windows readings the health source feeds the module with. Each returns null when the call fails.
public static class HealthReadings
{
    /// The CPU times of the whole computer now.
    public static CpuTimes? ReadCpuTimes()
    {
        if (!NativeMethods.GetSystemTimes(out var idle, out var kernel, out var user))
        {
            return null;
        }
        return new CpuTimes(idle, kernel, user);
    }

    /// The share of memory in use, 0..1 (dwMemoryLoad / 100), or null when the call fails.
    public static double? ReadMemoryLoad()
    {
        var status = new MemoryStatusEx { Length = (uint)Unsafe.SizeOf<MemoryStatusEx>() };
        if (!NativeMethods.GlobalMemoryStatusEx(ref status))
        {
            return null;
        }
        return status.MemoryLoad / 100.0;
    }

    /// The free bytes available to the user on the drive of `root` (a path root such as "C:\"), or null when the call fails.
    public static long? ReadFreeBytes(string root)
    {
        if (!NativeMethods.GetDiskFreeSpaceEx(root, out var free, out _, out _))
        {
            return null;
        }
        return (long)free;
    }
}
