using System.IO;
using System.Windows.Threading;
using NotchTasks.Core.Modules;
using NotchTasks.Core.Settings;

namespace NotchTasks.Services.Modules;

/// The health module's feed. Start checks the disk of the profile drive, then keeps checking it every 10 min while started. The
/// load is sampled every 2 s, but only while the pill is open, SystemLoad is on and the source is started: PillOpened and
/// SystemLoadChanged bring the load timer in step. When it stops, the load is forgotten and the pill is published, so a stale
/// ring never shows. Stop stops both timers. Nothing samples while the pill is closed.
public sealed class HealthSource : IModuleSource
{
    private static readonly TimeSpan DiskEvery = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan LoadEvery = TimeSpan.FromSeconds(2);

    private readonly HealthModule _module;
    private readonly Action _publish;
    private readonly SettingsStore _settings;
    private readonly string _drive = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) ?? string.Empty;
    private readonly DispatcherTimer _diskTimer = new() { Interval = DiskEvery };
    private readonly DispatcherTimer _loadTimer = new() { Interval = LoadEvery };
    private bool _started;
    private bool _pillOpen;
    private bool _readFailureLogged;

    /// The source of `module`, which publishes the app state through `publish` (on the UI thread). The SystemLoad setting is
    /// read from `settings` each time the pill opens or the setting changes.
    public HealthSource(HealthModule module, Action publish, SettingsStore settings)
    {
        _module = module;
        _publish = publish;
        _settings = settings;
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

    /// Stops both timers and forgets the load. The warned flag stays in the module, so a restart does not warn twice.
    public void Stop()
    {
        _started = false;
        _diskTimer.Stop();
        _loadTimer.Stop();
        _module.ResetLoad();
    }

    /// The pill opened or closed: the load is sampled only while it is open.
    public void PillOpened(bool open)
    {
        _pillOpen = open;
        SyncLoad();
    }

    /// The SystemLoad setting changed: the load runs or stops with it.
    public void SystemLoadChanged() => SyncLoad();

    /// Starts the load timer while the source runs, the pill is open and SystemLoad is on. Otherwise stops it, forgets the
    /// load and publishes when a load was shown.
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
        if (_module.ResetLoad())
        {
            _publish();
        }
    }

    /// Reads the CPU times and the memory, feeds the module and publishes. A failed read is logged once and skipped.
    private void SampleLoad()
    {
        var times = HealthReadings.ReadCpuTimes();
        var memory = HealthReadings.ReadMemoryLoad();
        if (times is null || memory is null)
        {
            LogFailure("the CPU and memory readings failed");
            return;
        }
        _module.Sample(times.Value, memory.Value);
        _publish();
    }

    /// Reads the free space of the profile drive and judges it. A failed read is logged once and skipped.
    private void CheckDisk()
    {
        if (!_started)
        {
            return;
        }
        var free = HealthReadings.ReadFreeBytes(_drive);
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
