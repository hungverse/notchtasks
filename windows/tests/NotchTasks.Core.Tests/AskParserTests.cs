using System.Globalization;
using System.Text.Json.Nodes;
using NotchTasks.Core.Asks;
using Xunit;

namespace NotchTasks.Core.Tests;

// Ported from electron/test/asks.test.js: actionLabel and the payload parsing (askFrom) for each agent.
// The session name comes in as a parameter here; deriving it from the payload is Task 4 (runHook).
[Collection("L10n")]
public sealed class AskParserTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 9, 12, 7, TimeSpan.Zero);

    public AskParserTests()
    {
        AsksFixtures.InitEnglish();
    }

    private static AgentAsk Parse(JsonObject input, AgentKind kind) =>
        AskParser.From(input, kind, "id-1", 42, "notchtasks", Now);

    [Theory]
    [InlineData("Bash", "Run command")]
    [InlineData("shell", "Run command")]
    [InlineData("run_command", "Run command")]
    [InlineData("write_file", "Edit file")]
    [InlineData("multi_replace_file_content", "Edit file")]
    [InlineData("view_file", "Read file")]
    [InlineData("WebFetch", "Web access")]
    [InlineData("mcp__github__create_issue", "Use tool issue")]
    [InlineData("AskUserQuestion", "AskUserQuestion")]
    public void ActionLabel_WordsForToolsAndMcpByLastPart(string tool, string expected)
    {
        Assert.Equal(expected, AskParser.ActionLabel(tool));
    }

    [Fact]
    public void ClaudeBash_ToolAndSummaryFromTheCommand()
    {
        var ask = Parse(AsksFixtures.Fixture("claude-bash"), AgentKind.Claude);

        Assert.Equal("id-1", ask.Id);
        Assert.Equal(42, ask.Pid);
        Assert.Equal("notchtasks", ask.Session);
        Assert.Equal("Bash", ask.Tool);
        Assert.Equal("rm -rf dist", ask.Summary);
        Assert.Empty(ask.Questions);
        Assert.False(ask.IsQuestion);
        Assert.Equal("claude", ask.Agent);
        Assert.Equal(Now, ask.At);
        Assert.Equal("Run command", AskParser.ActionLabel(ask.Tool));
    }

    [Fact]
    public void ClaudeAskUserQuestion_TwoQuestionsWithLabelsAndMultiSelect()
    {
        var ask = Parse(AsksFixtures.Fixture("claude-askuserquestion"), AgentKind.Claude);

        Assert.Equal("AskUserQuestion", ask.Tool);
        Assert.Equal("Which parts should the release notes cover?", ask.Summary);
        Assert.True(ask.IsQuestion);
        Assert.Equal(2, ask.Questions.Count);
        Assert.Equal(new[] { true, false }, ask.Questions.Select(q => q.MultiSelect));
        Assert.Equal(new[] { "Features", "Fixes", "Known issues" }, ask.Questions[0].Options);
        Assert.Equal("Channel", ask.Questions[1].Header);
        Assert.Equal("Scope", ask.Questions[0].Header);
    }

    [Fact]
    public void CodexShell_ArgvArrayIsJoined()
    {
        var ask = Parse(AsksFixtures.Fixture("codex-shell"), AgentKind.Codex);

        Assert.Equal("shell", ask.Tool);
        Assert.Equal("bash -lc npm test", ask.Summary);
        Assert.Equal("codex", ask.Agent);
    }

    [Fact]
    public void GeminiShell_RunShellCommand()
    {
        var ask = Parse(AsksFixtures.Fixture("gemini-shell"), AgentKind.Gemini);

        Assert.Equal("run_shell_command", ask.Tool);
        Assert.Equal("npm run build", ask.Summary);
        Assert.Equal("gemini", ask.Agent);
    }

    [Fact]
    public void Antigravity_CamelCaseToolCallWithCommandLineInArgs()
    {
        var ask = Parse(AsksFixtures.Fixture("antigravity-run"), AgentKind.Antigravity);

        Assert.Equal("run_command", ask.Tool);
        Assert.Equal("npm test", ask.Summary);
        Assert.Equal("antigravity", ask.Agent);
    }

    [Fact]
    public void Cursor_CommandSitsAtTheTopLevel()
    {
        var ask = Parse(AsksFixtures.Fixture("cursor-shell"), AgentKind.Cursor);

        Assert.Equal("shell", ask.Tool);
        Assert.Equal("git push origin main", ask.Summary);
        Assert.Equal("cursor", ask.Agent);
    }

    [Fact]
    public void OpenCode_CommandFromMetadata_ActionIsTheTool()
    {
        var ask = Parse(AsksFixtures.Fixture("opencode-permission"), AgentKind.OpenCode);

        Assert.Equal("bash", ask.Tool);
        Assert.Equal("git push origin main", ask.Summary);
        Assert.Equal("opencode", ask.Agent);
    }

    [Fact]
    public void OpenCode_WithoutMetadataCommand_SummaryIsTheResources()
    {
        var input = AsksFixtures.Json("{\"action\":\"bash\",\"resources\":[\"git\",\"push\"],\"metadata\":{}}");

        var ask = Parse(input, AgentKind.OpenCode);

        Assert.Equal("git push", ask.Summary);
    }

    [Fact]
    public void Summary_FallsBackToCompactJsonOfToolInput()
    {
        var ask = Parse(AsksFixtures.Json("{\"tool_name\":\"Custom\",\"tool_input\":{\"b\":1,\"a\":\"x\"}}"), AgentKind.Claude);

        Assert.Equal("{\"b\":1,\"a\":\"x\"}", ask.Summary);
    }

    [Fact]
    public void Summary_TruncatesAt600TextElements()
    {
        var input = new JsonObject
        {
            ["tool_name"] = "Bash",
            ["tool_input"] = new JsonObject { ["command"] = new string('a', 700) },
        };

        var ask = Parse(input, AgentKind.Claude);

        Assert.Equal(600, ask.Summary.Length);
    }

    [Fact]
    public void Summary_CountsACombiningSequenceAsOneCharacter()
    {
        var command = string.Concat(Enumerable.Repeat("é", 700));
        var input = new JsonObject
        {
            ["tool_name"] = "Bash",
            ["tool_input"] = new JsonObject { ["command"] = command },
        };

        var ask = Parse(input, AgentKind.Claude);

        Assert.Equal(600, new StringInfo(ask.Summary).LengthInTextElements);
        Assert.Equal(1200, ask.Summary.Length);
    }

    [Fact]
    public void Options_PlainStringsAndIsMultiSelectSpelling()
    {
        var input = AsksFixtures.Json("{\"tool_name\":\"ask_question\",\"tool_input\":{\"questions\":[" +
            "{\"question\":\"Pick\",\"options\":[\"A\",\"B\"],\"is_multi_select\":true}]}}");

        var ask = Parse(input, AgentKind.Antigravity);

        Assert.True(ask.IsQuestion);
        Assert.Equal("Pick", ask.Summary);
        Assert.Equal(new[] { "A", "B" }, ask.Questions[0].Options);
        Assert.True(ask.Questions[0].MultiSelect);
        Assert.Null(ask.Questions[0].Header);
    }
}
