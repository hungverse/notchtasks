using System.Text;
using System.Text.Json.Nodes;
using NotchTasks.Core.Asks;
using Xunit;

namespace NotchTasks.Core.Tests;

// Ported from electron/test/asks.test.js: approvalMode and selfApproved (codex, antigravity), and pendingCall and
// settled read from a Claude transcript and a Codex rollout. The runHook end-to-end cases only check these functions.
[Collection("L10n")]
public sealed class TranscriptTests : IDisposable
{
    private readonly List<string> _dirs = new();

    public void Dispose()
    {
        foreach (var dir in _dirs)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // A file still open elsewhere; the temp cleaner takes it later.
            }
        }
    }

    private string Temp()
    {
        var dir = AsksFixtures.NewTempDir();
        _dirs.Add(dir);
        return dir;
    }

    private static List<JsonObject> Lines(params string[] json) => json.Select(AsksFixtures.Json).ToList();

    // ---- approvalMode

    [Fact]
    public void ApprovalMode_CodexPermissionModeAutoReviewIsReturned()
    {
        var input = AsksFixtures.Json("{\"permission_mode\":\"auto_review\"}");

        Assert.Equal("auto_review", Transcript.ApprovalMode(AgentKind.Codex, input, Temp()));
    }

    [Fact]
    public void ApprovalMode_CodexDefaultWithoutRolloutIsNull()
    {
        var input = AsksFixtures.Fixture("codex-shell");
        input["transcript_path"] = Path.Combine(Temp(), "no-rollout.jsonl"); // the fixture's own path is a real user file

        Assert.Null(Transcript.ApprovalMode(AgentKind.Codex, input, Temp()));
    }

    [Fact]
    public void ApprovalMode_CodexAutoReviewFromTheRolloutTurnContext()
    {
        var rollout = Path.Combine(Temp(), "rollout.jsonl");
        File.WriteAllText(rollout, "{\"type\":\"turn_context\",\"payload\":{\"approvals_reviewer\":\"auto_review\"}}\n");
        var input = AsksFixtures.Fixture("codex-shell");
        input["transcript_path"] = rollout;

        Assert.Equal("auto_review", Transcript.ApprovalMode(AgentKind.Codex, input, Temp()));
    }

    [Fact]
    public void ApprovalMode_CodexUsesTheLastTurnContext()
    {
        var rollout = Path.Combine(Temp(), "rollout.jsonl");
        File.WriteAllText(rollout,
            "{\"type\":\"turn_context\",\"payload\":{\"approvals_reviewer\":\"user\"}}\n"
            + "{\"type\":\"event\",\"payload\":{\"text\":\"x\"}}\n"
            + "{\"type\":\"turn_context\",\"payload\":{\"approvals_reviewer\":\"auto_review\"}}\n");
        var input = AsksFixtures.Fixture("codex-shell");
        input["transcript_path"] = rollout;

        Assert.Equal("auto_review", Transcript.ApprovalMode(AgentKind.Codex, input, Temp()));
    }

    [Fact]
    public void ApprovalMode_AntigravityPresetFromItsConfig()
    {
        var home = Temp();
        Directory.CreateDirectory(Path.Combine(home, ".gemini", "config"));
        File.WriteAllText(
            Path.Combine(home, ".gemini", "config", "config.json"),
            "{\"userSettings\":{\"permissionPreset\":\"AGENT_PERMISSION_PRESET_TURBO\"}}");

        Assert.Equal(
            "AGENT_PERMISSION_PRESET_TURBO",
            Transcript.ApprovalMode(AgentKind.Antigravity, AsksFixtures.Fixture("antigravity-run"), home));
    }

    [Fact]
    public void ApprovalMode_AntigravityWithoutConfigIsNull()
    {
        Assert.Null(Transcript.ApprovalMode(AgentKind.Antigravity, AsksFixtures.Fixture("antigravity-run"), Temp()));
    }

    [Fact]
    public void ApprovalMode_OtherAgentsAreNull()
    {
        Assert.Null(Transcript.ApprovalMode(AgentKind.Claude, AsksFixtures.Fixture("claude-bash"), Temp()));
    }

    // ---- selfApproved

    [Theory]
    [InlineData("auto_review", "shell", true)]
    [InlineData("bypassPermissions", "Edit", true)]
    [InlineData("dontAsk", "Bash", true)]
    [InlineData("AGENT_PERMISSION_PRESET_TURBO", "run_command", true)]
    [InlineData("AGENT_PERMISSION_PRESET_TURBO", "ask_question", false)]
    [InlineData("auto_review", "request_user_input", false)]
    [InlineData("default", "Bash", false)]
    [InlineData("", "Bash", false)]
    public void SelfApproved_CommandsAreTheAgentsButQuestionsStillNeedYou(string mode, string tool, bool expected)
    {
        Assert.Equal(expected, Transcript.SelfApproved(mode, tool));
    }

    [Fact]
    public void SelfApproved_NullModeIsFalse()
    {
        Assert.False(Transcript.SelfApproved(null, "Bash"));
    }

    [Fact]
    public void CodexAutoReview_IsNotAskedAtAll()
    {
        var input = AsksFixtures.Fixture("codex-shell");
        input["permission_mode"] = "auto_review";

        var mode = Transcript.ApprovalMode(AgentKind.Codex, input, Temp());

        Assert.True(Transcript.SelfApproved(mode, "shell"));
    }

    [Fact]
    public void AntigravityTurbo_CommandsAllowedButQuestionsStillCome()
    {
        var home = Temp();
        Directory.CreateDirectory(Path.Combine(home, ".gemini", "config"));
        File.WriteAllText(
            Path.Combine(home, ".gemini", "config", "config.json"),
            "{\"userSettings\":{\"permissionPreset\":\"AGENT_PERMISSION_PRESET_TURBO\"}}");
        var mode = Transcript.ApprovalMode(AgentKind.Antigravity, AsksFixtures.Fixture("antigravity-run"), home);

        Assert.True(Transcript.SelfApproved(mode, "run_command"));
        Assert.False(Transcript.SelfApproved(mode, "ask_question"));
    }

    // ---- Tail

    [Fact]
    public void Tail_ReadsEveryJsonLine()
    {
        var file = Path.Combine(Temp(), "session.jsonl");
        File.WriteAllText(file, "{\"a\":1}\n\n{\"b\":2}\n");

        var lines = Transcript.Tail(file);

        Assert.Equal(2, lines.Count);
        Assert.Equal(2, lines[1]["b"]!.GetValue<int>());
    }

    [Fact]
    public void Tail_SkipsThePartialFirstLineWhenTheFileIsLong()
    {
        var file = Path.Combine(Temp(), "session.jsonl");
        var big = "{\"big\":\"" + new string('a', 300_000) + "\"}";
        File.WriteAllText(file, big + "\n{\"b\":2}\n", Encoding.UTF8);

        var lines = Transcript.Tail(file);

        Assert.Single(lines);
        Assert.Equal(2, lines[0]["b"]!.GetValue<int>());
    }

    [Fact]
    public void Tail_MissingFileIsEmpty()
    {
        Assert.Empty(Transcript.Tail(Path.Combine(Temp(), "missing.jsonl")));
    }

    // ---- pendingCall and settled

    [Fact]
    public void Claude_PendingCallIsTheToolUseIdAndSettledWhenItsResultArrives()
    {
        var transcript = Path.Combine(Temp(), "session.jsonl");
        var bash = AsksFixtures.Fixture("claude-bash");
        var toolUse = new JsonObject
        {
            ["message"] = new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject
                {
                    ["type"] = "tool_use",
                    ["id"] = "toolu_1",
                    ["name"] = "Bash",
                    ["input"] = bash["tool_input"]!.DeepClone(),
                }),
            },
        };
        File.WriteAllText(transcript, toolUse.ToJsonString() + "\n");

        var before = Transcript.Tail(transcript);
        Assert.Equal("toolu_1", Transcript.PendingCall(AgentKind.Claude, "Bash", bash["tool_input"] as JsonObject, before));
        Assert.False(Transcript.Settled(AgentKind.Claude, "toolu_1", before));

        File.AppendAllText(transcript,
            "{\"message\":{\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"toolu_1\",\"content\":\"ok\"}]}}\n");

        var after = Transcript.Tail(transcript);
        Assert.True(Transcript.Settled(AgentKind.Claude, "toolu_1", after));
    }

    [Fact]
    public void Claude_PendingCallMatchesTheInputOrTakesTheNewestOfTheTool()
    {
        var lines = Lines(
            "{\"message\":{\"content\":[{\"type\":\"tool_use\",\"id\":\"old\",\"name\":\"Bash\",\"input\":{\"command\":\"ls\"}}]}}",
            "{\"message\":{\"content\":[{\"type\":\"tool_use\",\"id\":\"new\",\"name\":\"Bash\",\"input\":{\"command\":\"rm -rf dist\"}}]}}");

        Assert.Equal("old", Transcript.PendingCall(AgentKind.Claude, "Bash", AsksFixtures.Json("{\"command\":\"ls\"}"), lines));
        Assert.Equal("new", Transcript.PendingCall(AgentKind.Claude, "Bash", null, lines));
        Assert.Null(Transcript.PendingCall(AgentKind.Claude, "Bash", AsksFixtures.Json("{\"command\":\"pwd\"}"), lines));
        Assert.Null(Transcript.PendingCall(AgentKind.Claude, "Edit", null, lines));
    }

    [Fact]
    public void Claude_InputKeyOrderDoesNotMatter()
    {
        var lines = Lines(
            "{\"message\":{\"content\":[{\"type\":\"tool_use\",\"id\":\"t\",\"name\":\"Bash\",\"input\":{\"b\":1,\"a\":{\"y\":2,\"x\":3}}}]}}");

        Assert.Equal("t", Transcript.PendingCall(AgentKind.Claude, "Bash", AsksFixtures.Json("{\"a\":{\"x\":3,\"y\":2},\"b\":1}"), lines));
    }

    [Fact]
    public void Codex_PendingCallIsTheNewestFunctionCallAndSettledOnItsOutput()
    {
        var lines = Lines(
            "{\"payload\":{\"type\":\"function_call\",\"name\":\"shell\",\"call_id\":\"c1\"}}",
            "{\"payload\":{\"type\":\"function_call\",\"name\":\"shell\",\"call_id\":\"c2\"}}");

        Assert.Equal("c2", Transcript.PendingCall(AgentKind.Codex, "shell", null, lines));
        Assert.False(Transcript.Settled(AgentKind.Codex, "c2", lines));

        var done = lines.Append(AsksFixtures.Json("{\"payload\":{\"type\":\"function_call_output\",\"call_id\":\"c2\"}}")).ToList();
        Assert.True(Transcript.Settled(AgentKind.Codex, "c2", done));
    }

    [Theory]
    [InlineData(AgentKind.Gemini)]
    [InlineData(AgentKind.Antigravity)]
    [InlineData(AgentKind.Cursor)]
    [InlineData(AgentKind.OpenCode)]
    public void OtherAgents_HaveNoPendingCallAndNeverSettle(AgentKind kind)
    {
        var lines = Lines("{\"payload\":{\"type\":\"function_call\",\"call_id\":\"c1\"}}");

        Assert.Null(Transcript.PendingCall(kind, "run_shell_command", null, lines));
        Assert.False(Transcript.Settled(kind, "c1", lines));
    }
}
