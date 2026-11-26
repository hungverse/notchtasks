using System.Globalization;
using NotchTasks.Core;
using NotchTasks.Core.Notch;

namespace NotchTasks.Pill;

/// The open pill's header text, as the Mac's summary (NotchView.swift): "{0} running" (working jobs), "{0} waiting" (the idle
/// jobs, total minus working) and "♪ app" (what is playing), joined with " · ", or "Nothing running" when none is so. The
/// Mac counts jobs here, not pending asks, so the header matches it.
public static class PillSummary
{
    /// The header text for a snapshot.
    public static string For(NotchSnapshot s)
    {
        var parts = new List<string>();
        var working = Running(s);
        if (working > 0)
        {
            parts.Add(L10n.L("{0} running", working.ToString(CultureInfo.InvariantCulture)));
        }
        var idle = s.JobCount - working;
        if (idle > 0)
        {
            parts.Add(L10n.L("{0} waiting", idle.ToString(CultureInfo.InvariantCulture)));
        }
        if (s.Media is { Playing: true } media)
        {
            parts.Add("♪ " + media.AppName);  // the Mac's note and app name, not translated
        }
        return parts.Count == 0 ? L10n.L("Nothing running") : string.Join(" · ", parts);
    }

    /// The jobs working now: Running holds only the working jobs, so they are its count.
    public static int Running(NotchSnapshot s) => s.Running.Sum(item => item.Jobs.Count);
}
