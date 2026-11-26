using NotchTasks.Core.Calendars;
using NotchTasks.Core.Modules;
using NotchTasks.Core.Notch;
using Xunit;

namespace NotchTasks.Core.Tests.Calendars;

public sealed class CalendarModuleTests
{
    /// Keeps every toast the module makes, so a test can count the meeting toasts.
    private sealed class RecordingAnnouncer : IAnnouncer
    {
        public List<Toast> Toasts { get; } = new();

        public void Announce(Toast t, bool notify = true) => Toasts.Add(t);
    }

    private const string Link = "https://meet.google.com/abc-defg-hij";

    private static CalendarRecord Timed(string id, DateTimeOffset start, TimeSpan length, string? uri = null) =>
        new(id, "Event " + id, start, start + length, false, "Work", "#0A84FF", null, null, uri);

    private static CalendarRecord AllDay(string id, DateTimeOffset start) =>
        new(id, "All day " + id, start, start.AddDays(1), true, "Holidays", "#30D158", null, null, null);

    private static CalendarModule Module(RecordingAnnouncer announce, IReadOnlyList<string>? dismissed = null, List<IReadOnlyList<string>>? saved = null) =>
        new(announce, () => dismissed ?? Array.Empty<string>(), list => saved?.Add(list));

    [Fact]
    public void Apply_ShowsTheEvents_AndRaisesChangedOnlyWhenTheyChange()
    {
        var now = DateTimeOffset.Now;
        var announce = new RecordingAnnouncer();
        var module = Module(announce);
        var changes = 0;
        module.Changed += () => changes++;
        var records = new[] { Timed("a", now.AddHours(1), TimeSpan.FromHours(1)) };

        module.Apply(records, now);
        module.Apply(records, now);

        Assert.Equal(new[] { "Event a" }, module.Visible.Select(e => e.Title));
        Assert.Equal(1, changes);
    }

    [Fact]
    public void Apply_MakesOneMeetingToast_InTheFiveMinutesBeforeATimedEvent()
    {
        var now = DateTimeOffset.Now;
        var announce = new RecordingAnnouncer();
        var module = Module(announce);
        var records = new[] { Timed("standup", now.AddSeconds(200), TimeSpan.FromMinutes(15), uri: Link) };

        module.Apply(records, now);
        module.Apply(records, now.AddSeconds(30));

        var toast = Assert.Single(announce.Toasts);
        Assert.Equal("meeting", toast.Event);
        Assert.Equal("Event standup", toast.Title);
        Assert.StartsWith("At ", toast.Subtitle);
        Assert.Equal(Link, toast.Url);
        Assert.Equal("calendar", toast.Glyph);
        Assert.Equal("calendar", toast.Module);
        Assert.Equal("#0A84FF", toast.Tint);
    }

    [Fact]
    public void Apply_MakesNoToast_BeyondFiveMinutesOrAfterTheStart()
    {
        var now = DateTimeOffset.Now;
        var announce = new RecordingAnnouncer();
        var module = Module(announce);

        module.Apply(new[] { Timed("later", now.AddMinutes(10), TimeSpan.FromMinutes(30)) }, now);
        module.Apply(new[] { Timed("started", now.AddSeconds(-10), TimeSpan.FromMinutes(30)) }, now);

        Assert.Empty(announce.Toasts);
    }

    [Fact]
    public void Apply_MakesNoToast_ForAnAllDayEvent()
    {
        var now = DateTimeOffset.Now;
        var announce = new RecordingAnnouncer();
        var module = Module(announce);

        module.Apply(new[] { AllDay("holiday", new DateTimeOffset(now.Date, now.Offset)) }, now);

        Assert.Empty(announce.Toasts);
    }

    [Fact]
    public void Apply_MakesNoToast_ForADismissedEvent()
    {
        var now = DateTimeOffset.Now;
        var announce = new RecordingAnnouncer();
        var start = now.AddSeconds(200);
        var record = Timed("standup", start, TimeSpan.FromMinutes(15));
        var module = Module(announce);
        module.Dismiss($"standup@{start.ToUnixTimeSeconds()}");

        module.Apply(new[] { record }, now);

        Assert.Empty(announce.Toasts);
    }

    [Fact]
    public void Dismiss_HidesTheEvent_AndSavesIt()
    {
        var now = DateTimeOffset.Now;
        var saved = new List<IReadOnlyList<string>>();
        var module = Module(new RecordingAnnouncer(), saved: saved);
        module.Apply(new[] { Timed("a", now.AddHours(1), TimeSpan.FromHours(1)) }, now);
        var id = module.Events[0].Id;

        module.Dismiss(id);

        Assert.Empty(module.Visible);
        Assert.Equal(new[] { id }, Assert.Single(saved));
    }

    [Fact]
    public void Dismissed_FromAnEarlierRun_HidesTheEventAtOnce()
    {
        var now = DateTimeOffset.Now;
        var start = now.AddHours(1);
        var id = $"a@{start.ToUnixTimeSeconds()}";
        var module = Module(new RecordingAnnouncer(), dismissed: new[] { id });

        module.Apply(new[] { Timed("a", start, TimeSpan.FromHours(1)) }, now);

        Assert.Equal(id, module.Events[0].Id);
        Assert.Empty(module.Visible);
    }

    [Fact]
    public void Apply_ForgetsADismissal_WhenTheEventIsGone()
    {
        var now = DateTimeOffset.Now;
        var saved = new List<IReadOnlyList<string>>();
        var module = Module(new RecordingAnnouncer(), dismissed: new[] { "gone@1" }, saved: saved);

        module.Apply(Array.Empty<CalendarRecord>(), now);

        Assert.Empty(module.Dismissed);
        Assert.Equal(Array.Empty<string>(), Assert.Single(saved));
    }

    [Fact]
    public void SetUnavailable_ClearsTheEvents_AndSaysSo_UntilClear()
    {
        var now = DateTimeOffset.Now;
        var module = Module(new RecordingAnnouncer());
        module.Apply(new[] { Timed("a", now.AddHours(1), TimeSpan.FromHours(1)) }, now);

        module.SetUnavailable();

        Assert.True(module.Unavailable);
        Assert.Empty(module.Events);

        module.Clear();

        Assert.False(module.Unavailable);
    }

    [Fact]
    public void Apply_ClearsTheUnavailableHint_WhenTheStoreReads()
    {
        var now = DateTimeOffset.Now;
        var module = Module(new RecordingAnnouncer());
        module.SetUnavailable();

        module.Apply(Array.Empty<CalendarRecord>(), now);

        Assert.False(module.Unavailable);
    }

    [Fact]
    public void SoonEvent_IsATimedEventInTheNextFifteenMinutes_ElseARunningOne()
    {
        var now = DateTimeOffset.Now;
        var module = Module(new RecordingAnnouncer());
        module.Apply(new[]
        {
            Timed("far", now.AddMinutes(40), TimeSpan.FromMinutes(30)),
            Timed("running", now.AddMinutes(-5), TimeSpan.FromMinutes(60)),
        }, now);

        Assert.Equal("Event running", module.SoonEvent(now)!.Title);

        module.Apply(new[] { Timed("soon", now.AddMinutes(10), TimeSpan.FromMinutes(30)) }, now);

        Assert.Equal("Event soon", module.SoonEvent(now)!.Title);
    }

    [Fact]
    public void SoonEvent_IsNullForAnAllDayEventOrNothingNear()
    {
        var now = DateTimeOffset.Now;
        var module = Module(new RecordingAnnouncer());
        module.Apply(new[] { AllDay("holiday", new DateTimeOffset(now.Date, now.Offset)), Timed("far", now.AddMinutes(20), TimeSpan.FromMinutes(30)) }, now);

        Assert.Null(module.SoonEvent(now));
    }

    [Fact]
    public void Clear_EmptiesTheEvents_WithoutAToast()
    {
        var now = DateTimeOffset.Now;
        var announce = new RecordingAnnouncer();
        var module = Module(announce);
        module.Apply(new[] { Timed("a", now.AddHours(1), TimeSpan.FromHours(1)) }, now);
        var before = announce.Toasts.Count;

        module.Clear();

        Assert.Empty(module.Events);
        Assert.Equal(before, announce.Toasts.Count);
    }
}
