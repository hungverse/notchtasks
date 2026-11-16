using NotchTasks.Core.Asks;
using Xunit;

namespace NotchTasks.Core.Tests;

// Ported from electron/test/asks.test.js ("agent table", the settings paths). Shares the L10n collection.
[Collection("L10n")]
public sealed class AgentKindTests
{
    [Fact]
    public void AgentTable_NamesClisEventsMatchersAndTestedFlags()
    {
        var claude = AgentKinds.Spec(AgentKind.Claude);
        Assert.Equal("Claude", claude.Name);
        Assert.Equal("claude", claude.Cli);
        Assert.Equal("PermissionRequest", claude.Event);
        Assert.Equal("*", claude.Matcher);
        Assert.True(claude.Tested);

        var codex = AgentKinds.Spec(AgentKind.Codex);
        Assert.Equal("Codex", codex.Name);
        Assert.True(codex.Tested);

        var gemini = AgentKinds.Spec(AgentKind.Gemini);
        Assert.Equal("BeforeTool", gemini.Event);
        Assert.Equal("run_shell_command|write_file|replace|web_fetch|ask_user", gemini.Matcher);
        Assert.False(gemini.Tested);

        var antigravity = AgentKinds.Spec(AgentKind.Antigravity);
        Assert.Equal("agy", antigravity.Cli);
        Assert.Equal("PreToolUse", antigravity.Event);
        Assert.Equal("run_command|write_to_file|replace_file_content|multi_replace_file_content|ask_question", antigravity.Matcher);

        var cursor = AgentKinds.Spec(AgentKind.Cursor);
        Assert.Equal("Cursor", cursor.Name);
        Assert.Equal("cursor-agent", cursor.Cli);
        Assert.Equal("beforeShellExecution", cursor.Event);

        var opencode = AgentKinds.Spec(AgentKind.OpenCode);
        Assert.Equal("OpenCode", opencode.Name);
        Assert.Equal("opencode", opencode.Cli);
        Assert.Equal("permission.ask", opencode.Event);
        Assert.True(opencode.Tested);
    }

    [Fact]
    public void Ids_AreTheLowercaseNamesInTableOrder()
    {
        Assert.Equal(
            new[] { "claude", "codex", "gemini", "antigravity", "cursor", "opencode" },
            Enum.GetValues<AgentKind>().Select(k => k.Id()));
    }

    [Fact]
    public void Parse_AcceptsOnlyTheLowercaseIds()
    {
        foreach (var kind in Enum.GetValues<AgentKind>())
        {
            Assert.Equal(kind, AgentKinds.Parse(kind.Id()));
        }
        Assert.Null(AgentKinds.Parse("Claude"));
        Assert.Null(AgentKinds.Parse("nope"));
        Assert.Null(AgentKinds.Parse(null));
    }

    [Fact]
    public void SettingsFile_FollowsTheHomeGiven()
    {
        var home = Path.Combine(Path.GetTempPath(), "nt-home");

        Assert.Equal(Path.Combine(home, ".claude", "settings.json"), AgentKinds.Spec(AgentKind.Claude).SettingsFile(home));
        Assert.Equal(Path.Combine(home, ".codex", "hooks.json"), AgentKinds.Spec(AgentKind.Codex).SettingsFile(home));
        Assert.Equal(Path.Combine(home, ".gemini", "settings.json"), AgentKinds.Spec(AgentKind.Gemini).SettingsFile(home));
        Assert.Equal(Path.Combine(home, ".gemini", "config", "hooks.json"), AgentKinds.Spec(AgentKind.Antigravity).SettingsFile(home));
        Assert.Equal(Path.Combine(home, ".cursor", "hooks.json"), AgentKinds.Spec(AgentKind.Cursor).SettingsFile(home));
        Assert.Equal(
            Path.Combine(home, ".config", "opencode", "plugins", "notchtasks.js"),
            AgentKinds.Spec(AgentKind.OpenCode).SettingsFile(home));
    }

    [Fact]
    public void SettingsFile_ClaudeHonorsClaudeConfigDir()
    {
        var home = Path.Combine(Path.GetTempPath(), "nt-home");
        var dir = Path.Combine(Path.GetTempPath(), "nt-claude-config");
        var old = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        try
        {
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", dir);

            Assert.Equal(Path.Combine(dir, "settings.json"), AgentKinds.Spec(AgentKind.Claude).SettingsFile(home));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", old);
        }
    }
}
