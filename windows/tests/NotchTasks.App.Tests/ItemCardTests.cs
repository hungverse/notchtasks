using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NotchTasks.Core;
using NotchTasks.Core.Agents;
using NotchTasks.Core.Limits;
using NotchTasks.Core.Notch;
using NotchTasks.Pill;
using NotchTasks.Pill.Sections;
using Xunit;

namespace NotchTasks.AppTests;

/// The agent cards of the open pill (Mac ItemCard), their limit badges, and the agent and limit chips and wing order.
public sealed class ItemCardTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 10, 0, 0, TimeSpan.Zero);
    private static readonly Color Tint = Color.FromRgb(0x32, 0xD2, 0xFF);
    private static readonly IReadOnlyDictionary<string, string> NoColours = new Dictionary<string, string>();

    /// A watch item with `jobs` working jobs; the first job's id is a pid, as the agents module makes them.
    private static ItemState Agent(string id, int jobs, WatchKind kind = WatchKind.Process)
    {
        var item = new WatchItem(id, "Agent " + id, kind);
        var list = Enumerable.Range(1, jobs)
            .Select(i => new Job((1000 + i).ToString(), "Task " + i, "3%", true))
            .ToList();
        return new ItemState(item, list);
    }

    private static ProviderLimits Provider(string kind, params double[] used) =>
        new(kind, kind, null, used.Select((u, i) => new LimitWindow(i == 0 ? "5h" : "7d", u, Now.AddHours(2))).ToList(), Now);

    /// The elements of a type under root in the logical tree (the card's template is not applied outside a window).
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T match)
            {
                yield return match;
            }
            foreach (var deeper in Descendants<T>(child))
            {
                yield return deeper;
            }
        }
    }

    private static IReadOnlyList<string> Texts(DependencyObject root) =>
        Descendants<TextBlock>(root).Select(t => t.Text).ToList();

    /// A card's state with one dot-led job (no symbol, so the row shows the status dot), working or not.
    private static ItemState DotJob(bool working) =>
        new(new WatchItem("dot", "Dot agent", WatchKind.Process), new List<Job> { new("1001", "Task 1", "3%", working) });

    [Fact]
    public void ItemCard_WorkingDotRow_HasOneRipple()
    {
        Sta.Run(() =>
        {
            var card = new ItemCard();
            card.Update(DotJob(working: true), Array.Empty<LimitWindow>(), Tint, null, Now);
            Assert.Equal(1, card.RowRippleCounts[0]);

            card.Update(DotJob(working: true), Array.Empty<LimitWindow>(), Tint, null, Now);
            card.Update(DotJob(working: true), Array.Empty<LimitWindow>(), Tint, null, Now);
            Assert.Equal(1, card.RowRippleCounts[0]);  // repeated updates do not stack ripples
        });
    }

    [Fact]
    public void ItemCard_IdleDotRow_HasNoRipple()
    {
        Sta.Run(() =>
        {
            var card = new ItemCard();
            card.Update(DotJob(working: false), Array.Empty<LimitWindow>(), Tint, null, Now);
            Assert.Equal(0, card.RowRippleCounts[0]);

            card.Update(DotJob(working: true), Array.Empty<LimitWindow>(), Tint, null, Now);
            card.Update(DotJob(working: false), Array.Empty<LimitWindow>(), Tint, null, Now);
            Assert.Equal(0, card.RowRippleCounts[0]);  // the ripple goes when the job stops
        });
    }

    [Fact]
    public void ItemCard_ShowsFourRows_ThenMore()
    {
        Sta.Run(() =>
        {
            var card = new ItemCard();
            card.Update(Agent("a", 6), Array.Empty<LimitWindow>(), Tint, null, Now);

            Assert.Equal(4, card.ShownRows);
            Assert.Contains("+2 more tasks", Texts(card));
        });
    }

    [Fact]
    public void ItemCard_KeptAcrossPublishes()
    {
        Sta.Run(() =>
        {
            var cards = new AgentCards();
            var running = new[] { Agent("a", 1) };

            var first = cards.Sync(null, running, Array.Empty<ProviderLimits>(), NoColours, null, null, Now);
            var card = Assert.Single(cards.Panel.Children.OfType<ItemCard>());
            var second = cards.Sync(null, running, Array.Empty<ProviderLimits>(), NoColours, null, null, Now);

            Assert.Same(first, second);
            Assert.Same(card, Assert.Single(cards.Panel.Children.OfType<ItemCard>()));
        });
    }

    [Fact]
    public void ItemCard_ClaudeCard_HasClaudeLimitBadges()
    {
        Sta.Run(() =>
        {
            var cards = new AgentCards();
            var providers = new[] { Provider("claude", 40, 95), Provider("codex", 10) };

            cards.Sync(null, new[] { Agent("claude", 1, WatchKind.Claude) }, providers, NoColours, null, null, Now);
            var card = Assert.Single(cards.Panel.Children.OfType<ItemCard>());

            // the Claude card shows the two Claude windows and none of Codex's
            Assert.Equal(2, card.LimitBadgeCount);
        });
    }

    [Fact]
    public void AgentChip_CountInTint_MaxFourChips()
    {
        Sta.Run(() =>
        {
            var chip = new Chip(ChipKind.Agent, "agents", "agent-a") { Agent = Agent("a", 3) };
            var count = Assert.IsType<TextBlock>(ChipView.Trail(chip, 11, null, NoColours, Now));
            Assert.Equal("3", count.Text);
            Assert.Equal(ChipView.TintOf(chip, null, NoColours), Assert.IsType<SolidColorBrush>(count.Foreground).Color);

            var six = new NotchSnapshot(new[] { "agents" }, 0, null, null, Fixture.QuietSensors, null, null)
            {
                Running = Enumerable.Range(1, 6).Select(i => Agent("agent" + i, 1)).ToList(),
            };
            var agentChips = ChipBoard.Chips(six).Where(c => c.Kind == ChipKind.Agent).ToList();
            Assert.Equal(ChipBoard.MaxAgentChips, agentChips.Count);
            Assert.Equal(4, agentChips.Count);
        });
    }

    [Fact]
    public void LimitChip_At100_SaysWaiting()
    {
        Sta.Run(() =>
        {
            var chip = new Chip(ChipKind.Limit, "limits", "limit-Claude")
            {
                Limit = new LimitChip("Claude", new LimitWindow("5h", 100, Now.AddMinutes(90))),
            };
            var trail = Assert.IsType<TextBlock>(ChipView.Trail(chip, 11, null, NoColours, Now));
            Assert.StartsWith("Waiting ", trail.Text);

            var warm = new Chip(ChipKind.Limit, "limits", "limit-Claude")
            {
                Limit = new LimitChip("Claude", new LimitWindow("5h", 85, Now.AddMinutes(90))),
            };
            Assert.Equal("85%", Assert.IsType<TextBlock>(ChipView.Trail(warm, 11, null, NoColours, Now)).Text);
        });
    }

    [Fact]
    public void Wings_BadgesOutermost_WithAgentChips()
    {
        Sta.Run(() =>
        {
            var left = new StackPanel { Orientation = Orientation.Horizontal };
            var right = new StackPanel { Orientation = Orientation.Horizontal };
            var leftBadge = new Border { Tag = "badge" };
            var rightBadge = new Border { Tag = "badge" };
            left.Children.Add(leftBadge);
            right.Children.Add(rightBadge);

            var chips = new[]
            {
                new Chip(ChipKind.Agent, "agents", "agent-a") { Agent = Agent("a", 1) },
                new Chip(ChipKind.Agent, "agents", "agent-b") { Agent = Agent("b", 2) },
            };
            ChipView.Fill(left, right, chips, new Dictionary<string, string>(), NoColours, null, Now, null);

            // a badge is the outermost item of its wing: first on the left, last on the right
            Assert.Same(leftBadge, left.Children[0]);
            Assert.Same(rightBadge, right.Children[^1]);
            Assert.Equal(4, left.Children.Count + right.Children.Count);  // two badges and one panel per chip
        });
    }
}
