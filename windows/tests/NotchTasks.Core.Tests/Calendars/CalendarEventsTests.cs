using NotchTasks.Core.Calendars;
using Xunit;

namespace NotchTasks.Core.Tests.Calendars;

public sealed class CalendarEventsTests
{
    private static CalendarRecord Timed(string id, DateTimeOffset start, TimeSpan length, string? details = null, string? uri = null) =>
        new(id, "Event " + id, start, start + length, false, "Work", "#0A84FF", null, details, uri);

    private static CalendarRecord AllDay(string id, DateTimeOffset start, int days) =>
        new(id, "All day " + id, start, start.AddDays(days), true, "Holidays", "#30D158", null, null, null);

    /// Local midnight today, as an offset time (the Pick rule for all-day events).
    private static DateTimeOffset LocalToday()
    {
        var now = DateTimeOffset.Now;
        return new DateTimeOffset(now.Date, now.Offset);
    }

    [Fact]
    public void Pick_PutsAllDayFirst_ThenTimedByStart()
    {
        var now = DateTimeOffset.Now;
        var records = new[]
        {
            Timed("late", now.AddHours(2), TimeSpan.FromHours(1)),
            AllDay("day", LocalToday(), 1),
            Timed("soon", now.AddHours(1), TimeSpan.FromHours(1)),
        };

        var events = CalendarEvents.Pick(records, now);

        Assert.Equal(new[] { "All day day", "Event soon", "Event late" }, events.Select(e => e.Title));
        Assert.True(events[0].AllDay);
    }

    [Fact]
    public void Pick_DropsTimedEventsThatEnded_KeepsRunningOnes()
    {
        var now = DateTimeOffset.Now;
        var records = new[]
        {
            Timed("ended", now.AddHours(-1), TimeSpan.FromMinutes(30)),
            Timed("running", now.AddMinutes(-10), TimeSpan.FromHours(1)),
        };

        var events = CalendarEvents.Pick(records, now);

        Assert.Equal(new[] { "Event running" }, events.Select(e => e.Title));
    }

    [Fact]
    public void Pick_KeepsAnAllDayEventThatSpansToday_DropsOneThatEndedBeforeToday()
    {
        var now = DateTimeOffset.Now;
        var today = LocalToday();
        var records = new[]
        {
            AllDay("yesterday", today.AddDays(-1), 1),
            AllDay("spanning", today.AddDays(-1), 3),
            AllDay("tomorrow", today.AddDays(1), 1),
        };

        var events = CalendarEvents.Pick(records, now);

        Assert.Equal(new[] { "All day spanning" }, events.Select(e => e.Title));
    }

    [Fact]
    public void Pick_ShowsAtMostTenEvents()
    {
        var now = DateTimeOffset.Now;
        var records = Enumerable.Range(0, 12).Select(i => Timed("e" + i, now.AddMinutes(10 + i), TimeSpan.FromMinutes(5))).ToList();

        var events = CalendarEvents.Pick(records, now);

        Assert.Equal(10, events.Count);
        Assert.Equal("Event e9", events[9].Title);
    }

    [Fact]
    public void Pick_TitleFallsBackToEvents_WhenBlank()
    {
        var now = DateTimeOffset.Now;
        var events = CalendarEvents.Pick(new[] { Timed("", now.AddMinutes(5), TimeSpan.FromMinutes(5)) with { Title = " " } }, now);

        Assert.Equal("Events", events[0].Title);
    }

    [Fact]
    public void Pick_IdIsTheStoreIdAndTheStart_AndEventIdIsTheStoreId()
    {
        var now = DateTimeOffset.Now;
        var start = now.AddMinutes(5);
        var events = CalendarEvents.Pick(new[] { Timed("abc", start, TimeSpan.FromMinutes(5)) }, now);

        Assert.Equal($"abc@{start.ToUnixTimeSeconds()}", events[0].Id);
        Assert.Equal("abc", events[0].EventId);
    }

    [Fact]
    public void Pick_ReadsTheMeetingLinkFromTheUriLocationOrDetails()
    {
        var now = DateTimeOffset.Now;
        var records = new[]
        {
            Timed("details", now.AddMinutes(5), TimeSpan.FromMinutes(5), details: "Join at https://meet.google.com/abc-defg-hij please"),
            Timed("uri", now.AddMinutes(6), TimeSpan.FromMinutes(5), uri: "https://zoom.us/j/12345?pwd=x"),
        };

        var events = CalendarEvents.Pick(records, now);

        Assert.Equal("https://meet.google.com/abc-defg-hij", events[0].Link);
        Assert.Equal("https://zoom.us/j/12345?pwd=x", events[1].Link);
    }

    [Theory]
    [InlineData("https://meet.google.com/abc-defg-hij", true)]
    [InlineData("https://us02web.zoom.us/j/123", true)]
    [InlineData("https://teams.microsoft.com/l/meetup-join/19%3ameeting", true)]
    [InlineData("https://teams.live.com/meet/9123", true)]
    [InlineData("https://acme.webex.com/meet/jane", true)]
    [InlineData("https://example.com/meeting", false)]
    [InlineData("http://zoom.us/j/1", false)]
    public void MeetingLink_MatchesTheMeetingServices(string link, bool expected)
    {
        var found = CalendarEvents.MeetingLink(link, null, null);

        Assert.Equal(expected ? link : null, found);
    }

    [Fact]
    public void MeetingLink_IsNullWithNoText()
    {
        Assert.Null(CalendarEvents.MeetingLink(null, null, null));
        Assert.Null(CalendarEvents.MeetingLink(string.Empty, "Room 4", string.Empty));
    }

    [Fact]
    public void Running_IsATimedEventBetweenItsStartAndEnd_AllDayNever()
    {
        var now = DateTimeOffset.Now;
        var running = CalendarEvents.Pick(new[] { Timed("r", now.AddMinutes(-30), TimeSpan.FromHours(1)) }, now)[0];
        var allDay = CalendarEvents.Pick(new[] { AllDay("d", LocalToday(), 1) }, now)[0];

        Assert.True(running.Running(now));
        Assert.False(allDay.Running(now));
        Assert.InRange(running.Elapsed(now), 0.49, 0.51);
    }

    [Fact]
    public void SpanDays_CountsTheDaysAnAllDayEventRunsPastItsFirst()
    {
        var now = DateTimeOffset.Now;
        var single = CalendarEvents.Pick(new[] { AllDay("one", LocalToday(), 1) }, now)[0];
        var three = CalendarEvents.Pick(new[] { AllDay("three", LocalToday(), 3) }, now)[0];

        Assert.Equal(0, single.SpanDays);
        Assert.Equal(2, three.SpanDays);
    }
}
