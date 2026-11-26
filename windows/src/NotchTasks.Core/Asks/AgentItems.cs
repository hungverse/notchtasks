using NotchTasks.Core.Agents;

namespace NotchTasks.Core.Asks;

/// The watch item an agent's ask is drawn with (Mac AgentKind.item(in:)). The user's own item of that agent is used when the list
/// has one, so a custom icon shows; else the agent's preset, so an agent that is not in the list still has its icon.
public static class AgentItems
{
    /// The item for an ask's agent, or null when the agent is not one this build knows.
    public static WatchItem? ItemOf(AgentAsk ask, IReadOnlyList<WatchItem> items)
    {
        if (AgentKinds.Parse(ask.Agent) is not { } kind)
        {
            return null;
        }
        var spec = AgentKinds.Spec(kind);
        // the preset is found by its name or its CLI, as the Mac does (the first group is the terminal agents)
        var preset = WatchItems.PresetGroups[0].Items
            .FirstOrDefault(p => p.Name.StartsWith(spec.Name, StringComparison.Ordinal) || p.Cli == spec.Cli);
        if (preset is null)
        {
            return null;
        }
        return items.FirstOrDefault(i => i.Kind == preset.Kind && i.Process == preset.Process && i.Args == preset.Args) ?? preset;
    }
}
