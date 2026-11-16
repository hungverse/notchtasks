namespace NotchTasks.Core.Asks;

/// The coding agents the pill answers. Their names in files and args are the lowercase ids (AgentKinds.Id).
public enum AgentKind
{
    Claude,
    Codex,
    Gemini,
    Antigravity,
    Cursor,
    OpenCode,
}

/// One row of asks.js AGENTS: display name, CLI, hook event, tool matcher, whether it is tested, and where its settings live.
public sealed record AgentSpec(
    string Name,
    string Cli,
    string Event,
    string Matcher,
    bool Tested,
    Func<string, string> SettingsFile);

public static class AgentKinds
{
    /// The lowercase name used in files and args: "claude", "codex", "gemini", "antigravity", "cursor", "opencode".
    public static string Id(this AgentKind kind) => kind switch
    {
        AgentKind.Claude => "claude",
        AgentKind.Codex => "codex",
        AgentKind.Gemini => "gemini",
        AgentKind.Antigravity => "antigravity",
        AgentKind.Cursor => "cursor",
        AgentKind.OpenCode => "opencode",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// The agent for a lowercase id, or null for anything else.
    public static AgentKind? Parse(string? id) => id switch
    {
        "claude" => AgentKind.Claude,
        "codex" => AgentKind.Codex,
        "gemini" => AgentKind.Gemini,
        "antigravity" => AgentKind.Antigravity,
        "cursor" => AgentKind.Cursor,
        "opencode" => AgentKind.OpenCode,
        _ => null,
    };

    /// The table row for an agent (asks.js AGENTS).
    public static AgentSpec Spec(AgentKind kind) => kind switch
    {
        AgentKind.Claude => new AgentSpec(
            "Claude", "claude", "PermissionRequest", "*", true,
            home => Path.Combine(ClaudeDir(home), "settings.json")),
        AgentKind.Codex => new AgentSpec(
            "Codex", "codex", "PermissionRequest", "*", true,
            home => Path.Combine(home, ".codex", "hooks.json")),
        AgentKind.Gemini => new AgentSpec(
            "Gemini", "gemini", "BeforeTool", "run_shell_command|write_file|replace|web_fetch|ask_user", false,
            home => Path.Combine(home, ".gemini", "settings.json")),
        AgentKind.Antigravity => new AgentSpec(
            "Antigravity", "agy", "PreToolUse",
            "run_command|write_to_file|replace_file_content|multi_replace_file_content|ask_question", true,
            home => Path.Combine(home, ".gemini", "config", "hooks.json")),
        AgentKind.Cursor => new AgentSpec(
            "Cursor", "cursor-agent", "beforeShellExecution", "*", true,
            home => Path.Combine(home, ".cursor", "hooks.json")),
        // OpenCode has no hook config: a small plugin of ours asks the pill
        AgentKind.OpenCode => new AgentSpec(
            "OpenCode", "opencode", "permission.ask", "*", true,
            home => Path.Combine(home, ".config", "opencode", "plugins", "notchtasks.js")),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// CLAUDE_CONFIG_DIR when set, else <home>\.claude (providers.js claudeDir).
    public static string ClaudeDir(string home)
    {
        var dir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        return string.IsNullOrEmpty(dir) ? Path.Combine(home, ".claude") : dir;
    }
}
