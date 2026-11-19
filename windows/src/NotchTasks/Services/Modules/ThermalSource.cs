using System.Windows.Threading;
using NotchTasks.Core.Modules;
using NotchTasks.Core.Settings;

namespace NotchTasks.Services.Modules;

/// The header's temperature feed. It asks the helper every 2 s, and only while the pill is open, SystemLoad is on, the sensorHelper
/// setting is on, the helper is On and the health module runs (the source is started with it). Each answer goes through
/// Thermals.Hottest; a change of the shown degrees or the zone is published, and nothing else is. Stopping, closing the pill or
/// turning a gate off publishes no reading, so the header shows nothing.
public sealed class ThermalSource : IModuleSource, IDisposable
{
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(2);

    private readonly SettingsStore _settings;
    private readonly SensorHelper _helper;
    private readonly Action<ThermalReading?> _publish;
    private readonly DispatcherTimer _timer = new() { Interval = Every };
    private bool _started;
    private bool _pillOpen;
    private bool _asking;
    private ThermalReading? _shown;

    /// The source for the helper. publish gets the hottest reading, or null when none is shown (on the UI thread).
    public ThermalSource(SettingsStore settings, SensorHelper helper, Action<ThermalReading?> publish)
    {
        _settings = settings;
        _helper = helper;
        _publish = publish;
        _timer.Tick += (_, _) => _ = AskAsync();
        _helper.StateChanged += SettingsChanged;
    }

    public void Start()
    {
        _started = true;
        SettingsChanged();
    }

    public void Stop()
    {
        _started = false;
        SettingsChanged();
    }

    /// Unsubscribes from the helper's state and stops the timer. The source is not used after this.
    public void Dispose()
    {
        _helper.StateChanged -= SettingsChanged;
        _timer.Stop();
    }

    /// The pill opened or closed: the helper is asked only while it is open.
    public void PillOpened(bool open)
    {
        _pillOpen = open;
        SettingsChanged();
    }

    /// A gate changed (SystemLoad, sensorHelper, the helper's state, or the source's own start or stop): the timer runs or stops.
    public void SettingsChanged()
    {
        if (Active)
        {
            if (!_timer.IsEnabled)
            {
                _timer.Start();
                _ = AskAsync();  // the first reading at once, so the header is not empty for two seconds
            }
            return;
        }
        _timer.Stop();
        Clear();
    }

    /// Every gate that lets the helper be asked.
    private bool Active => _started && _pillOpen && _settings.SystemLoad && _settings.SensorHelper
        && _helper.State == HelperState.On;

    /// Asks once. A reply that arrives after a gate closed is dropped.
    private async Task AskAsync()
    {
        if (_asking || !Active)
        {
            return;
        }
        _asking = true;
        try
        {
            var hottest = Thermals.Hottest(await _helper.ReadAsync());
            if (Active)
            {
                Publish(hottest);
            }
        }
        finally
        {
            _asking = false;
        }
    }

    private void Publish(ThermalReading? hottest)
    {
        if (SameShown(_shown, hottest))
        {
            return;
        }
        _shown = hottest;
        _publish(hottest);
    }

    private void Clear()
    {
        if (_shown is null)
        {
            return;
        }
        _shown = null;
        _publish(null);
    }

    /// The same degrees of the same zone show the same header: a reading that moves inside one degree is not published.
    private static bool SameShown(ThermalReading? a, ThermalReading? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }
        return a.Value.Name == b.Value.Name && Math.Round(a.Value.Celsius) == Math.Round(b.Value.Celsius);
    }
}

/// Runs several sources as one module's feed: each is started and stopped with the module.
public sealed class CompositeSource : IModuleSource
{
    private readonly IModuleSource[] _parts;

    public CompositeSource(params IModuleSource[] parts)
    {
        _parts = parts;
    }

    public void Start()
    {
        foreach (var part in _parts)
        {
            part.Start();
        }
    }

    public void Stop()
    {
        foreach (var part in _parts)
        {
            part.Stop();
        }
    }
}
