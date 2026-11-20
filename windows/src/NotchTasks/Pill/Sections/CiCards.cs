using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NotchTasks.ControlPanel;
using NotchTasks.Core.Agents;
using NotchTasks.Core.Ci;
using NotchTasks.Core.Limits;

namespace NotchTasks.Pill.Sections;

/// The Build & CI section of the open pill (Mac: the ci section, Electron itemCard for a build): one AlertCard per failure alert, then one
/// ItemCard per working build row (the build row and the build watch items). Both are kept per id, so a publish updates them in place; the
/// panel's children are replaced only when the set or the order differs (as AgentCards does).
public sealed class CiCards
{
    private readonly Dictionary<string, AlertCard> _alerts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ItemCard> _items = new(StringComparer.Ordinal);
    private readonly Action<string> _dismiss;
    private readonly Action<string> _open;

    /// dismiss hides an alert for good (CiModule.Dismiss); open opens a log's web link.
    public CiCards(Action<string> dismiss, Action<string> open)
    {
        _dismiss = dismiss;
        _open = open;
    }

    /// The panel the section is: the alert cards, then the build cards.
    public StackPanel Panel { get; } = new();

    /// Brings the section in line with the alerts and the working build rows, and returns it; null when there is nothing to show.
    public FrameworkElement? Sync(IReadOnlyList<JobAlert> alerts, IReadOnlyList<ItemState> running,
        IReadOnlyDictionary<string, JsonObject> toolIcons, IReadOnlyDictionary<string, string> moduleColors, Color? accent,
        IProcessReader? processes, DateTimeOffset now)
    {
        RemoveGone(_alerts, new HashSet<string>(alerts.Select(a => a.Id), StringComparer.Ordinal));
        RemoveGone(_items, new HashSet<string>(running.Select(s => s.Item.Id), StringComparer.Ordinal));

        var wanted = new List<FrameworkElement>();
        foreach (var alert in alerts)
        {
            if (!_alerts.TryGetValue(alert.Id, out var alertCard))
            {
                alertCard = new AlertCard(_open, _dismiss);
                _alerts[alert.Id] = alertCard;
            }
            alertCard.Update(alert);
            wanted.Add(alertCard);
        }
        foreach (var state in running)
        {
            if (!_items.TryGetValue(state.Item.Id, out var card))
            {
                card = new ItemCard();
                _items[state.Item.Id] = card;
            }
            card.Update(state, Array.Empty<LimitWindow>(), TintOf(state.Item, moduleColors, accent), processes, now, toolIcons);
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

    /// The colour of a build card and of its chip: the ci module's colour when it is set, else the item's own (ItemTint.Of).
    public static Color TintOf(WatchItem item, IReadOnlyDictionary<string, string> moduleColors, Color? accent) =>
        moduleColors.TryGetValue("ci", out var hex) && HexColor.TryNormalize(hex, out var normalized)
            ? HexColor.ToColor(normalized)
            : ItemTint.Of(item, moduleColors, accent);

    /// Drops the cards whose ids are gone.
    private static void RemoveGone<T>(Dictionary<string, T> cards, HashSet<string> ids)
    {
        foreach (var id in cards.Keys.Where(id => !ids.Contains(id)).ToList())
        {
            cards.Remove(id);
        }
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
