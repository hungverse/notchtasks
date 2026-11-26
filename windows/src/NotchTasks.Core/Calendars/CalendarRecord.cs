namespace NotchTasks.Core.Calendars;

/// One appointment as the OS calendar store reads it, before the module picks the ones to show. Plain values, so Core stays free
/// of WinRT: the app maps the store's appointments to these. Color is the calendar's colour as #RRGGBB. OnlineMeetingLink, Location,
/// Details and Uri are the text a video-call link may be in, read in that order (the Mac reads the event's url, location and notes).
public sealed record CalendarRecord(
    string Id,
    string Title,
    DateTimeOffset Start,
    DateTimeOffset End,
    bool AllDay,
    string Calendar,
    string Color,
    string? Location,
    string? Details,
    string? Uri,
    string? OnlineMeetingLink = null);
