using NotchTasks.Core.Agents;
using NotchTasks.Core.Asks;
using Xunit;

namespace NotchTasks.Core.Tests;

/// The watch item an agent's ask is drawn with (Mac AgentKind.item(in:)): the user's item of that agent, else the agent's preset.
public class AgentItemsTests
{
    private static AgentAsk AskBy(string agent) =>
        new("ask-1", 1, "session", "Bash", "ls", Array.Empty<AskQuestion>(), DateTimeOffset.UnixEpoch, agent);

    [Fact]
    public void ItemOf_NoItemInTheList_IsThePresetOfTheAgent()
    {
        var item = AgentItems.ItemOf(AskBy("codex"), Array.Empty<WatchItem>());

        Assert.NotNull(item);
        Assert.Equal(WatchKind.Codex, item!.Kind);
        Assert.Equal("Codex", item.Name);
    }

    [Fact]
    public void ItemOf_UsersItemOfTheSameKindProcessAndArgs_WinsOverThePreset()
    {
        var mine = new WatchItem("claude-mine", "My Claude", WatchKind.Claude, Process: "claude", Icon: "{}");

        var item = AgentItems.ItemOf(AskBy("claude"), new[] { mine });

        Assert.Equal(mine, item);
    }

    [Fact]
    public void ItemOf_UnknownAgent_IsNull()
    {
        Assert.Null(AgentItems.ItemOf(AskBy("nobody"), Array.Empty<WatchItem>()));
    }
}
