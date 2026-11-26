using System.Windows.Threading;
using NotchTasks.Core.Modules;

namespace NotchTasks.Services.Modules;

/// The heat toast's feed (Mac: thermalChanged, Model.swift). It runs with the health module, and asks the temperature helper for the
/// hottest reading every 30 s while the helper is On, with the pill open or closed. It is off while the helper is off, so an idle
/// PC does no work for it. Each reading is judged by the HeatModule, which keeps the crossing levels across stops; topTool names the
/// working tool for the toast's subtitle.
public sealed class HeatSource : IModuleSource, IDisposable
{
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(30);

    private readonly HeatModule _module;
    private readonly SensorHelper _helper;
    private readonly Func<string?> _topTool;
    private readonly DispatcherTimer _timer = new() { Interval = Every };
    private bool _started;
    private bool _asking;

    /// The source for `module`, fed by `helper`. topTool gives the working tool's name (null when none works).
    public HeatSource(HeatModule module, SensorHelper helper, Func<string?> topTool)
    {
        _module = module;
        _helper = helper;
        _topTool = topTool;
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

    /// A gate changed (the source started or stopped, or the helper's state): the timer runs or stops. The first reading is asked at once.
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

    /// Both gates: the source runs, and the helper answers.
    private bool Active => _started && _helper.State == HelperState.On;

    /// Asks once and judges the hottest reading. A reply that arrives after the helper stopped is dropped.
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
                _module.Judge(hottest, _topTool());
            }
        }
        finally
        {
            _asking = false;
        }
    }
}
