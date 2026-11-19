using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NotchTasks.ControlPanel;
using NotchTasks.Core;
using NotchTasks.Core.Agents;
using NotchTasks.Core.Limits;

namespace NotchTasks.Pill.Sections;

/// The agents' section of the open pill, after the ask cards (Mac: the agents' ItemCards). One ItemCard per running item that is
/// not a build, kept per item id so a publish updates its rows in place and a press on one of them is not lost.
public sealed class AgentCards
{
    private static readonly Color DefaultTint = Color.FromRgb(0x32, 0xD2, 0xFF);  // the chips' default for agents
    private readonly Dictionary<string, ItemCard> _cards = new(StringComparer.Ordinal);

    /// The panel the section is: the head (the asks, when there are any) and then the cards.
    public StackPanel Panel { get; } = new();

    /// The card tint: the agents module's colour, else the chosen accent (null for "auto"), else the default blue.
    public static Color TintOf(IReadOnlyDictionary<string, string> moduleColors, Color? accent) =>
        moduleColors.TryGetValue("agents", out var hex) && HexColor.TryNormalize(hex, out var normalized)
            ? HexColor.ToColor(normalized)
            : accent ?? DefaultTint;

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

        var tint = TintOf(moduleColors, accent);
        Panel.Children.Clear();
        if (head is not null)
        {
            Panel.Children.Add(head);
        }
        foreach (var state in items)
        {
            if (!_cards.TryGetValue(state.Item.Id, out var card))
            {
                card = new ItemCard();
                _cards[state.Item.Id] = card;
            }
            card.Update(state, ItemCard.LimitsFor(state.Item, limits), tint, processes, now);
            Panel.Children.Add(card);
        }
        for (var i = 0; i < Panel.Children.Count; i++)
        {
            ((FrameworkElement)Panel.Children[i]).Margin = new Thickness(0, i == 0 ? 0 : 8, 0, 0);
        }
        return Panel.Children.Count == 0 ? null : Panel;
    }
}
