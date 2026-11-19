using System.Globalization;
using NotchTasks.Core;
using NotchTasks.Core.Notch;

namespace NotchTasks.Pill;

/// The open pill's header text, as the Mac's summary (NotchView.swift): "{0} running" and "{0} waiting" joined with " · ",
/// or "Nothing running" when neither is so. The waiting count is the snapshot's AskCount, which is zero while the agents
/// module is off. Running agents come in Phase 3b: Running is the hook for them and reads none until then.
public static class PillSummary
{
    /// The header text for a snapshot.
    public static string For(NotchSnapshot s)
    {
        var parts = new List<string>();
        var running = Running(s);
        if (running > 0)
        {
            parts.Add(L10n.L("{0} running", running.ToString(CultureInfo.InvariantCulture)));
        }
        if (s.AskCount > 0)
        {
            parts.Add(L10n.L("{0} waiting", s.AskCount.ToString(CultureInfo.InvariantCulture)));
        }
        return parts.Count == 0 ? L10n.L("Nothing running") : string.Join(" · ", parts);
    }

    /// The agents working now. Phase 3b reads them from the agent sources; until then none run.
    public static int Running(NotchSnapshot s) => 0;
}
