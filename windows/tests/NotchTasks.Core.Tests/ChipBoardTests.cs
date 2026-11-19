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
        // order: call, ask, timer, sensor. ask pinned left, timer pinned right; call and sensor balance.
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
}
