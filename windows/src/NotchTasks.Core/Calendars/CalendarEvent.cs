namespace NotchTasks.Core.Calendars;

/// One event the notch shows (the Mac's CalEvent). Id is the store id and the start, so a moved event is a new one (dismissals
/// follow it); EventId is the store's own id; Link is the video-call link the event carries, when it has one; Color is #RRGGBB.
public sealed record CalendarEvent(
    string Id,
    string Title,
    DateTimeOffset Start,
    DateTimeOffset End,
    string Color,
    bool AllDay,
    string Calendar,
    string? Link,
    string? EventId)
{
    /// True while a timed event runs. An all-day event never runs, as on the Mac.
    public bool Running(DateTimeOffset now) => !AllDay && Start <= now && now < End;

    /// The share of a running event that has passed, 0 to 1 (a zero-length event reads as finished).
    public double Elapsed(DateTimeOffset now) =>
        Math.Clamp((now - Start).TotalSeconds / Math.Max((End - Start).TotalSeconds, 1), 0, 1);

    /// How many days an all-day event runs past its first day: 0 for a single day (the Mac's "until" line shows from 1).
    public int SpanDays
    {
        get
        {
            var first = Start.ToLocalTime().Date;
            var last = End.AddSeconds(-1).ToLocalTime().Date;
            return Math.Max(0, (last - first).Days);
        }
    }

    /// The minutes the chip shows: until the start while it has not started, else until the end (the Mac's Int(seconds / 60) + 1).
    public int MinutesShown(DateTimeOffset now)
    {
        var until = Start - now;
        var seconds = until > TimeSpan.Zero ? until.TotalSeconds : (End - now).TotalSeconds;
        return (int)(seconds / 60) + 1;
    }

    /// The last day of an all-day event (its end less a second), in local time.
    public DateTimeOffset LastDay => End.AddSeconds(-1).ToLocalTime();
}
