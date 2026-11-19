using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NotchTasks.Core;
using NotchTasks.Core.Agents;
using NotchTasks.Core.Limits;

namespace NotchTasks.Pill.Sections;

/// The agents' section of the open pill, after the ask cards (Mac: the agents' ItemCards). One ItemCard per running item that is
/// not a build, kept per item id so a publish updates it in place. The panel's children are replaced only when the set or the order
/// differs (as the pill's SameChildren does), so a press on a card is not lost to a rebuild.
public sealed class AgentCards
{
    private readonly Dictionary<string, ItemCard> _cards = new(StringComparer.Ordinal);

    /// The panel the section is: the head (the asks, when there are any) and then the cards.
    public StackPanel Panel { get; } = new();

    /// Brings the section in line with the head and the running items, and returns it; null when there is nothing to show.
    public FrameworkElement? Sync(FrameworkElement? head, IReadOnlyList<ItemState> running, IReadOnlyList<ProviderLimits> limits,
        IReadOnlyDictionary<string, string> moduleColors, Color? accent, IProcessReader? processes, DateTimeOffset now)
    {
        var items = running.Where(s => !WatchItems.InBuild(s.Item)).ToList();
        var ids = new HashSet<string>(items.Select(s => s.Item.Id), StringComparer.Ordinal);
        foreach (var id in _cards.Keys.Where(id => !ids.Contains(id)).ToList())
        {
            _cards.Remove(id);
        }

        var wanted = new List<FrameworkElement>();
        if (head is not null)
        {
            wanted.Add(head);
        }
        foreach (var state in items)
        {
            if (!_cards.TryGetValue(state.Item.Id, out var card))
            {
                card = new ItemCard();
                _cards[state.Item.Id] = card;
            }
            card.Update(state, ItemCard.LimitsFor(state.Item, limits), ItemTint.Of(state.Item, moduleColors, accent), processes, now);
            wanted.Add(card);
        }

        if (!SameChildren(wanted))
        {
            Panel.Children.Clear();
            foreach (var element in wanted)
            {
                Panel.Children.Add(element);
            }
        }
        for (var i = 0; i < wanted.Count; i++)
        {
            var margin = new Thickness(0, i == 0 ? 0 : 8, 0, 0);
            if (wanted[i].Margin != margin)
            {
                wanted[i].Margin = margin;
            }
        }
        return Panel.Children.Count == 0 ? null : Panel;
    }

    /// True when the panel holds exactly these elements, in this order.
    private bool SameChildren(IReadOnlyList<FrameworkElement> wanted)
    {
        if (Panel.Children.Count != wanted.Count)
        {
            return false;
        }
        for (var i = 0; i < wanted.Count; i++)
        {
            if (!ReferenceEquals(Panel.Children[i], wanted[i]))
            {
                return false;
            }
        }
        return true;
    }
}
