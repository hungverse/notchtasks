using System.IO;
using System.Windows.Threading;
using NotchTasks.Core.Modules;
using NotchTasks.Core.Notch;
using NotchTasks.Core.Settings;

namespace NotchTasks.Services.Modules;

/// The readings the health source takes. The default reads this PC; tests pass fakes.
public sealed record HealthReaders(Func<CpuTimes?> Cpu, Func<double?> Memory, Func<string, long?> FreeBytes)
{
    /// The readings of this PC.
    public static HealthReaders Default { get; } =
        new(HealthReadings.ReadCpuTimes, HealthReadings.ReadMemoryLoad, HealthReadings.ReadFreeBytes);
}

/// The health module's feed. Start checks the disk of the profile drive, then keeps checking it every 10 min while started.
/// While the pill is open (and SystemLoad is on and the source is started) the load is sampled every second, starting with a
/// baseline sample at once. A tick publishes only when the rounded CPU or RAM percent changed, through publishLoad, which does
/// not rebuild the notch. Closing the pill forgets only the CPU baseline: the last meters stay for the next open. Stop, or
/// SystemLoad off, clears the load. Nothing samples while the pill is closed.
public sealed class HealthSource : IModuleSource
{
    private static readonly TimeSpan DiskEvery = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan LoadEvery = TimeSpan.FromSeconds(1);

    private readonly HealthModule _module;
    private readonly Action _publishLoad;
    private readonly SettingsStore _settings;
    private readonly HealthReaders _read;
    private readonly string _drive = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) ?? string.Empty;
    private readonly DispatcherTimer _diskTimer = new() { Interval = DiskEvery };
    private readonly DispatcherTimer _loadTimer = new() { Interval = LoadEvery };
    private bool _started;
    private bool _pillOpen;
    private bool _readFailureLogged;

    /// The source of `module`. publishLoad tells the app that the load changed (on the UI thread). The SystemLoad setting is read
    /// from `settings`. `readers` defaults to this PC's readings.
    public HealthSource(HealthModule module, Action publishLoad, SettingsStore settings, HealthReaders? readers = null)
    {
        _module = module;
        _publishLoad = publishLoad;
        _settings = settings;
        _read = readers ?? HealthReaders.Default;
        _diskTimer.Tick += (_, _) => CheckDisk();
        _loadTimer.Tick += (_, _) => SampleLoad();
    }

    /// Checks the disk now and every 10 min after, and starts the load if the pill is open.
    public void Start()
    {
        _started = true;
        CheckDisk();
        _diskTimer.Start();
        SyncLoad();
    }

    /// Stops both timers and clears the load. The warned flag stays in the module, so a restart does not warn twice.
    public void Stop()
    {
        _started = false;
        _diskTimer.Stop();
        _loadTimer.Stop();
        _module.ClearLoad();
    }

    /// The pill opened or closed: the load is sampled only while it is open.
    public void PillOpened(bool open)
    {
        _pillOpen = open;
        SyncLoad();
    }

    /// The SystemLoad setting changed: the load runs or stops with it.
    public void SystemLoadChanged() => SyncLoad();

    /// Runs the load timer while the source runs, the pill is open and SystemLoad is on. Otherwise stops it and forgets the CPU
    /// baseline; with SystemLoad off the load itself is cleared and the change is published.
    private void SyncLoad()
    {
        if (_started && _pillOpen && _settings.SystemLoad)
        {
            if (!_loadTimer.IsEnabled)
            {
                _loadTimer.Start();
                SampleLoad();  // the baseline now, so the next tick has two samples
            }
            return;
        }
        _loadTimer.Stop();
        _module.ResetLoad();
        if (!_settings.SystemLoad && _module.ClearLoad())
        {
            _publishLoad();
        }
    }

    /// Reads the CPU times and the memory, feeds the module, and publishes only when the rounded percents changed. A failed read
    /// is logged once and skipped.
    private void SampleLoad()
    {
        var before = _module.Load;
        var times = _read.Cpu();
        var memory = _read.Memory();
        if (times is null || memory is null)
        {
            LogFailure("the CPU and memory readings failed");
            return;
        }
        _module.Sample(times.Value, memory.Value);
        if (!SameShown(before, _module.Load))
        {
            _publishLoad();
        }
    }

    /// Two loads show the same meters when both are null, or their rounded percents match.
    private static bool SameShown(SystemLoad? a, SystemLoad? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }
        return HealthModule.LoadPercent(a.Cpu) == HealthModule.LoadPercent(b.Cpu)
            && HealthModule.LoadPercent(a.Memory) == HealthModule.LoadPercent(b.Memory);
    }

    /// Reads the free space of the profile drive and judges it. A failed read is logged once and skipped.
    private void CheckDisk()
    {
        if (!_started)
        {
            return;
        }
        var free = _read.FreeBytes(_drive);
        if (free is null)
        {
            LogFailure("the free space of " + _drive + " could not be read");
            return;
        }
        _module.Disk(free.Value);
    }

    private void LogFailure(string what)
    {
        if (!_readFailureLogged)
        {
            _readFailureLogged = true;
            ErrorLog.Write(new IOException("NotchTasks: the health module could not read: " + what));
        }
    }
}
