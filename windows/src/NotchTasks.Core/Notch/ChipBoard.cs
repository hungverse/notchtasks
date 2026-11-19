using NotchTasks.Core.Agents;
using NotchTasks.Core.Limits;

namespace NotchTasks.Core.Notch;

/// Builds the closed pill's chips and splits them into the left and right wings, as the Mac's NotchView does.
public static class ChipBoard
{
    /// The most agent chips the closed pill shows at once.
    public const int MaxAgentChips = 4;

    /// The chips for a snapshot. A live call is always first; an agent waiting on you comes right after the calls.
    public static IReadOnlyList<Chip> Chips(NotchSnapshot s)
    {
        var order = s.ActiveModules.Where(m => m == "calls").Concat(s.ActiveModules.Where(m => m != "calls"));
        var list = new List<Chip>();
        foreach (var m in order)
        {
            switch (m)
            {
                case "calls":
                    if (s.Call is { } call) list.Add(new Chip(ChipKind.Call, "calls", "call") { Call = call });
                    break;
                case "timer":
                    if (s.Timer is { } timer) list.Add(new Chip(ChipKind.Timer, "timer", "timer") { Timer = timer });
                    break;
                case "privacy":
                    // the sensor chip only shows when no call is on: the call chip already says the mic is in use
                    if (s.Call is null && (s.Sensors.Mic || s.Sensors.Camera))
                        list.Add(new Chip(ChipKind.Sensor, "privacy", "sensor") { Sensors = s.Sensors });
                    break;
                case "agents":
                    // one chip per working agent, in the watch list's order, at most four
                    foreach (var state in s.Running.Take(MaxAgentChips))
                        list.Add(new Chip(ChipKind.Agent, "agents", "agent-" + state.Item.Id) { Agent = state });
                    break;
                case "limits":
                    // per provider, the window fullest among those at or above the warning level (NotchView .limits)
                    foreach (var p in s.Limits)
                    {
                        var top = p.Windows.Where(w => Limit.Step(w.Used) >= s.LimitWarn).MaxBy(w => w.Used);
                        if (top is not null)
                            list.Add(new Chip(ChipKind.Limit, "limits", "limit-" + p.Name) { Limit = new LimitChip(p.Name, top) });
                    }
                    break;
            }
        }

        // an agent waiting on you comes right after a call: it is stuck until you answer
        if (s.AskCount > 0 && s.ActiveModules.Contains("agents"))
        {
            var calls = 0;
            while (calls < list.Count && list[calls].Kind == ChipKind.Call) calls++;
            list.Insert(calls, new Chip(ChipKind.Ask, "agents", "ask") { AskCount = s.AskCount });
        }
        return list;
    }

    /// Splits chips into the wings. Chips pinned "left" or "right" go there; "auto" ones (and any module with no
    /// placement) go to whichever side has fewer. A lone auto chip is the single one, shown in the middle.
    /// Each side keeps the chips' order.
    public static (IReadOnlyList<Chip> Left, IReadOnlyList<Chip> Right, Chip? Single) Sides(
        IReadOnlyList<Chip> chips, IReadOnlyDictionary<string, string> placements)
    {
        static string Place(IReadOnlyDictionary<string, string> p, Chip c) =>
            p.TryGetValue(c.Module, out var side) && side is "left" or "right" ? side : "auto";

        if (chips.Count == 1 && Place(placements, chips[0]) == "auto")
            return (Array.Empty<Chip>(), Array.Empty<Chip>(), chips[0]);

        var left = new List<(int Index, Chip Chip)>();
        var right = new List<(int Index, Chip Chip)>();
        var autos = new List<(int Index, Chip Chip)>();
        for (var i = 0; i < chips.Count; i++)
        {
            var item = (i, chips[i]);
            switch (Place(placements, chips[i]))
            {
                case "left": left.Add(item); break;
                case "right": right.Add(item); break;
                default: autos.Add(item); break;
            }
        }
        foreach (var item in autos)
        {
            if (left.Count <= right.Count) left.Add(item); else right.Add(item);
        }

        return (
            left.OrderBy(x => x.Index).Select(x => x.Chip).ToList(),
            right.OrderBy(x => x.Index).Select(x => x.Chip).ToList(),
            null);
    }
}
