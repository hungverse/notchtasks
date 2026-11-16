using System.Text.Json.Nodes;

namespace NotchTasks.Core.Permissions;

/// Start at login: the Run key of the current user holds the app's exe in quotes (ported from the Electron build).
public static class StartAtLogin
{
    public const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "NotchTasks";

    /// The state.json key that records the first-run default was applied.
    public const string FirstRunKey = "loginItemSet";

    /// True when the Run value names this exe. A value that names another path reads as off, so turning it on rewrites it.
    public static bool IsOn(IRegistry reg, string exe) =>
        string.Equals(reg.GetString(KeyPath, ValueName), Quoted(exe), StringComparison.OrdinalIgnoreCase);

    /// Adds the exe to the Run key, or removes the value.
    public static void Set(IRegistry reg, string exe, bool on)
    {
        if (on)
        {
            reg.SetString(KeyPath, ValueName, Quoted(exe));
        }
        else
        {
            reg.Delete(KeyPath, ValueName);
        }
    }

    /// The first run only: start at login is turned on once, and state.json remembers it (loginItemSet). Later runs leave the
    /// user's choice alone. Throws the file and registry errors; the caller decides what a failed first run means.
    public static void ApplyFirstRun(IRegistry reg, string exe, string stateFile)
    {
        if (StateJson.Get(stateFile, FirstRunKey) is not null)
        {
            return;
        }
        Set(reg, exe, true);
        StateJson.Set(stateFile, FirstRunKey, JsonValue.Create(true));
    }

    private static string Quoted(string exe) => $"\"{exe}\"";
}
