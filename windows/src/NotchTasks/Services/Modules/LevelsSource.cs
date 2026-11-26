using System.Windows.Threading;
using NotchTasks.Core.Levels;
using NotchTasks.Core.Modules;

namespace NotchTasks.Services.Modules;

/// The volume and brightness feed (the Mac's sampleLevels, on events instead of its 150 ms poll). Start binds the default output and
/// reads its volume and the panel's brightness as the baselines, then runs on events only: the endpoint's volume notification, the
/// default device's change (which binds the new output and re-baselines it, so the jump is not shown), and the panel's change. Each COM
/// or WMI callback arrives on another thread and is marshalled to the UI thread here. The bar hides after LevelsModule.Shown from a
/// one-shot timer that runs only while a bar is up. Stop releases the endpoint, the device watch and the panel, and clears the bar.
public sealed class LevelsSource : IModuleSource
{
    private readonly LevelsModule _module;
    private readonly Func<IOutputDevices> _outputs;
    private readonly Func<IBrightness> _brightness;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _hide = new() { Interval = LevelsModule.Shown };
    private IOutputDevices? _devices;
    private IBrightness? _panel;
    private Action<double>? _panelHandler;
    private Bound? _bound;  // the endpoint bound now, with the handler it was given
    private bool _started;
    private int _generation;  // bumps on each Start and Stop: a panel that opens after its start was stopped is given back

    /// The source of `module`, binding through `outputs` and `brightness` (made at Start). It is made on the UI thread.
    public LevelsSource(LevelsModule module, Func<IOutputDevices> outputs, Func<IBrightness> brightness)
    {
        _module = module;
        _outputs = outputs;
        _brightness = brightness;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _hide.Tick += (_, _) => _module.Hide();
        _module.Changed += OnModuleChanged;
    }

    /// Binds the default output (Core Audio, on this thread) and starts the panel's open on a worker, then returns: the panel's first WMI
    /// read and its watch can take a while to connect, so Start never waits on them. The output's volume is the baseline now, and the
    /// panel's brightness is the baseline when it lands. Nothing shows at start.
    public void Start()
    {
        _started = true;
        var generation = ++_generation;
        try
        {
            _devices = _outputs();
            _devices.DefaultChanged += OnDefaultChanged;
            Bind();
        }
        catch (Exception e)
        {
            ErrorLog.Write(e);
            _module.SetVolumeBaseline(null);
        }
        _ = Task.Run(() => OpenPanel(generation));
    }

    /// Stops the feeds and releases the COM objects and the panel's watch. The bar is cleared, and no timer runs. A panel still opening
    /// is given back when it lands: its generation is past.
    public void Stop()
    {
        _started = false;
        _generation++;
        _hide.Stop();
        Unbind();
        if (_devices is { } devices)
        {
            _devices = null;
            devices.DefaultChanged -= OnDefaultChanged;
            devices.Dispose();
        }
        if (_panel is { } panel)
        {
            _panel = null;
            if (_panelHandler is not null)
            {
                panel.Changed -= _panelHandler;
                _panelHandler = null;
            }
            panel.Watch(false);
            panel.Dispose();
        }
        _module.Clear();
    }

    /// On a worker: opens the panel, reads its brightness, and starts its change feed. The result goes back to the UI thread, which adopts it
    /// only when this open is still the current one.
    private void OpenPanel(int generation)
    {
        PanelOpening? opened = null;
        IBrightness? panel = null;
        Action<double>? handler = null;
        try
        {
            panel = _brightness();
            var level = panel.Read();
            if (level is not null)
            {
                handler = value => _dispatcher.BeginInvoke(new Action(() => OnBrightness(value)));
                panel.Changed += handler;
                panel.Watch(true);
            }
            opened = new PanelOpening(panel, level, handler);
        }
        catch (Exception e)
        {
            ErrorLog.Write(e);
            // A panel that failed while it was opened is given back here, whatever the exception: its feed off, then disposed.
            if (panel is not null)
            {
                GiveBack(panel, handler);
            }
        }
        _dispatcher.BeginInvoke(new Action(() => AdoptPanel(generation, opened)));
    }

    /// Releases a panel the source does not keep: its change feed is removed, it stops watching and it is disposed. A failure here is
    /// logged, so the cleanup never takes the worker down.
    private static void GiveBack(IBrightness panel, Action<double>? handler)
    {
        try
        {
            if (handler is not null)
            {
                panel.Changed -= handler;
            }
            panel.Watch(false);
        }
        catch (Exception e)
        {
            ErrorLog.Write(e);
        }
        finally
        {
            panel.Dispose();
        }
    }

    /// On the UI thread: the opened panel becomes the source's, and its brightness the baseline. A panel from a start that was stopped (or
    /// restarted) since is given back at once.
    private void AdoptPanel(int generation, PanelOpening? opened)
    {
        if (opened is null)
        {
            return;
        }
        if (!_started || generation != _generation)
        {
            if (opened.Handler is { } stale)
            {
                opened.Panel.Changed -= stale;
            }
            opened.Panel.Watch(false);
            opened.Panel.Dispose();
            return;
        }
        _panel = opened.Panel;
        _panelHandler = opened.Handler;
        _module.SetBrightnessBaseline(opened.Level);
    }

    /// A panel that was opened on the worker, with its first reading and the handler its change feed raises through.
    private sealed record PanelOpening(IBrightness Panel, double? Level, Action<double>? Handler);

    /// Called on a COM thread when the default device changes: the rebind runs on the UI thread.
    private void OnDefaultChanged() => _dispatcher.BeginInvoke(new Action(Rebind));

    /// Binds the default output now, and takes its volume as the baseline (its jump is not a change, as on the Mac).
    private void Bind()
    {
        var endpoint = _devices?.OpenDefault();
        if (endpoint is null)
        {
            _module.SetVolumeBaseline(null);
            return;
        }
        Action handler = () => _dispatcher.BeginInvoke(new Action(() => OnVolume(endpoint)));
        endpoint.Changed += handler;
        _bound = new Bound(endpoint, handler);
        _module.SetVolumeBaseline(endpoint.Read());
    }

    /// Drops the bound endpoint: its change is no longer heard, and its COM objects are released.
    private void Unbind()
    {
        if (_bound is not { } bound)
        {
            return;
        }
        _bound = null;
        bound.Endpoint.Changed -= bound.Handler;
        bound.Endpoint.Dispose();
    }

    /// The default output changed: the old endpoint goes, the new one is bound and re-baselined.
    private void Rebind()
    {
        if (!_started)
        {
            return;
        }
        try
        {
            Unbind();
            Bind();
        }
        catch (Exception e)
        {
            ErrorLog.Write(e);
        }
    }

    /// The bound endpoint's volume or mute changed. Read now; a change from an endpoint that is no longer bound is dropped.
    private void OnVolume(IOutputEndpoint endpoint)
    {
        if (!_started || !ReferenceEquals(_bound?.Endpoint, endpoint))
        {
            return;
        }
        if (endpoint.Read() is { } reading)
        {
            _module.Volume(reading);
        }
    }

    private void OnBrightness(double value)
    {
        if (_started)
        {
            _module.Brightness(value);
        }
    }

    /// A bar shows or hides: its timer restarts on each bar, and stops when none is up (no wake-ups while the notch is quiet).
    private void OnModuleChanged()
    {
        _hide.Stop();
        if (_module.Hud is not null)
        {
            _hide.Start();
        }
    }

    /// The bound endpoint and the handler that was added to its change.
    private sealed record Bound(IOutputEndpoint Endpoint, Action Handler);
}
