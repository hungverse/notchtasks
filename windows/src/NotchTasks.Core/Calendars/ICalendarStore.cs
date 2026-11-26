namespace NotchTasks.Core.Calendars;

/// The OS calendar store the module reads: the Windows appointment store in the app, a fake in the tests. Changed is raised when
/// the store's calendars change; it may come on any thread, so the source marshals it to the UI thread.
public interface ICalendarStore : IDisposable
{
    event Action? Changed;

    /// The appointments that overlap the window from `from` for `length`, with their calendars' colours.
    Task<IReadOnlyList<CalendarRecord>> ReadAsync(DateTimeOffset from, TimeSpan length);
}

/// Opens the calendar store for reading (AppointmentManager.RequestStoreAsync in the app). Null means the store cannot be opened:
/// no access to the calendars, or no store on this PC. A throw means the same.
public interface ICalendarOpener
{
    Task<ICalendarStore?> OpenAsync();
}
