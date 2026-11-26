using System.Windows.Threading;
using NotchTasks.Core;
using NotchTasks.Core.Calendars;
using NotchTasks.Core.Modules;

namespace NotchTasks.Services.Modules;

/// The calendar module's feed. Start opens the store and reads it at once. While the store is open, a 30 s timer reads it again, and a
/// change in the store reads it at once. The meeting reminder and the chip's minutes are checked on the shared 1 s clock against the
/// cached events, so they come at the Mac's exact times. A read that fails keeps the store and the poll; only MaxFailures failed reads in
/// a row close the store and show the hint. A store that cannot open shows the hint at once. Stop closes the store, stops the timer and
/// clears the module, so a module that is off or has no access costs no wake-ups. Each Start is a new generation: a read still in flight
/// from an earlier one drops its result and changes nothing.
public sealed class CalendarSource : IModuleSource, IClockUser
{
    /// How often the store is read while it is open (the Mac's 30 s poll).
    public static readonly TimeSpan Poll = TimeSpan.FromSeconds(30);

    /// How many reads in a row must fail before the store is closed and the hint shows. One failure is a transient error (the store
    /// syncing, a read racing a change), and the next poll tries again.
    public const int MaxFailures = 3;

    private readonly CalendarModule _module;
    private readonly ICalendarOpener _opener;
    private readonly Action _publish;
    private readonly SharedClock _clock;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer = new() { Interval = Poll };
    private ICalendarStore? _store;
    private bool _started;
    private int _generation;  // bumps on each Start and Stop: a read or an open from an earlier generation is stale
    private bool _reading;
    private bool _again;
    private int _failures;  // reads in a row that failed, in this generation
    private string? _chipKey;  // the soon event and its minutes as the last clock tick saw them

    /// The source of `module`, reading through `opener`. publish runs on the UI thread after the module changes; clock is the app's
    /// shared one-second ticker. It is made on the UI thread.
    public CalendarSource(CalendarModule module, ICalendarOpener opener, Action publish, SharedClock clock)
    {
        _module = module;
        _opener = opener;
        _publish = publish;
        _clock = clock;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _timer.Tick += (_, _) => _ = ReadAsync();
        _clock.Add(this);
    }

    /// True while the store is open and a timed event is listed: the reminder and the chip's minutes need the clock then, and not otherwise.
    public bool NeedsClock => _started && _store is not null && _module.Events.Any(e => !e.AllDay);

    /// One second passed: makes the reminder of an event due now, and reports whether the chip's event or its minutes changed, so the app
    /// publishes once for either. Works on the cached events; no store read.
    public bool ClockTick(DateTimeOffset now)
    {
        var reminded = _module.Remind(now);
        var key = _module.SoonEvent(now) is { } soon ? $"{soon.Id}|{soon.MinutesShown(now)}" : null;
        var changed = key != _chipKey;
        _chipKey = key;
        return reminded || changed;
    }

    /// Opens the store and reads it. The hint shows at once if it cannot be opened.
    public void Start()
    {
        _started = true;
        _reading = false;
        _again = false;
        _failures = 0;
        _chipKey = null;
        var generation = ++_generation;
        _ = OpenAsync(generation);
    }

    /// Stops the poll, closes the store and clears the module (the source keeps no state). An in-flight read is left behind: its
    /// generation is over, so it publishes nothing.
    public void Stop()
    {
        _started = false;
        _generation++;
        _reading = false;
        _again = false;
        _timer.Stop();
        CloseStore();
        _module.Clear();
        _clock.Sync();
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

        if (generation != _generation)
        {
            // stopped or restarted while it was opening: this store is not wanted
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

    /// Reads the store for the window around now and feeds the module. A read that is already running is followed by one more, so a change
    /// during a read is not lost.
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
        var generation = _generation;
        try
        {
            do
            {
                _again = false;
                IReadOnlyList<CalendarRecord> records;
                try
                {
                    records = await store.ReadAsync(DateTimeOffset.Now - CalendarEvents.Back, CalendarEvents.Span);
                }
                catch (Exception e)
                {
                    if (generation == _generation)
                    {
                        ReadFailed(e);
                    }
                    return;
                }
                if (generation != _generation)
                {
                    return;  // stopped or restarted while reading: this result belongs to a store that is no longer wanted
                }

                _failures = 0;
                _module.Apply(records, DateTimeOffset.Now);
                _publish();
                _clock.Sync();
            }
            while (_again && generation == _generation && _store is not null);
        }
        finally
        {
            if (generation == _generation)
            {
                _reading = false;
            }
        }
    }

    /// One read failed. Below MaxFailures the store and the poll stay, and the next read tries again. At MaxFailures in a row the store is
    /// closed, the timer stops, and the module shows its hint.
    private void ReadFailed(Exception error)
    {
        _failures++;
        ErrorLog.Write(error);
        if (_failures < MaxFailures)
        {
            return;
        }
        _timer.Stop();
        CloseStore();
        _module.SetUnavailable();
        _publish();
        _clock.Sync();
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
