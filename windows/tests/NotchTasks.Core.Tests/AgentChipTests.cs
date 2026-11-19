using NotchTasks.Core.Agents;
using NotchTasks.Core.Notch;
using Xunit;

namespace NotchTasks.Core.Tests;

public class AgentChipTests
{
    private static readonly SensorState Quiet = new(Array.Empty<string>(), false, false);

    private static WatchItem Item(string id) => new(id, id, WatchKind.Process, Process: id);

    private static ItemState Working(string id) => new(Item(id), new[] { new Job(id + "-1", id, "Running", true) });

    [Fact]
    public void Agents_OneChipPerWorkingItem_InSnapshotOrder()
    {
        var running = new[] { Working("a"), Working("b") };
        var snap = new NotchSnapshot(new[] { "agents" }, 0, null, null, Quiet, null, null) { Running = running };

        var chips = ChipBoard.Chips(snap);

        Assert.Equal(new[] { ChipKind.Agent, ChipKind.Agent }, chips.Select(c => c.Kind));
        Assert.Equal(new[] { "a", "b" }, chips.Select(c => c.Agent!.Item.Id));
    }

    [Fact]
    public void Agents_AtMostFourChips()
    {
        var running = new[] { Working("a"), Working("b"), Working("c"), Working("d"), Working("e") };
        var snap = new NotchSnapshot(new[] { "agents" }, 0, null, null, Quiet, null, null) { Running = running };

        Assert.Equal(4, ChipBoard.Chips(snap).Count);
    }

    [Fact]
    public void Agents_NoChipsWhileTheModuleIsOff()
    {
        var snap = new NotchSnapshot(new[] { "timer" }, 0, null, null, Quiet, null, null) { Running = new[] { Working("a") } };

        Assert.Empty(ChipBoard.Chips(snap));
    }
}
