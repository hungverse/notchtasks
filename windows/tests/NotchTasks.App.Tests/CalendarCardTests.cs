using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NotchTasks.Core;
using NotchTasks.Core.Calendars;
using NotchTasks.Core.Notch;
using NotchTasks.Pill;
using NotchTasks.Pill.Sections;
using Xunit;

namespace NotchTasks.AppTests;

/// The calendar card in the open pill, its chip in the closed pill, and the calendar's state on the app state.
public sealed class CalendarCardTests
{
    private const string Link = "https://meet.google.com/abc-defg-hij";

    private static CalendarRecord Timed(string id, DateTimeOffset start, TimeSpan length, string? details = null) =>
        new(id, "Event " + id, start, start + length, false, "Work", "#0A84FF", null, details, null);

    private static CalendarRecord AllDay(DateTimeOffset start) =>
        new("holiday", "Holiday", start, start.AddDays(1), true, "Holidays", "#30D158", null, null, null);

    /// Every text block under the element, in the order the tree gives them.
    private static IEnumerable<string> Texts(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TextBlock text)
            {
                yield return text.Text;
            }
            foreach (var inner in Texts(child))
            {
                yield return inner;
            }
        }
    }

    private static DateTimeOffset LocalToday()
    {
        var now = DateTimeOffset.Now;
        return new DateTimeOffset(now.Date, now.Offset);
    }

    [Fact]
    public void Card_IsNothing_WithNoEventsAndNoHint()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            state.Calendar.Apply(Array.Empty<CalendarRecord>(), DateTimeOffset.Now);
            var card = new CalendarCard();

            Assert.False(card.Update(state));
        });
    }

    [Fact]
    public void Card_IsNothing_WhenTheStoreCannotOpen_TheHintIsInTheModulesPaneInstead()
    {
        // Ruling: without calendar access the pill shows nothing for calendar, as the Mac does. The hint is the Modules pane's caption.
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            state.Calendar.SetUnavailable();
            var card = new CalendarCard();

            Assert.False(card.Update(state));
        });
    }

    [Fact]
    public void Card_ListsAllDayThenTheTimedEvents_WithJoinForAMeetingLink()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            var now = DateTimeOffset.Now;
            state.Calendar.Apply(new[]
            {
                AllDay(LocalToday()),
                Timed("now", now.AddMinutes(-10), TimeSpan.FromMinutes(40), details: "Join " + Link),
                Timed("next", now.AddHours(2), TimeSpan.FromMinutes(30)),
            }, now);
            var card = new CalendarCard();

            Assert.True(card.Update(state));

            var texts = Texts(card).ToList();
            Assert.Contains("All day", texts);
            Assert.Contains("Holiday", texts);
            Assert.Contains("Event now", texts);
            Assert.Contains("Event next", texts);
            Assert.Contains(L10n.L("Join"), texts);
            // the span comes first ("9:00–9:40 · Happening now · 29 min left"), so the status is inside the line
            Assert.Contains(texts, text => text.Contains(L10n.L("Happening now"), StringComparison.Ordinal));
        });
    }

    [Fact]
    public void Card_IsNothing_AfterTheLastEventIsHidden()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            var now = DateTimeOffset.Now;
            state.Calendar.Apply(new[] { Timed("only", now.AddHours(1), TimeSpan.FromHours(1)) }, now);
            var card = new CalendarCard();
            Assert.True(card.Update(state));

            state.DismissCalendarEvent(state.Calendar.Events[0].Id);

            Assert.False(card.Update(state));
        });
    }

    [Fact]
    public void Chip_Trail_CountsTheMinutesToAnUpcomingEvent()
    {
        Sta.Run(() =>
        {
            var now = DateTimeOffset.Now;
            var soon = CalendarEvents.Pick(new[] { Timed("a", now.AddMinutes(10), TimeSpan.FromMinutes(30)) }, now)[0];
            var chip = new Chip(ChipKind.Calendar, "calendar", "calendar") { Event = soon };

            var trail = Assert.IsType<TextBlock>(ChipView.Trail(chip, 11, null, new Dictionary<string, string>(), now));

            Assert.Equal("11′", trail.Text);  // the Mac's Int(seconds / 60) + 1, with the prime
        });
    }

    [Fact]
    public void Chip_Lead_IsTheGlyphWhenUpcoming_AndARingWhenRunning()
    {
        Sta.Run(() =>
        {
            var now = DateTimeOffset.Now;
            var upcoming = CalendarEvents.Pick(new[] { Timed("u", now.AddMinutes(10), TimeSpan.FromMinutes(30)) }, now)[0];
            var running = CalendarEvents.Pick(new[] { Timed("r", now.AddMinutes(-10), TimeSpan.FromMinutes(30)) }, now)[0];
            var colors = new Dictionary<string, string>();

            var glyph = ChipView.Lead(new Chip(ChipKind.Calendar, "calendar", "calendar") { Event = upcoming }, 18, null, colors);
            var ring = ChipView.Lead(new Chip(ChipKind.Calendar, "calendar", "calendar") { Event = running }, 18, null, colors);

            Assert.IsType<TextBlock>(glyph);
            Assert.IsType<Grid>(ring);
        });
    }

    [Fact]
    public void Chip_Tint_IsTheModuleColourElseTheEventsCalendarColour()
    {
        var now = DateTimeOffset.Now;
        var soon = CalendarEvents.Pick(new[] { Timed("a", now.AddMinutes(10), TimeSpan.FromMinutes(30)) }, now)[0];
        var chip = new Chip(ChipKind.Calendar, "calendar", "calendar") { Event = soon };

        Assert.Equal(Color.FromRgb(0x0A, 0x84, 0xFF), ChipView.TintOf(chip, null, new Dictionary<string, string>()));
        Assert.Equal(Color.FromRgb(0xFF, 0x45, 0x3A), ChipView.TintOf(chip, null, new Dictionary<string, string> { ["calendar"] = "#FF453A" }));
    }

    [Fact]
    public void AppState_ASoonEvent_IsTheSnapshotsChip_AndItsMeetingToastIsQueued()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            var now = DateTimeOffset.Now;

            // 200 s before the start: inside the five-minute reminder and the fifteen-minute chip window.
            state.Calendar.Apply(new[] { Timed("standup", now.AddSeconds(200), TimeSpan.FromMinutes(15), details: Link) }, now);
            state.Publish();

            var toast = state.Toasts.Current;
            Assert.NotNull(toast);
            Assert.Equal("meeting", toast!.Event);
            Assert.Equal("Event standup", toast.Title);
            Assert.Equal(Link, toast.Url);
            Assert.NotNull(state.Snapshot.SoonEvent);
            Assert.Contains(ChipBoard.Chips(state.Snapshot), chip => chip.Kind == ChipKind.Calendar);
        });
    }

    [Fact]
    public void AppState_DismissCalendarEvent_TakesItOffTheSnapshot()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            var now = DateTimeOffset.Now;
            state.Calendar.Apply(new[] { Timed("standup", now.AddSeconds(200), TimeSpan.FromMinutes(15)) }, now);
            state.Publish();

            state.DismissCalendarEvent(state.Calendar.Events[0].Id);

            Assert.Null(state.Snapshot.SoonEvent);
            Assert.DoesNotContain(ChipBoard.Chips(state.Snapshot), chip => chip.Kind == ChipKind.Calendar);
        });
    }
}
