using NotchTasks.Core.Calendars;
using Windows.ApplicationModel.Appointments;

namespace NotchTasks.Services.Appointments;

/// Opens this PC's calendars for reading (Mac: EKEventStore with the calendar permission). Windows' appointment store gives read-only
/// access to every calendar the user has added to Windows (Outlook, Microsoft account, Exchange). It gives null, or throws, when this
/// app may not read them, which the calendar module shows as its hint.
public sealed class AppointmentCalendarOpener : ICalendarOpener
{
    public async Task<ICalendarStore?> OpenAsync()
    {
        var store = await AppointmentManager.RequestStoreAsync(AppointmentStoreAccessType.AllCalendarsReadOnly);
        return store is null ? null : new AppointmentCalendarStore(store);
    }
}

/// The appointment store as the calendar module reads it: its appointments in a window, each with its calendar's name and colour,
/// and its change event. A calendar with no colour of its own reads as the Mac's default blue.
internal sealed class AppointmentCalendarStore : ICalendarStore
{
    private const string DefaultColor = "#0A84FF";
    private readonly AppointmentStore _store;

    public AppointmentCalendarStore(AppointmentStore store)
    {
        _store = store;
        _store.StoreChanged += OnStoreChanged;
    }

    public event Action? Changed;

    public async Task<IReadOnlyList<CalendarRecord>> ReadAsync(DateTimeOffset from, TimeSpan length)
    {
        var calendars = new Dictionary<string, (string Name, string Color)>(StringComparer.Ordinal);
        foreach (var calendar in await _store.FindAppointmentCalendarsAsync())
        {
            Windows.UI.Color? color = calendar.DisplayColor;
            calendars[calendar.LocalId] = (calendar.DisplayName ?? string.Empty, color is { } c ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : DefaultColor);
        }

        var appointments = await _store.FindAppointmentsAsync(from, length);
        return appointments.Select(a =>
        {
            var (name, color) = calendars.TryGetValue(a.CalendarId, out var found) ? found : (string.Empty, DefaultColor);
            return new CalendarRecord(
                a.LocalId,
                a.Subject ?? string.Empty,
                a.StartTime,
                a.StartTime + a.Duration,
                a.AllDay,
                name,
                color,
                a.Location,
                a.Details,
                a.Uri?.AbsoluteUri);
        }).ToList();
    }

    public void Dispose() => _store.StoreChanged -= OnStoreChanged;

    private void OnStoreChanged(AppointmentStore sender, AppointmentStoreChangedEventArgs args) => Changed?.Invoke();
}
