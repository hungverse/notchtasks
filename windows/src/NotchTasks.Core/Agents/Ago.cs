namespace NotchTasks.Core.Agents;

/// How long ago, in the app's words (Electron: providers.js ago).
public static class Ago
{
    /// "30 sec", "2 min", "2 hours", with the whole number of the unit (truncated).
    public static string Text(double seconds) =>
        seconds < 60
            ? L10n.L("{0} sec", (long)Math.Truncate(seconds))
            : seconds < 3600
                ? L10n.L("{0} min", (long)Math.Truncate(seconds / 60))
                : L10n.L("{0} hours", (long)Math.Truncate(seconds / 3600));
}
