using NotchTasks.Core.Asks;
using Xunit;

namespace NotchTasks.Core.Tests;

public class AskTrackerTests
{
    private static AgentAsk Ask(string id) =>
        new(id, 1, "s", "Bash", "summary", Array.Empty<AskQuestion>(), DateTimeOffset.UnixEpoch, "claude");

    [Fact]
    public void Unanswered_asks_pass_through_in_order()
    {
        var tracker = new AskTracker();
        var list = new[] { Ask("a"), Ask("b") };

        var visible = tracker.Filter(list);

        Assert.Equal(new[] { "a", "b" }, visible.Select(a => a.Id));
    }

    [Fact]
    public void Answered_ask_is_hidden_even_when_a_stale_snapshot_still_lists_it()
    {
        var tracker = new AskTracker();
        tracker.MarkAnswered("a");

        var visible = tracker.Filter(new[] { Ask("a"), Ask("b") });

        Assert.Equal(new[] { "b" }, visible.Select(a => a.Id));
        Assert.Equal(1, tracker.Count);
    }

    [Fact]
    public void Answered_id_is_forgotten_once_the_list_no_longer_names_it()
    {
        var tracker = new AskTracker();
        tracker.MarkAnswered("a");
        tracker.Filter(new[] { Ask("a") });

        tracker.Filter(Array.Empty<AgentAsk>());

        Assert.Equal(0, tracker.Count);
    }

    [Fact]
    public void Answered_ask_stays_hidden_while_the_stale_snapshot_is_still_listed_then_clears()
    {
        var tracker = new AskTracker();
        tracker.Filter(new[] { Ask("a") });
        tracker.MarkAnswered("a");

        Assert.Empty(tracker.Filter(new[] { Ask("a") }));
        Assert.Empty(tracker.Filter(Array.Empty<AgentAsk>()));
        Assert.Equal(0, tracker.Count);
    }
}
