using System.Diagnostics;
using System.Text.Json.Nodes;
using NotchTasks.Core.Asks;
using Xunit;

namespace NotchTasks.Core.Tests;

// Ported from electron/test/asks.test.js (runHook cases): the exact stdout per agent, the no-ask paths, codex and
// antigravity self-approval, and a Claude transcript that settles the call. The Hook_* cases are the brief's list.
[Collection("L10n")]
public sealed class HookRunnerTests : IDisposable
{
    private const string Deny = "The user denied this from the NotchTasks notch.";
    private const string AnsweredText = "The user already answered this on the NotchTasks notch, so the dialog was not shown. "
        + "Their answers: \"Proceed?\" → \"Yes\". Continue with these answers; do not ask again.";
    private const string ClaudeAllow = "{\"hookSpecificOutput\":{\"hookEventName\":\"PermissionRequest\",\"decision\":{\"behavior\":\"allow\"}}}\n";
    private const string ClaudeDeny = "{\"hookSpecificOutput\":{\"hookEventName\":\"PermissionRequest\",\"decision\":{\"behavior\":\"deny\",\"message\":\""
        + Deny + "\"}}}\n";

    private static readonly Dictionary<string, string> Answers = new() { ["Proceed?"] = "Yes" };

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

    private HookOptions Options(string dir, string home, int waitSeconds = 10, Func<bool>? running = null, int? parent = null) => new()
    {
        Dir = dir,
        Home = home,
        Pid = Environment.ProcessId,
        ParentPid = parent ?? Environment.ProcessId,
        WaitSeconds = waitSeconds,
        AppRunning = running ?? (() => true),
    };

    /// The first ask the hook writes, once it appears.
    private static async Task<AgentAsk> WaitForAsk(AskStore store)
    {
        for (var i = 0; i < 100; i++)
        {
            var list = store.Pending(DateTimeOffset.UtcNow);
            if (list.Count > 0)
            {
                return list[0];
            }
            await Task.Delay(50);
        }
        throw new TimeoutException("no ask was written");
    }

    private static bool FolderHasFiles(string dir) => Directory.Exists(dir) && Directory.GetFileSystemEntries(dir).Length > 0;

    /// Runs the hook on a payload, answers its ask (null = no answer), and checks the Asks folder is clean afterwards.
    private async Task<(AgentAsk Ask, string Stdout, int ExitCode)> Hook(
        AgentKind kind, JsonObject payload, AskReply? answer, int waitSeconds = 10, int replyDelayMs = 0)
    {
        var dir = Path.Combine(Temp(), "Asks");
        var store = new AskStore(dir);
        var run = HookRunner.RunAsync(kind, payload.ToJsonString(), Options(dir, Temp(), waitSeconds));
        var ask = await WaitForAsk(store);
        if (answer is not null)
        {
            await Task.Delay(replyDelayMs);
            store.Reply(ask.Id, answer);
        }
        var (stdout, exit) = await run;
        Assert.False(FolderHasFiles(dir), "ask and reply files are removed");
        return (ask, stdout, exit);
    }

    private static JsonObject Fixture(string name) => AsksFixtures.Fixture(name);

    // ---- The brief's cases

    [Fact]
    public async Task Hook_AppNotRunning_ReturnsEmptyFast()
    {
        var dir = Path.Combine(Temp(), "Asks");
        // A warm-up call first, so JIT compilation is not counted in the measured run.
        await HookRunner.RunAsync(AgentKind.Claude, Fixture("claude-bash").ToJsonString(), Options(dir, Temp(), running: () => false));

        var sw = Stopwatch.StartNew();
        var (stdout, exit) = await HookRunner.RunAsync(AgentKind.Claude, Fixture("claude-bash").ToJsonString(),
            Options(dir, Temp(), running: () => false));
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 300, $"took {sw.ElapsedMilliseconds} ms");
        Assert.Equal("", stdout);
        Assert.Equal(0, exit);
        Assert.False(Directory.Exists(dir), "no ask is written");
    }

    [Fact]
    public async Task Hook_ReplyAllow_PrintsClaudeAllow()
    {
        var (ask, stdout, exit) = await Hook(AgentKind.Claude, Fixture("claude-bash"), new AskReply(ReplyKinds.Allow, null), replyDelayMs: 300);

        Assert.Equal(ClaudeAllow, stdout);
        Assert.Equal(0, exit);
        Assert.Equal("Bash", ask.Tool);
        Assert.Equal("rm -rf dist", ask.Summary);
        Assert.True(ask.Session == "notchtasks", "the session is the last folder of cwd");
        Assert.Equal("claude", ask.Agent);
    }

    [Fact]
    public async Task Hook_ParentGone_ExitsWithin2500ms()
    {
        using var parent = Process.Start(new ProcessStartInfo("ping.exe", "-n 60 127.0.0.1")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        var dir = Path.Combine(Temp(), "Asks");
        var store = new AskStore(dir);
        var run = HookRunner.RunAsync(AgentKind.Claude, Fixture("claude-bash").ToJsonString(),
            Options(dir, Temp(), waitSeconds: 60, parent: parent.Id));
        await WaitForAsk(store);

        parent.Kill();
        parent.WaitForExit();
        var sw = Stopwatch.StartNew();
        var (stdout, _) = await run;
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds <= 2500, $"took {sw.ElapsedMilliseconds} ms after the agent quit");
        Assert.Equal("", stdout);
        Assert.False(FolderHasFiles(dir));
    }

    [Fact]
    public async Task Hook_UnknownParent_KeepsWaiting()
    {
        // The parent cannot be resolved, so the agent-alive check is skipped (it used to read pid 0 as dead).
        var dir = Path.Combine(Temp(), "Asks");
        var store = new AskStore(dir);
        var options = new HookOptions
        {
            Dir = dir,
            Home = Temp(),
            Pid = Environment.ProcessId,
            ParentPid = null,
            ParentLookup = () => null,
            WaitSeconds = 10,
            AppRunning = () => true,
            PidAlive = _ => false,
        };
        var run = HookRunner.RunAsync(AgentKind.Claude, Fixture("claude-bash").ToJsonString(), options);
        var ask = await WaitForAsk(store);

        await Task.Delay(2000);
        Assert.False(run.IsCompleted, "the hook still waits after 2 s");
        store.Reply(ask.Id, new AskReply(ReplyKinds.Allow, null));
        var (stdout, _) = await run;

        Assert.Equal(ClaudeAllow, stdout);
    }

    [Fact]
    public async Task Hook_TranscriptSettled_Exits()
    {
        var bash = Fixture("claude-bash");
        var transcript = Path.Combine(Temp(), "session.jsonl");
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
        bash["transcript_path"] = transcript;
        var toolResult = new JsonObject
        {
            ["message"] = new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = "toolu_1", ["content"] = "ok" }),
            },
        };
        _ = Task.Run(async () =>
        {
            await Task.Delay(400);
            File.AppendAllText(transcript, toolResult.ToJsonString() + "\n");
        });

        var sw = Stopwatch.StartNew();
        var (stdout, _) = await HookRunner.RunAsync(AgentKind.Claude, bash.ToJsonString(),
            Options(Path.Combine(Temp(), "Asks"), Temp(), waitSeconds: 60));
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 10000, "left well before the 60 s wait");
        Assert.Equal("", stdout);
    }

    [Fact]
    public async Task Hook_SelfApprovedMode_ReturnsAtOnce()
    {
        var payload = Fixture("codex-shell");
        payload["permission_mode"] = "bypassPermissions";
        var dir = Path.Combine(Temp(), "Asks");

        var sw = Stopwatch.StartNew();
        var (stdout, _) = await HookRunner.RunAsync(AgentKind.Codex, payload.ToJsonString(), Options(dir, Temp(), waitSeconds: 60));
        sw.Stop();

        Assert.Equal("", stdout);
        Assert.True(sw.ElapsedMilliseconds < 1000, $"took {sw.ElapsedMilliseconds} ms");
        Assert.False(Directory.Exists(dir));
    }

    // ---- The Electron cases

    [Fact]
    public async Task NoReplyBeforeTheWaitRunsOut_PassesAndCleansUp()
    {
        var (_, stdout, exit) = await Hook(AgentKind.Claude, Fixture("claude-bash"), null, waitSeconds: 1);

        Assert.Equal("", stdout);
        Assert.Equal(0, exit);
    }

    [Fact]
    public async Task AntigravityNotRunning_AsksAsUsual()
    {
        var dir = Path.Combine(Temp(), "Asks");
        var (stdout, _) = await HookRunner.RunAsync(AgentKind.Antigravity, Fixture("antigravity-run").ToJsonString(),
            Options(dir, Temp(), running: () => false));

        Assert.Equal("{\"decision\":\"ask\"}\n", stdout);
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public async Task PayloadForAnotherEvent_PassesStraightThrough()
    {
        var dir = Path.Combine(Temp(), "Asks");
        var (stdout, _) = await HookRunner.RunAsync(AgentKind.Claude, Fixture("gemini-shell").ToJsonString(), Options(dir, Temp()));

        Assert.Equal("", stdout);
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public async Task NotJsonOnStdin_Passes()
    {
        var (stdout, exit) = await HookRunner.RunAsync(AgentKind.Gemini, "not json", Options(Path.Combine(Temp(), "Asks"), Temp()));

        Assert.Equal("", stdout);
        Assert.Equal(0, exit);
    }

    [Fact]
    public async Task EmptyTranscriptPath_IsTreatedAsAbsent()
    {
        var payload = Fixture("codex-shell");
        payload["transcript_path"] = "";

        var (ask, _, _) = await Hook(AgentKind.Codex, payload, new AskReply(ReplyKinds.Allow, null));

        Assert.Equal("shell", ask.Tool);
    }

    [Fact]
    public async Task CodexSetToApproveOnItsOwn_IsNotAsked()
    {
        var payload = Fixture("codex-shell");
        payload["permission_mode"] = "auto_review";
        var dir = Path.Combine(Temp(), "Asks");

        var (stdout, _) = await HookRunner.RunAsync(AgentKind.Codex, payload.ToJsonString(), Options(dir, Temp()));

        Assert.Equal("", stdout);
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public async Task AntigravityTurboPreset_IsAllowed_QuestionsStillComeHere()
    {
        var home = Temp();
        Directory.CreateDirectory(Path.Combine(home, ".gemini", "config"));
        File.WriteAllText(Path.Combine(home, ".gemini", "config", "config.json"),
            "{\"userSettings\":{\"permissionPreset\":\"AGENT_PERMISSION_PRESET_TURBO\"}}");
        var dir = Path.Combine(Temp(), "Asks");

        var (allowed, _) = await HookRunner.RunAsync(AgentKind.Antigravity, Fixture("antigravity-run").ToJsonString(), Options(dir, home));
        Assert.Equal("{\"decision\":\"allow\"}\n", allowed);
        Assert.False(Directory.Exists(dir));

        var question = Fixture("antigravity-run");
        ((JsonObject)question["toolCall"]!)["name"] = "ask_question";
        var store = new AskStore(dir);
        var run = HookRunner.RunAsync(AgentKind.Antigravity, question.ToJsonString(), Options(dir, home, waitSeconds: 1));
        Assert.Equal("ask_question", (await WaitForAsk(store)).Tool);
        var (asked, _) = await run;
        Assert.Equal("{\"decision\":\"ask\"}\n", asked);
    }

    [Fact]
    public async Task CodexAutoReviewFromTheRollout_IsAllowed()
    {
        var rollout = Path.Combine(Temp(), "rollout.jsonl");
        File.WriteAllText(rollout, "{\"type\":\"turn_context\",\"payload\":{\"approvals_reviewer\":\"auto_review\"}}\n");
        var payload = Fixture("codex-shell");
        payload["transcript_path"] = rollout;
        var dir = Path.Combine(Temp(), "Asks");

        var (stdout, _) = await HookRunner.RunAsync(AgentKind.Codex, payload.ToJsonString(), Options(dir, Temp()));

        Assert.Equal("", stdout);
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public async Task Claude_DenyPassAndAnswers_PrintWhatClaudeCodeExpects()
    {
        Assert.Equal(ClaudeDeny, (await Hook(AgentKind.Claude, Fixture("claude-bash"), new AskReply(ReplyKinds.Deny, null))).Stdout);
        Assert.Equal("", (await Hook(AgentKind.Claude, Fixture("claude-bash"), new AskReply(ReplyKinds.Pass, null))).Stdout);

        var answered = (await Hook(AgentKind.Claude, Fixture("claude-bash"), new AskReply(ReplyKinds.Answers, Answers))).Stdout;
        var expected = Fixture("claude-bash")["tool_input"]!.AsObject();
        expected["answers"] = new JsonObject { ["Proceed?"] = "Yes" };
        Assert.Equal(
            "{\"hookSpecificOutput\":{\"hookEventName\":\"PermissionRequest\",\"decision\":{\"behavior\":\"allow\",\"updatedInput\":"
            + expected.ToJsonString() + "}}}\n",
            answered);
    }

    [Fact]
    public async Task Codex_UsesThePermissionRequestShape()
    {
        Assert.Equal(ClaudeAllow, (await Hook(AgentKind.Codex, Fixture("codex-shell"), new AskReply(ReplyKinds.Allow, null))).Stdout);
        Assert.Equal(ClaudeDeny, (await Hook(AgentKind.Codex, Fixture("codex-shell"), new AskReply(ReplyKinds.Deny, null))).Stdout);
        Assert.Equal("", (await Hook(AgentKind.Codex, Fixture("codex-shell"), new AskReply(ReplyKinds.Pass, null))).Stdout);
    }

    [Fact]
    public async Task Gemini_AllowDenyAnswersPass()
    {
        Assert.Equal("{\"decision\":\"allow\"}\n", (await Hook(AgentKind.Gemini, Fixture("gemini-shell"), new AskReply(ReplyKinds.Allow, null))).Stdout);
        Assert.Equal("{\"decision\":\"deny\",\"reason\":\"" + Deny + "\"}\n",
            (await Hook(AgentKind.Gemini, Fixture("gemini-shell"), new AskReply(ReplyKinds.Deny, null))).Stdout);
        var answeredJson = new JsonObject { ["decision"] = "deny", ["reason"] = AnsweredText }.ToJsonString(AsksJson.Options) + "\n";
        Assert.Equal(answeredJson, (await Hook(AgentKind.Gemini, Fixture("gemini-shell"), new AskReply(ReplyKinds.Answers, Answers))).Stdout);
        Assert.Equal("", (await Hook(AgentKind.Gemini, Fixture("gemini-shell"), new AskReply(ReplyKinds.Pass, null))).Stdout);
    }

    [Fact]
    public async Task Antigravity_AllowGrantsThisCall_PassMeansAsk()
    {
        Assert.Equal("{\"decision\":\"allow\",\"permissionOverrides\":[\"command(npm test)\"]}\n",
            (await Hook(AgentKind.Antigravity, Fixture("antigravity-run"), new AskReply(ReplyKinds.Allow, null))).Stdout);
        Assert.Equal("{\"decision\":\"ask\"}\n",
            (await Hook(AgentKind.Antigravity, Fixture("antigravity-run"), new AskReply(ReplyKinds.Pass, null))).Stdout);
    }

    [Fact]
    public async Task Cursor_PermissionAllowDenyAsk()
    {
        Assert.Equal("{\"permission\":\"allow\"}\n",
            (await Hook(AgentKind.Cursor, Fixture("cursor-shell"), new AskReply(ReplyKinds.Allow, null))).Stdout);
        Assert.Equal("{\"permission\":\"deny\",\"user_message\":\"Denied on the notch\",\"agent_message\":\"" + Deny + "\"}\n",
            (await Hook(AgentKind.Cursor, Fixture("cursor-shell"), new AskReply(ReplyKinds.Deny, null))).Stdout);
        Assert.Equal("{\"permission\":\"ask\"}\n",
            (await Hook(AgentKind.Cursor, Fixture("cursor-shell"), new AskReply(ReplyKinds.Pass, null))).Stdout);
    }

    [Fact]
    public async Task OpenCode_StatusAllowDenyAsk_FolderIsTheAgentName()
    {
        var (ask, stdout, _) = await Hook(AgentKind.OpenCode, Fixture("opencode-permission"), new AskReply(ReplyKinds.Allow, null));

        Assert.Equal("{\"status\":\"allow\"}\n", stdout);
        Assert.Equal("bash", ask.Tool);
        Assert.Equal("git push origin main", ask.Summary);
        Assert.Equal("OpenCode", ask.Session);
        Assert.Equal("{\"status\":\"deny\"}\n", (await Hook(AgentKind.OpenCode, Fixture("opencode-permission"), new AskReply(ReplyKinds.Deny, null))).Stdout);
        Assert.Equal("{\"status\":\"ask\"}\n", (await Hook(AgentKind.OpenCode, Fixture("opencode-permission"), new AskReply(ReplyKinds.Pass, null))).Stdout);
    }
}
