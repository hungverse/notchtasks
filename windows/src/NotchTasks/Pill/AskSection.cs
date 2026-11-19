using System.Windows;
using System.Windows.Controls;
using NotchTasks.Core;
using NotchTasks.Core.Asks;

namespace NotchTasks.Pill;

/// The agents' section of the open pill: one card per waiting ask, oldest first. A card is kept while its ask waits, so what
/// was picked in it is not lost when another ask comes or goes.
public sealed class AskSection
{
    private readonly Dictionary<string, AskCard> _cards = new(StringComparer.Ordinal);

    /// The panel that holds the cards: the section the open pill shows for the agents.
    public StackPanel Panel { get; } = new();

    /// Brings the cards in line with the asks. A reply on a card goes to answer.
    public void Sync(IReadOnlyList<AgentAsk> asks, Action<string, AskReply> answer)
    {
        var waiting = new HashSet<string>(asks.Select(a => a.Id), StringComparer.Ordinal);
        foreach (var id in _cards.Keys.Where(id => !waiting.Contains(id)).ToList())
        {
            _cards.Remove(id);
        }

        Panel.Children.Clear();
        foreach (var ask in asks)
        {
            if (!_cards.TryGetValue(ask.Id, out var card))
            {
                card = new AskCard(ask);
                var id = ask.Id;
                card.Replied += reply => answer(id, reply);
                _cards[ask.Id] = card;
            }
            card.Margin = new Thickness(0, Panel.Children.Count == 0 ? 0 : 8, 0, 0);
            Panel.Children.Add(card);
        }
    }

    /// Each card's "Waiting m:ss" for the time now.
    public void UpdateWaiting(DateTimeOffset now)
    {
        foreach (var card in _cards.Values)
        {
            card.UpdateWaiting(now);
        }
    }
}
