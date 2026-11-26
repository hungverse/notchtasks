using NotchTasks.Core.Agents;
using NotchTasks.Core.Calendars;
using NotchTasks.Core.Ci;
using NotchTasks.Core.Media;
using NotchTasks.Core.Notch;
using Xunit;

namespace NotchTasks.Core.Tests;

public class ChipBoardTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 19, 9, 0, 0, TimeSpan.Zero);
    private static readonly SensorState Quiet = new(Array.Empty<string>(), false, false);
    private static readonly SensorState Mic = new(new[] { "Teams" }, false, false);
    private static readonly SensorState Cam = new(Array.Empty<string>(), true, false);
    private static readonly CallInfo Call = new("Teams", "Team Meeting", T0, false, false);
    private static readonly TimerState Timer = new(T0.AddMinutes(5), TimeSpan.FromMinutes(5));

    private static NotchSnapshot Snap(string[] active, int asks = 0, TimerState? timer = null,
        CallInfo? call = null, SensorState? sensors = null) =>
        new(active, asks, timer, call, sensors ?? Quiet, null, null);

    private static IReadOnlyDictionary<string, string> P(params (string Module, string Side)[] pairs) =>
        pairs.ToDictionary(p => p.Module, p => p.Side);

    [Fact]
    public void Calls_AlwaysFirst_EvenWhenOrderedLater()
    {
        var chips = ChipBoard.Chips(Snap(new[] { "timer", "calls" }, timer: Timer, call: Call));

        Assert.Equal(new[] { ChipKind.Call, ChipKind.Timer }, chips.Select(c => c.Kind));
    }

    [Fact]
    public void Ask_ComesRightAfterCalls()
    {
        var chips = ChipBoard.Chips(Snap(new[] { "timer", "calls", "agents" }, asks: 2, timer: Timer, call: Call));

        Assert.Equal(new[] { ChipKind.Call, ChipKind.Ask, ChipKind.Timer }, chips.Select(c => c.Kind));
        Assert.Equal("agents", chips[1].Module);
        Assert.Equal(2, chips[1].AskCount);
    }

    [Fact]
    public void Ask_CarriesTheAskingAgentsItem_ForItsIcon()
    {
        var claude = new WatchItem("claude-1", "Claude", WatchKind.Claude);
        var snap = Snap(new[] { "agents" }, asks: 1) with { AskItem = claude };

        var ask = Assert.Single(ChipBoard.Chips(snap), c => c.Kind == ChipKind.Ask);

        Assert.Same(claude, ask.AskItem);
    }

    [Fact]
    public void Ask_NotShown_WhenAgentsOff()
    {
        var chips = ChipBoard.Chips(Snap(new[] { "timer" }, asks: 2, timer: Timer));

        Assert.Equal(new[] { ChipKind.Timer }, chips.Select(c => c.Kind));
    }

    [Fact]
    public void Sensor_ShownForMic_WhenNoCall()
    {
        var chips = ChipBoard.Chips(Snap(new[] { "privacy" }, sensors: Mic));

        Assert.Equal(new[] { ChipKind.Sensor }, chips.Select(c => c.Kind));
        Assert.Equal("privacy", chips[0].Module);
    }

    [Fact]
    public void Sensor_ShownForCamera_WhenNoCall()
    {
        var chips = ChipBoard.Chips(Snap(new[] { "privacy" }, sensors: Cam));

        Assert.Equal(new[] { ChipKind.Sensor }, chips.Select(c => c.Kind));
    }

    [Fact]
    public void Sensor_HiddenDuringACall()
    {
        var chips = ChipBoard.Chips(Snap(new[] { "calls", "privacy" }, call: Call, sensors: Mic));

        Assert.Equal(new[] { ChipKind.Call }, chips.Select(c => c.Kind));
    }

    [Fact]
    public void Sensor_HiddenWithoutMicOrCamera()
    {
        var chips = ChipBoard.Chips(Snap(new[] { "privacy" }, sensors: Quiet));

        Assert.Empty(chips);
    }

    [Fact]
    public void Inactive_ModulesGiveNoChip()
    {
        var chips = ChipBoard.Chips(Snap(Array.Empty<string>(), asks: 1, timer: Timer, call: Call, sensors: Mic));

        Assert.Empty(chips);
    }

    [Fact]
    public void Sides_SingleAutoChip_IsSingle()
    {
        var chips = ChipBoard.Chips(Snap(new[] { "timer" }, timer: Timer));

        var (left, right, single) = ChipBoard.Sides(chips, P());

        Assert.Empty(left);
        Assert.Empty(right);
        Assert.NotNull(single);
        Assert.Equal(ChipKind.Timer, single!.Kind);
    }

    [Fact]
    public void Sides_SingleChip_PinnedToASide_GoesThere()
    {
        var chips = ChipBoard.Chips(Snap(new[] { "timer" }, timer: Timer));

        var (left, right, single) = ChipBoard.Sides(chips, P(("timer", "left")));

        Assert.Null(single);
        Assert.Equal(new[] { ChipKind.Timer }, left.Select(c => c.Kind));
        Assert.Empty(right);
    }

    [Fact]
    public void Sides_PinnedGoTheirSide_AutoBalances_KeepsOrder()
    {
        // order: call, ask, timer (the call hides the sensor chip). ask pinned left, timer pinned right; the call balances the sides.
        var chips = ChipBoard.Chips(Snap(new[] { "calls", "agents", "timer", "privacy" },
            asks: 1, timer: Timer, call: Call, sensors: Mic));
        Assert.Equal(new[] { ChipKind.Call, ChipKind.Ask, ChipKind.Timer }, chips.Select(c => c.Kind).Take(3));

        var placements = P(("agents", "left"), ("timer", "right"));
        // sensor is hidden by the call, so only three chips: call (auto) goes to the emptier left side (tie -> left)
        var (left, right, single) = ChipBoard.Sides(chips, placements);

        Assert.Null(single);
        Assert.Equal(new[] { ChipKind.Call, ChipKind.Ask }, left.Select(c => c.Kind));
        Assert.Equal(new[] { ChipKind.Timer }, right.Select(c => c.Kind));
    }

    [Fact]
    public void Sides_AutoChipsBalance_WhenNoPins()
    {
        var chips = ChipBoard.Chips(Snap(new[] { "timer", "privacy" }, timer: Timer, sensors: Mic));

        var (left, right, single) = ChipBoard.Sides(chips, P());

        Assert.Null(single);
        Assert.Equal(new[] { ChipKind.Timer }, left.Select(c => c.Kind));
        Assert.Equal(new[] { ChipKind.Sensor }, right.Select(c => c.Kind));
    }

    [Fact]
    public void Calendar_ChipShowsTheSoonEvent_WhenTheModuleIsOn()
    {
        // The Mac's calendar chip: the event the snapshot carries (a timed one soon, or running), in the calendar module's place.
        var soon = new CalendarEvent("a@1", "Standup", T0, T0.AddMinutes(30), "#0A84FF", false, "Work", null, "a");
        var snap = Snap(new[] { "timer", "calendar" }, timer: Timer) with { SoonEvent = soon };

        var chips = ChipBoard.Chips(snap);

        Assert.Equal(new[] { ChipKind.Timer, ChipKind.Calendar }, chips.Select(c => c.Kind));
        Assert.Same(soon, chips[1].Event);
        Assert.Equal("calendar", chips[1].Module);
    }

    [Fact]
    public void Calendar_NoChip_WhenTheModuleIsOff_OrNothingIsSoon()
    {
        var soon = new CalendarEvent("a@1", "Standup", T0, T0.AddMinutes(30), "#0A84FF", false, "Work", null, "a");

        Assert.Empty(ChipBoard.Chips(Snap(new[] { "timer" }) with { SoonEvent = soon }));
        Assert.Empty(ChipBoard.Chips(Snap(new[] { "calendar" })));
    }

    private static WatchItem BuildItem(string id) => new(id, id, WatchKind.Dev);

    private static ItemState BuildRow(string id) => new(BuildItem(id), new[] { new Job(id + "-1", id, "build", true, Symbol: "hammer.fill") });

    private static JobAlert Alert(string id) => new(id, "Build", "repo", "failure", "https://example.test/log");

    [Fact]
    public void ChipBoard_Ci_AlertsThenBuildChips_NoCap()
    {
        var builds = Enumerable.Range(1, 6).Select(i => BuildRow("b" + i)).ToArray();
        var snap = new NotchSnapshot(new[] { "ci" }, 0, null, null, Quiet, null, null)
        {
            Alerts = new[] { Alert("gh-1") },
            BuildRunning = builds,
        };

        var chips = ChipBoard.Chips(snap);

        Assert.Equal(new[] { ChipKind.Alerts, ChipKind.Agent, ChipKind.Agent, ChipKind.Agent, ChipKind.Agent, ChipKind.Agent, ChipKind.Agent }, chips.Select(c => c.Kind));
        Assert.All(chips, c => Assert.Equal("ci", c.Module));
        Assert.Equal("alerts", chips[0].Id);
        Assert.Equal(1, chips[0].AlertCount);
        Assert.Equal(builds.Select(b => b.Item.Id), chips.Skip(1).Select(c => c.Agent!.Item.Id));
    }

    [Fact]
    public void ChipBoard_Ci_NoAlertChip_WithoutAlerts()
    {
        var snap = new NotchSnapshot(new[] { "ci" }, 0, null, null, Quiet, null, null) { BuildRunning = new[] { BuildRow("b1") } };

        Assert.Equal(new[] { ChipKind.Agent }, ChipBoard.Chips(snap).Select(c => c.Kind));
    }

    [Fact]
    public void ChipBoard_CiOff_NoChips()
    {
        var snap = new NotchSnapshot(new[] { "timer" }, 0, null, null, Quiet, null, null)
        {
            Alerts = new[] { Alert("gh-1") },
            BuildRunning = new[] { BuildRow("b1") },
        };

        Assert.Empty(ChipBoard.Chips(snap));
    }

    [Fact]
    public void ChipBoard_Media_OnlyWhilePlaying()
    {
        var playing = new NowPlaying("spotify.exe", "Spotify", "spotify.exe", "Song", "Artist", true, 10, 200, 0,
            true, true, true, true, "key");
        var paused = playing with { Playing = false };

        var on = new NotchSnapshot(new[] { "media" }, 0, null, null, Quiet, null, null) { Media = playing };
        var chips = ChipBoard.Chips(on);
        Assert.Equal(new[] { ChipKind.Media }, chips.Select(c => c.Kind));
        Assert.Equal("media", chips[0].Id);
        Assert.Equal("media", chips[0].Module);
        Assert.Same(playing, chips[0].Media);

        Assert.Empty(ChipBoard.Chips(on with { Media = paused }));
        Assert.Empty(ChipBoard.Chips(on with { Media = null }));
    }
}
