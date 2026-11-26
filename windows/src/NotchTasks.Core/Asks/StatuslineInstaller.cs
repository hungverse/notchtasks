using System.Text.Json.Nodes;
using NotchTasks.Core.Agents;

namespace NotchTasks.Core.Asks;

/// Where Claude's status line is: no statusLine in settings.json (Off), our `"<hookExe>" --statusline` (Ours), or someone
/// else's (Other, which NotchTasks never replaces).
public enum StatuslineState
{
    Off,
    Ours,
    Other,
}

/// Turns on and off the NotchTasks status line in Claude Code's settings.json, the way HookInstaller does for the hook: only
/// our entry is added or removed, the rest of the file is kept (sorted, as HookInstaller writes it), the old file is saved as
/// <file>.notchtasks-backup, and a file that cannot be parsed is never written. Opt-in: nothing installs it by itself.
public static class StatuslineInstaller
{
    private const string Key = "statusLine";

    /// The entry's command names NotchTasks and --statusline (the current exe; any exe path).
    public static StatuslineState State(string home)
    {
        if (ReadRoot(home) is not { } root || root[Key] is null)
        {
            return StatuslineState.Off;
        }
        return IsOurs(root) ? StatuslineState.Ours : StatuslineState.Other;
    }

    /// Sets the status line to `"<hookExe>" --statusline`, but only when there is no status line yet: another one is never
    /// overwritten. Returns false, and writes nothing, when the status line is already set (ours or someone else's). Throws
    /// InvalidDataException, leaving the file untouched, when the settings file is not a JSON object.
    public static bool Install(string hookExe, string home)
    {
        ArgumentException.ThrowIfNullOrEmpty(hookExe);
        var file = SettingsFile(home);
        var root = HookInstaller.ReadRootForWrite(file, out var existed);
        if (root[Key] is not null)
        {
            return false;
        }
        if (existed)
        {
            HookInstaller.Backup(file);
        }
        root[Key] = Line(hookExe);
        AtomicFile.WriteAllText(file, HookInstaller.SortedJson(root));
        return true;
    }

    /// Removes the status line when it is ours. Another status line, and a file without one, are left as they are.
    public static void Uninstall(string home)
    {
        var file = SettingsFile(home);
        var root = HookInstaller.ReadRootForWrite(file, out var existed);
        if (!existed || !IsOurs(root))
        {
            return;
        }
        HookInstaller.Backup(file);
        root.Remove(Key);
        AtomicFile.WriteAllText(file, HookInstaller.SortedJson(root));
    }

    /// Removes our status line only when it runs exactly this hookExe (for the uninstaller). Returns true when it was removed.
    public static bool UninstallPointingAt(string hookExe, string home)
    {
        if (State(home) != StatuslineState.Ours || !PointsAt(home, hookExe))
        {
            return false;
        }
        Uninstall(home);
        return true;
    }

    /// Points our status line at this exe when its exe no longer exists (the app moved). A status line that still names an
    /// existing exe is another copy's and is left alone, as is another status line. Returns true when it was rewritten.
    public static bool Repoint(string hookExe, string home)
    {
        ArgumentException.ThrowIfNullOrEmpty(hookExe);
        var file = SettingsFile(home);
        var root = HookInstaller.ReadRootForWrite(file, out var existed);
        if (!existed || !IsOurs(root))
        {
            return false;
        }
        var current = ExeOf(root);
        if (current is null || string.Equals(current, hookExe, StringComparison.OrdinalIgnoreCase) || File.Exists(current))
        {
            return false;
        }
        HookInstaller.Backup(file);
        root[Key] = Line(hookExe);
        AtomicFile.WriteAllText(file, HookInstaller.SortedJson(root));
        return true;
    }

    /// The statusLine entry for `"<hookExe>" --statusline`, padding 0 (Claude Code's own settings shape).
    public static JsonObject Line(string hookExe) => new()
    {
        ["type"] = "command",
        ["command"] = $"\"{hookExe}\" --statusline",
        ["padding"] = 0,
    };

    private static bool IsOurs(JsonObject root)
    {
        var command = AsksJson.Str((root[Key] as JsonObject)?["command"]);
        return command is not null && command.Contains("NotchTasks", StringComparison.Ordinal)
            && command.Contains("--statusline", StringComparison.Ordinal);
    }

    private static bool PointsAt(string home, string hookExe) =>
        ReadRoot(home) is { } root && ExeOf(root) is { } exe && string.Equals(exe, hookExe, StringComparison.OrdinalIgnoreCase);

    /// The exe a status line command runs: the quoted path, or the first word.
    private static string? ExeOf(JsonObject root)
    {
        var command = AsksJson.Str((root[Key] as JsonObject)?["command"]);
        if (string.IsNullOrEmpty(command))
        {
            return null;
        }
        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            return end > 1 ? command[1..end] : null;
        }
        var space = command.IndexOf(' ');
        return space > 0 ? command[..space] : command;
    }

    /// The settings object, or null when the file is missing, empty, or cannot be read or parsed (State then reads as Off).
    private static JsonObject? ReadRoot(string home)
    {
        try
        {
            return HookInstaller.ReadRootForWrite(SettingsFile(home), out _);
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string SettingsFile(string home) => AgentKinds.Spec(AgentKind.Claude).SettingsFile(home);
}
