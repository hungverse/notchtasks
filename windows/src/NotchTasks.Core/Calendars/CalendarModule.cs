using System.Globalization;
using NotchTasks.Core.Modules;
using NotchTasks.Core.Notch;

namespace NotchTasks.Core.Calendars;

/// The calendar module (the Mac's refreshEvents, dismissedEvents and the meeting toast). It holds the events the notch lists, the
/// ones the user hid, and the meeting toast per event. A timed event that starts within 300 s (and has not started) gets one meeting
/// toast, once. The source reads the store and calls Apply, so this has no timers and no OS calls. Dismissals are kept through
/// saveDismissed, so a hidden event stays hidden after a restart, as on the Mac.
public sealed class CalendarModule
{
    /// How long before a timed event its meeting toast is made (the Mac's 0...300 s).
    public static readonly TimeSpan ReminderLead = TimeSpan.FromSeconds(300);

    /// How long before a timed event the notch's chip shows it (the Mac's 15 min window).
    public static readonly TimeSpan SoonWindow = TimeSpan.FromMinutes(15);

    private readonly IAnnouncer _announce;
    private readonly Action<IReadOnlyList<string>> _saveDismissed;
    private readonly HashSet<string> _dismissed;
    private readonly HashSet<string> _reminded = new(StringComparer.Ordinal);

    /// The announcer receives the meeting toasts. loadDismissed gives the ids the user hid earlier; saveDismissed gets the whole set
    /// whenever it changes (null for both: nothing is kept between runs).
    public CalendarModule(IAnnouncer announce, Func<IReadOnlyList<string>>? loadDismissed = null, Action<IReadOnlyList<string>>? saveDismissed = null)
    {
        _announce = announce;
        _saveDismissed = saveDismissed ?? (_ => { });
        _dismissed = new HashSet<string>(loadDismissed?.Invoke() ?? Array.Empty<string>(), StringComparer.Ordinal);
    }

    /// The events the store gave, in the order the notch lists them (all-day first, then by start). Dismissed ones included.
    public IReadOnlyList<CalendarEvent> Events { get; private set; } = Array.Empty<CalendarEvent>();

    /// True when the store could not be opened: the card shows the hint, and nothing is read until the module is turned off and on.
    public bool Unavailable { get; private set; }

    /// The ids the user hid (the Mac's dismissedEvents).
    public IReadOnlySet<string> Dismissed => _dismissed;

    /// The events the notch shows: those not hidden.
    public IReadOnlyList<CalendarEvent> Visible => Events.Where(e => !_dismissed.Contains(e.Id)).ToList();

    /// Raised when Events or Unavailable changes. Dismissing raises it too.
    public event Action? Changed;

    /// Takes the store's appointments as they are at `now`: the events change, the hidden ones that are gone are forgotten, and each
    /// timed event that starts within ReminderLead gets its meeting toast once. Changed is raised only when the events or the hint change.
    public void Apply(IReadOnlyList<CalendarRecord> records, DateTimeOffset now)
    {
        var events = CalendarEvents.Pick(records, now);
        var changed = Unavailable || !events.SequenceEqual(Events);
        Events = events;
        Unavailable = false;

        var ids = events.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        _reminded.IntersectWith(ids);
        if (_dismissed.RemoveWhere(id => !ids.Contains(id)) > 0)
        {
            Save();
        }

        Remind(now);
        if (changed)
        {
            Changed?.Invoke();
        }
    }

    /// Makes the meeting toast of each visible timed event that starts within ReminderLead of `now`, once per event. The clock calls it
    /// every second, so the toast comes at the Mac's exact lead time, not only when the store is read. Returns true when a toast was made.
    public bool Remind(DateTimeOffset now)
    {
        var made = false;
        foreach (var e in Visible)
        {
            var until = e.Start - now;
            if (!e.AllDay && until >= TimeSpan.Zero && until <= ReminderLead && _reminded.Add(e.Id))
            {
                Announce(e);
                made = true;
            }
        }
        return made;
    }

    /// The store could not be opened or read: no events, and the hint shows. No reminders are made until a store reads again.
    public void SetUnavailable()
    {
        var changed = !Unavailable || Events.Count > 0;
        Unavailable = true;
        Events = Array.Empty<CalendarEvent>();
        _reminded.Clear();
        if (changed)
        {
            Changed?.Invoke();
        }
    }

    /// The module is turned off: no events and no hint. Hidden events stay hidden, as the Mac keeps them.
    public void Clear()
    {
        if (Events.Count == 0 && !Unavailable)
        {
            return;
        }
        Events = Array.Empty<CalendarEvent>();
        Unavailable = false;
        Changed?.Invoke();
    }

    /// Hides an event from the notch, and keeps that across runs.
    public void Dismiss(string id)
    {
        if (!_dismissed.Add(id))
        {
            return;
        }
        Save();
        Changed?.Invoke();
    }

    /// The event the notch's chip shows: a timed event that starts within SoonWindow, else a timed one that runs now. Null when none.
    public CalendarEvent? SoonEvent(DateTimeOffset now)
    {
        var timed = Visible.Where(e => !e.AllDay).ToList();
        return timed.FirstOrDefault(e => Soon(e, now)) ?? timed.FirstOrDefault(e => e.Running(now));
    }

    /// True when a timed event starts within SoonWindow from `now` (not yet started, and not further off).
    private static bool Soon(CalendarEvent e, DateTimeOffset now)
    {
        var until = e.Start - now;
        return until >= TimeSpan.Zero && until < SoonWindow;
    }

    /// The meeting toast: the event's title, "At" and its start time, the Open button to its video-call link when it has one.
    private void Announce(CalendarEvent e) =>
        _announce.Announce(new Toast(
            Glyph: "calendar",
            Tint: e.Color,
            Title: e.Title,
            Subtitle: L10n.L("At ") + e.Start.ToLocalTime().ToString("t", CultureInfo.CurrentCulture),
            Event: "meeting",
            Url: e.Link,
            Module: "calendar"));

    private void Save() => _saveDismissed(_dismissed.OrderBy(id => id, StringComparer.Ordinal).ToList());
}
