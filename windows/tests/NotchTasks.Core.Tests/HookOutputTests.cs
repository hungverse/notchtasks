using System.Text.Json.Nodes;
using NotchTasks.Core.Asks;
using Xunit;

namespace NotchTasks.Core.Tests;

// Ported from electron/test/asks.test.js: the exact stdout per agent for allow, deny, answers and pass.
[Collection("L10n")]
public sealed class HookOutputTests
{
    private const string Denied = "The user denied this from the NotchTasks notch.";
    private const string ClaudeAllow = "{\"hookSpecificOutput\":{\"hookEventName\":\"PermissionRequest\",\"decision\":{\"behavior\":\"allow\"}}}";
    private const string ClaudeDeny = "{\"hookSpecificOutput\":{\"hookEventName\":\"PermissionRequest\",\"decision\":{\"behavior\":\"deny\",\"message\":\"" + Denied + "\"}}}";

    private const string AnsweredJson = "The user already answered this on the NotchTasks notch, so the dialog was not shown. "
        + "Their answers: \\\"Proceed?\\\" → \\\"Yes\\\". Continue with these answers; do not ask again.";

    private static readonly Dictionary<string, string> Answers = new() { ["Proceed?"] = "Yes" };

    public HookOutputTests()
    {
        AsksFixtures.InitEnglish();
    }

    private static AskReply Reply(string kind, IReadOnlyDictionary<string, string>? answers = null) => new(kind, answers);

    private static string? For(string kind, string fixture, AgentKind agent, IReadOnlyDictionary<string, string>? answers = null) =>
        HookOutput.For(Reply(kind, answers), AsksFixtures.Fixture(fixture), agent);

    // ---- Claude

    [Fact]
    public void Claude_AllowAndDenyPrintTheDecision()
    {
        Assert.Equal(ClaudeAllow, For("allow", "claude-bash", AgentKind.Claude));
        Assert.Equal(ClaudeDeny, For("deny", "claude-bash", AgentKind.Claude));
    }

    [Fact]
    public void Claude_AnswersGoBackInUpdatedInput()
    {
        var expected = "{\"hookSpecificOutput\":{\"hookEventName\":\"PermissionRequest\",\"decision\":{\"behavior\":\"allow\","
            + "\"updatedInput\":{\"command\":\"rm -rf dist\",\"description\":\"Remove the build output\",\"answers\":{\"Proceed?\":\"Yes\"}}}}}";

        Assert.Equal(expected, For("answers", "claude-bash", AgentKind.Claude, Answers));
    }

    [Fact]
    public void Claude_PassPrintsNothing()
    {
        Assert.Null(For("pass", "claude-bash", AgentKind.Claude));
        Assert.Null(HookOutput.For(null, AsksFixtures.Fixture("claude-bash"), AgentKind.Claude));
    }

    [Fact]
    public void Claude_AnswersWithoutAnswersLeavesTheAnswersKeyOut()
    {
        var json = JsonNode.Parse(For("answers", "claude-bash", AgentKind.Claude)!)!;

        Assert.Null(json["hookSpecificOutput"]!["decision"]!["updatedInput"]!["answers"]);
    }

    [Fact]
    public void Claude_AskUserQuestionAnswersGoBackWithTheQuestions()
    {
        var answers = new Dictionary<string, string>
        {
            ["Which parts should the release notes cover?"] = "Features, Fixes",
            ["Which channel gets the build?"] = "Stable",
        };

        var json = JsonNode.Parse(For("answers", "claude-askuserquestion", AgentKind.Claude, answers)!)!;
        var updated = json["hookSpecificOutput"]!["decision"]!["updatedInput"]!;

        Assert.Equal("Stable", updated["answers"]!["Which channel gets the build?"]!.GetValue<string>());
        Assert.Equal("Features, Fixes", updated["answers"]!["Which parts should the release notes cover?"]!.GetValue<string>());
        Assert.NotNull(updated["questions"]);
    }

    // ---- Codex

    [Fact]
    public void Codex_AllowDenyUseTheSameShapeAsClaude()
    {
        Assert.Equal(ClaudeAllow, For("allow", "codex-shell", AgentKind.Codex));
        Assert.Equal(ClaudeDeny, For("deny", "codex-shell", AgentKind.Codex));
        Assert.Null(For("pass", "codex-shell", AgentKind.Codex));
    }

    [Fact]
    public void Codex_AnswersKeepTheArgvAndWorkdir()
    {
        var expected = @"{""hookSpecificOutput"":{""hookEventName"":""PermissionRequest"",""decision"":{""behavior"":""allow"",""updatedInput"":{""command"":[""bash"",""-lc"",""npm test""],""workdir"":""C:\\Users\\hung\\Projects\\notchtasks"",""answers"":{""Proceed?"":""Yes""}}}}}";

        Assert.Equal(expected, For("answers", "codex-shell", AgentKind.Codex, Answers));
    }

    // ---- Gemini

    [Fact]
    public void Gemini_AllowDenyAnswersPass()
    {
        Assert.Equal("{\"decision\":\"allow\"}", For("allow", "gemini-shell", AgentKind.Gemini));
        Assert.Equal("{\"decision\":\"deny\",\"reason\":\"" + Denied + "\"}", For("deny", "gemini-shell", AgentKind.Gemini));
        Assert.Equal(
            "{\"decision\":\"deny\",\"reason\":\"" + AnsweredJson + "\"}",
            For("answers", "gemini-shell", AgentKind.Gemini, Answers));
        Assert.Null(For("pass", "gemini-shell", AgentKind.Gemini));
    }

    [Fact]
    public void Gemini_NoReplyPrintsNothing()
    {
        Assert.Null(HookOutput.For(null, AsksFixtures.Fixture("gemini-shell"), AgentKind.Gemini));
    }

    [Fact]
    public void Answered_ListsTheAnswersSortedByTheirFormattedText()
    {
        var answers = new Dictionary<string, string> { ["b"] = "2", ["a"] = "1" };

        var text = For("answers", "gemini-shell", AgentKind.Gemini, answers)!;

        Assert.Contains("Their answers: \\\"a\\\" → \\\"1\\\"; \\\"b\\\" → \\\"2\\\". Continue", text);
    }

    // ---- Antigravity

    [Fact]
    public void Antigravity_AllowGrantsThisOneCommand()
    {
        Assert.Equal(
            "{\"decision\":\"allow\",\"permissionOverrides\":[\"command(npm test)\"]}",
            For("allow", "antigravity-run", AgentKind.Antigravity));
    }

    [Fact]
    public void Antigravity_AllowGrantsThisOneFileWrite()
    {
        var input = AsksFixtures.Json(@"{""toolCall"":{""name"":""write_to_file"",""args"":{""TargetFile"":""C:\\x\\a.txt""}}}");

        Assert.Equal(
            @"{""decision"":""allow"",""permissionOverrides"":[""write_file(C:\\x\\a.txt)""]}",
            HookOutput.For(Reply("allow"), input, AgentKind.Antigravity));
    }

    [Fact]
    public void Antigravity_AllowWithoutArgsIsPlainAllow()
    {
        Assert.Equal("{\"decision\":\"allow\"}", HookOutput.For(Reply("allow"), AsksFixtures.Json("{}"), AgentKind.Antigravity));
    }

    [Fact]
    public void Antigravity_DenyAnswersAndPass()
    {
        Assert.Equal("{\"decision\":\"deny\",\"reason\":\"" + Denied + "\"}", For("deny", "antigravity-run", AgentKind.Antigravity));
        Assert.Equal(
            "{\"decision\":\"deny\",\"reason\":\"" + AnsweredJson + "\"}",
            For("answers", "antigravity-run", AgentKind.Antigravity, Answers));
        Assert.Equal("{\"decision\":\"ask\"}", For("pass", "antigravity-run", AgentKind.Antigravity));
    }

    // ---- Cursor

    [Fact]
    public void Cursor_AllowDenyAskWithTheNoticeMessage()
    {
        Assert.Equal("{\"permission\":\"allow\"}", For("allow", "cursor-shell", AgentKind.Cursor));
        Assert.Equal(
            "{\"permission\":\"deny\",\"user_message\":\"Denied on the notch\",\"agent_message\":\"" + Denied + "\"}",
            For("deny", "cursor-shell", AgentKind.Cursor));
        Assert.Equal("{\"permission\":\"allow\"}", For("answers", "cursor-shell", AgentKind.Cursor, Answers));
        Assert.Equal("{\"permission\":\"ask\"}", For("pass", "cursor-shell", AgentKind.Cursor));
    }

    // ---- OpenCode

    [Fact]
    public void OpenCode_StatusAllowDenyAsk()
    {
        Assert.Equal("{\"status\":\"allow\"}", For("allow", "opencode-permission", AgentKind.OpenCode));
        Assert.Equal("{\"status\":\"deny\"}", For("deny", "opencode-permission", AgentKind.OpenCode));
        Assert.Equal("{\"status\":\"allow\"}", For("answers", "opencode-permission", AgentKind.OpenCode, Answers));
        Assert.Equal("{\"status\":\"ask\"}", For("pass", "opencode-permission", AgentKind.OpenCode));
    }

    [Fact]
    public void NoReply_IsPassForEveryAgent()
    {
        var input = AsksFixtures.Fixture("claude-bash");

        Assert.Null(HookOutput.For(null, input, AgentKind.Claude));
        Assert.Null(HookOutput.For(null, input, AgentKind.Codex));
        Assert.Null(HookOutput.For(null, input, AgentKind.Gemini));
        Assert.Equal("{\"decision\":\"ask\"}", HookOutput.For(null, input, AgentKind.Antigravity));
        Assert.Equal("{\"permission\":\"ask\"}", HookOutput.For(null, input, AgentKind.Cursor));
        Assert.Equal("{\"status\":\"ask\"}", HookOutput.For(null, input, AgentKind.OpenCode));
    }
}
