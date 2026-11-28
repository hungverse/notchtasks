using System.Windows.Threading;
using NotchTasks.Core.Modules;

namespace NotchTasks.Services.Modules;

/// The heat toast's feed (Mac: thermalChanged, Model.swift). It runs with the health module, and asks every 30 s, with the pill open or
/// closed: the thermal counter (no administrator), and while the helper is On its readings come first (their CPU reading when there is
/// one). It runs only while the counter reads here or the helper is On, so a PC with neither does no work for it. Each reading is judged by the HeatModule, which keeps the crossing levels across stops; topTool names the working tool
/// for the toast's subtitle.
public sealed class HeatSource : IModuleSource, IDisposable
{
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(30);

    private readonly HeatModule _module;
    private readonly SensorHelper _helper;
    private readonly IThermalCounter _counter;
    private readonly Func<string?> _topTool;
    private readonly DispatcherTimer _timer = new() { Interval = Every };
    private bool _started;
    private bool _asking;
    private bool _counterReads;  // the counter gave a plausible reading when it was last read (a probe at the start)

    /// The source for `module`, fed by the counter and `helper`. topTool gives the working tool's name (null when none works).
    public HeatSource(HeatModule module, SensorHelper helper, IThermalCounter counter, Func<string?> topTool)
    {
        _module = module;
        _helper = helper;
        _counter = counter;
        _topTool = topTool;
        _timer.Tick += (_, _) => _ = AskAsync();
        _helper.StateChanged += SettingsChanged;
    }

    public void Start()
    {
        _started = true;
        _ = ProbeAsync();
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

    /// A gate changed (the source started or stopped, the counter probe, or the helper's state): the timer runs or stops. The first
    /// reading is asked at once.
    private void SettingsChanged()
    {
        if (Active)
        {
            if (!_timer.IsEnabled)
            {
                _timer.Start();
                _ = AskAsync();
            }
            return;
        }
        _timer.Stop();
    }

    /// The source runs, and the counter reads here or the helper is On.
    private bool Active => _started && (_counterReads || _helper.State == HelperState.On);

    /// Whether the counter reads here, from one read.
    private async Task ProbeAsync()
    {
        var zones = await _counter.ReadAsync();
        _counterReads = Thermals.Plausible(zones).Count > 0;
        SettingsChanged();
    }

    /// Asks once and judges the hottest reading: the counter's, else the helper's (when it is On). A reply that arrives after the source
    /// stopped is dropped.
    private async Task AskAsync()
    {
        if (_asking || !Active)
        {
            return;
        }
        _asking = true;
        try
        {
            var zones = await _counter.ReadAsync();
            _counterReads = Thermals.Plausible(zones).Count > 0;
            if (_helper.State == HelperState.On)
            {
                // The helper's readings are the real ones: they win whenever it gives a plausible one.
                var fromHelper = await _helper.ReadAsync();
                if (Thermals.Plausible(fromHelper).Count > 0)
                {
                    zones = fromHelper;
                }
            }
            if (!_counterReads && _helper.State != HelperState.On)
            {
                SettingsChanged();  // no counter reading and no helper: nothing to judge, and the timer stops
                return;
            }
            if (Active)
            {
                _module.Judge(Thermals.Headline(zones), _topTool());
            }
        }
        finally
        {
            _asking = false;
        }
    }
}
