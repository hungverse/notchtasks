using System.Windows.Threading;
using NotchTasks.Core;
using NotchTasks.Core.Calendars;
using NotchTasks.Core.Modules;

namespace NotchTasks.Services.Modules;

/// The calendar module's feed (the Mac's refreshEvents and its 30 s poll). Start opens the store and reads it at once. While the store is
/// open, a 30 s timer reads it again, and a change in the store reads it at once. When the store cannot be opened, or fails to read, the
/// module shows its hint and nothing runs: no timer, no store. Stop closes the store, stops the timer and clears the module, so a module
/// that is off or has no access costs no wake-ups. Reads never overlap: a change during a read reads once more after it.
public sealed class CalendarSource : IModuleSource
{
    /// How often the store is read while it is open (the Mac's 30 s poll).
    public static readonly TimeSpan Poll = TimeSpan.FromSeconds(30);

    private readonly CalendarModule _module;
    private readonly ICalendarOpener _opener;
    private readonly Action _publish;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer = new() { Interval = Poll };
    private ICalendarStore? _store;
    private bool _started;
    private int _generation;  // bumps on each Start and Stop: an open that finishes after a Stop is dropped
    private bool _reading;
    private bool _again;

    /// The source of `module`, reading through `opener`; publish runs on the UI thread after the module changes. It is made on the UI thread.
    public CalendarSource(CalendarModule module, ICalendarOpener opener, Action publish)
    {
        _module = module;
        _opener = opener;
        _publish = publish;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _timer.Tick += (_, _) => _ = ReadAsync();
    }

    /// Opens the store and reads it. The hint shows at once if it cannot be opened.
    public void Start()
    {
        _started = true;
        var generation = ++_generation;
        _ = OpenAsync(generation);
    }

    /// Stops the poll, closes the store and clears the module (the source keeps no state).
    public void Stop()
    {
        _started = false;
        _generation++;
        _timer.Stop();
        CloseStore();
        _module.Clear();
    }

    private async Task OpenAsync(int generation)
    {
        ICalendarStore? store = null;
        try
        {
            store = await _opener.OpenAsync();
        }
        catch (Exception e)
        {
            ErrorLog.Write(e);
        }

        if (generation != _generation || !_started)
        {
            // stopped while it was opening: the store is not wanted
            store?.Dispose();
            return;
        }
        if (store is null)
        {
            _module.SetUnavailable();
            _publish();
            return;
        }

        _store = store;
        _store.Changed += OnChanged;
        _timer.Start();
        await ReadAsync();
    }

    /// Called on any thread when the store changes: the read runs on the UI thread.
    private void OnChanged() => _dispatcher.BeginInvoke(new Action(() => _ = ReadAsync()));

    /// Reads the store for the window around now and feeds the module. A read that fails closes the store and shows the hint.
    private async Task ReadAsync()
    {
        if (_store is not { } store || !_started)
        {
            return;
        }
        if (_reading)
        {
            _again = true;
            return;
        }

        _reading = true;
        try
        {
            do
            {
                _again = false;
                var now = DateTimeOffset.Now;
                var records = await store.ReadAsync(now - CalendarEvents.Back, CalendarEvents.Span);
                if (!_started || !ReferenceEquals(_store, store))
                {
                    return;
                }
                _module.Apply(records, DateTimeOffset.Now);
                _publish();
            }
            while (_again && _started && ReferenceEquals(_store, store));
        }
        catch (Exception e)
        {
            ErrorLog.Write(e);
            _timer.Stop();
            CloseStore();
            _module.SetUnavailable();
            _publish();
        }
        finally
        {
            _reading = false;
        }
    }

    /// Unsubscribes from the store and disposes it; does nothing when no store is open.
    private void CloseStore()
    {
        if (_store is not { } store)
        {
            return;
        }
        _store = null;
        store.Changed -= OnChanged;
        store.Dispose();
    }
}
