using System.Text.Json.Nodes;

namespace NotchTasks.Core.Asks;

/// The first-run setup of each coding agent: is its CLI here, what is left before the notch can answer it, and the script
/// that runs the agent's own command (ported from electron/src/core/asks.js setupStep, codexTrusted, geminiSignedIn, present
/// and openInTerminal). Nothing here reads a credential: only the presence of settings keys.
public static class AgentSetup
{
    /// What is missing (Missing), the button that fixes it (Button) and the command to run in a terminal (Command).
    public sealed record Step(string Missing, string Button, string Command);

    private static readonly string[] Extensions = [".exe", ".cmd", ".bat", ".ps1"];

    /// True when the agent's CLI is on one of the usual paths, or for Codex the desktop app is installed. Claude is always present:
    /// the desktop app reads ~/.claude too. `path` is the PATH to search; null means this process's PATH. `appData` and
    /// `localAppData` are the folders searched for the npm CLI and the desktop app; null means the real ones.
    public static bool Present(AgentKind kind, string home, string? path = null, string? appData = null, string? localAppData = null) =>
        kind == AgentKind.Claude
        || FindCli(AgentKinds.Spec(kind).Cli, home, path, appData) is not null
        || (kind == AgentKind.Codex && CodexDesktopInstalled(home, localAppData));

    /// True when the Codex desktop app is installed: it writes ~/.codex/.desktop-created, and its package's execution alias folder
    /// is in %LOCALAPPDATA%\Microsoft\WindowsApps as OpenAI.Codex_*. `localAppData` null means this user's own folder.
    public static bool CodexDesktopInstalled(string home, string? localAppData = null)
    {
        if (File.Exists(Path.Combine(home, ".codex", ".desktop-created")))
        {
            return true;
        }
        localAppData ??= Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (string.IsNullOrEmpty(localAppData))
        {
            return false;
        }
        try
        {
            return Directory.EnumerateDirectories(Path.Combine(localAppData, "Microsoft", "WindowsApps"), "OpenAI.Codex_*").Any();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// The full path of a CLI found on PATH or in one of the usual folders, or null. The folders are the npm global ones and
    /// ~/.local/bin, ~/.npm-global/bin and ~/.opencode/bin. `appData` is the %APPDATA% folder to search (null: the real one);
    /// tests pass a temp folder so they never see the machine's npm folder.
    public static string? FindCli(string name, string home, string? path = null, string? appData = null)
    {
        var dirs = (path ?? Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).ToList();
        appData ??= Environment.GetEnvironmentVariable("APPDATA");
        if (!string.IsNullOrEmpty(appData))
        {
            dirs.Add(Path.Combine(appData, "npm"));
        }
        dirs.Add(Path.Combine(home, ".local", "bin"));
        dirs.Add(Path.Combine(home, ".npm-global", "bin"));
        dirs.Add(Path.Combine(home, ".opencode", "bin"));

        foreach (var dir in dirs)
        {
            foreach (var extension in Extensions)
            {
                var file = Path.Combine(dir, name + extension);
                if (File.Exists(file))
                {
                    return file;
                }
            }
        }
        return null;
    }

    /// True when Codex has recorded trust for our hook: a [hooks.state."...:permission_request..."] table in config.toml.
    /// Matched as text, since Codex keeps the key's shape in its own file.
    public static bool CodexTrusted(string home)
    {
        var toml = ReadTextOrNull(Path.Combine(home, ".codex", "config.toml")) ?? "";
        return toml.Split('\n').Any(line =>
        {
            var text = line.TrimEnd('\r');
            return text.StartsWith("[hooks.state.", StringComparison.Ordinal)
                && text.Contains("hooks.json:permission_request", StringComparison.Ordinal);
        });
    }

    /// Signed in = an API key in the environment, or an auth type picked in Gemini's settings. Credentials are never read.
    public static bool GeminiSignedIn(string home, bool hasApiKey)
    {
        if (hasApiKey)
        {
            return true;
        }
        return (ReadTextOrNull(Path.Combine(home, ".gemini", "settings.json")) ?? "").Contains("selectedType", StringComparison.Ordinal);
    }

    /// The step still left before the notch can answer this agent, or null when nothing is left. Codex needs its hook on and
    /// trusted; Gemini needs its CLI and a sign-in.
    public static Step? SetupStep(AgentKind kind, string home, bool geminiKeySet, string? path = null)
    {
        if (kind == AgentKind.Codex && HookInstaller.IsInstalled(kind, home) && !CodexTrusted(home))
        {
            return new Step(L10n.L("Not trusted in Codex yet"), L10n.L("Open Codex"), "codex");
        }
        if (kind == AgentKind.Gemini && Present(kind, home, path) && !GeminiSignedIn(home, geminiKeySet))
        {
            return new Step(L10n.L("Not signed in"), L10n.L("Sign in"), "gemini");
        }
        return null;
    }

    /// Writes a small script that runs `command` from the user's profile folder, for a terminal to open. Returns its path.
    /// The command is always one of our constants (codex or gemini).
    public static string WriteTerminalScript(string command, string? dir = null)
    {
        var folder = dir ?? AppPaths.DataDir;
        var file = Path.Combine(folder, $"{command}-setup.cmd");
        AtomicFile.WriteAllText(file, $"@echo off\r\ncd /d \"%USERPROFILE%\"\r\n{command}\r\n");
        return file;
    }

    /// The file's text, or null when it is missing or cannot be read.
    private static string? ReadTextOrNull(string file)
    {
        try
        {
            return AtomicFile.ReadAllTextOrNull(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
