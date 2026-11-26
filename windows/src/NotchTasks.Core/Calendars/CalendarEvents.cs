using System.Globalization;
using System.Text.RegularExpressions;

namespace NotchTasks.Core.Calendars;

/// Picks the events the notch shows from the store's appointments, and finds their video-call links (the Mac's CalendarSource).
public static class CalendarEvents
{
    /// The most events the notch lists at once (the Mac's prefix(10)).
    public const int Max = 10;

    /// How far back the store is read: events that started up to this long ago, still running, are shown (the Mac's -6 h).
    public static readonly TimeSpan Back = TimeSpan.FromHours(6);

    /// How long the store is read ahead, so the window is Back plus Span (the Mac's +12 h from now).
    public static readonly TimeSpan Span = TimeSpan.FromHours(18);

    /// The meeting services a link may point to, as the Mac's meetingPattern: Google Meet, Zoom, Teams and Webex, over https.
    private static readonly Regex MeetingPattern = new(
        @"https://[\w.-]*(meet\.google\.com|zoom\.us|teams\.microsoft\.com|teams\.live\.com|webex\.com)/[^\s""<>]*",
        RegexOptions.CultureInvariant);

    /// The events to show: the all-day ones that run today (local day), then the timed ones that have not ended, by start; at most
    /// Max. A blank title reads "Events" (the Mac's fallback). Id is the store id and the start, so a moved event is a new one.
    public static IReadOnlyList<CalendarEvent> Pick(IEnumerable<CalendarRecord> records, DateTimeOffset now)
    {
        var local = now.ToLocalTime();
        var today = new DateTimeOffset(local.Date, local.Offset);
        var tomorrow = today.AddDays(1);
        return records
            .Where(r => r.AllDay ? r.Start < tomorrow && r.End > today : r.End > now)
            .OrderBy(r => r.AllDay ? 0 : 1)
            .ThenBy(r => r.Start)
            .Take(Max)
            .Select(r => new CalendarEvent(
                $"{r.Id}@{r.Start.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}",
                string.IsNullOrWhiteSpace(r.Title) ? L10n.L("Events") : r.Title,
                r.Start,
                r.End,
                r.Color,
                r.AllDay,
                r.Calendar,
                MeetingLink(r.OnlineMeetingLink, r.Location, r.Details, r.Uri),
                r.Id))
            .ToList();
    }

    /// The video-call link of the event: the first Meet, Zoom, Teams or Webex link in the online meeting link, then the location,
    /// then the notes, then the url; else null. Each field is searched on its own, in that order.
    public static string? MeetingLink(string? onlineMeetingLink, string? location, string? details, string? uri)
    {
        foreach (var field in new[] { onlineMeetingLink, location, details, uri })
        {
            if (string.IsNullOrEmpty(field))
            {
                continue;
            }

            var match = MeetingPattern.Match(field);
            if (match.Success)
            {
                return match.Value;
            }
        }

        return null;
    }
}
