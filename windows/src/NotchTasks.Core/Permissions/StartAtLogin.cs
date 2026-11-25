using System.IO;
using System.Text.Json.Nodes;

namespace NotchTasks.Core.Permissions;

/// Start at login: the Run key of the current user holds the app's exe in quotes (ported from the Electron build).
public static class StartAtLogin
{
    public const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "NotchTasks";

    /// The Run value the Electron build wrote (its app.setLoginItemSettings, under the app's AUMID).
    public const string ElectronValueName = "com.notchtasks.windows";

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

    /// Moves the Electron build's Run value to ours. The value is always deleted. When it named a NotchTasks.exe that exists (the
    /// Electron install folder can be anywhere), our value is set to this exe, so the user's on-state carries over and the
    /// Permissions switch reads it. Nothing else in the Run key is touched.
    public static void MigrateElectron(IRegistry reg, string exe, Func<string, bool>? fileExists = null)
    {
        var legacy = reg.GetString(KeyPath, ElectronValueName);
        if (legacy is null)
        {
            return;
        }
        reg.Delete(KeyPath, ElectronValueName);
        if (NamesExistingNotchTasksExe(legacy, fileExists ?? File.Exists))
        {
            Set(reg, exe, true);
        }
    }

    /// True when the Run value's command is a quoted or bare path to a file named NotchTasks.exe that exists.
    private static bool NamesExistingNotchTasksExe(string command, Func<string, bool> fileExists)
    {
        var text = command.Trim();
        if (text.StartsWith('"'))
        {
            var close = text.IndexOf('"', 1);
            text = close > 0 ? text[1..close] : string.Empty;
        }
        return text.Length > 0 && Path.GetFileName(text).Equals("NotchTasks.exe", StringComparison.OrdinalIgnoreCase)
            && fileExists(text);
    }

    private static string Quoted(string exe) => $"\"{exe}\"";
}
