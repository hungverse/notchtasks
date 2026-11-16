using System.Text.Json;
using System.Text.Json.Nodes;

namespace NotchTasks.Core.Asks;

/// Where and how the hook runs. Tests replace the clock, the waits, and the app and pid checks.
public sealed class HookOptions
{
    /// The Asks folder.
    public required string Dir { get; init; }

    /// The user's home folder: the agent settings and the Antigravity config live under it.
    public string Home { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// The hook's own pid (the ask's pid: the ask is pending while this process waits).
    public int Pid { get; init; } = Environment.ProcessId;

    /// The agent that started the hook. Null = the parent of this process.
    public int? ParentPid { get; init; }

    /// The clock.
    public Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.UtcNow;

    /// How long to wait for a reply (the agent's hook timeout is this plus 10 s).
    public int WaitSeconds { get; init; } = AskStore.DefaultWaitSeconds;

    /// Whether the app is running. Null = the app.pid check in Dir's folder.
    public Func<bool>? AppRunning { get; init; }

    /// Whether a pid is alive. Null = ProcessChecks.IsAlive.
    public Func<int, bool>? PidAlive { get; init; }
}

/// Port of runHook in electron/src/core/asks.js: what `NotchTasksHook.exe --hook <agent>` does with the agent's stdin.
public static class HookRunner
{
    /// How often the hook looks for a reply (asks.js POLL_MS).
    private const int PollMilliseconds = 250;

    /// Runs the hook for one payload. Returns the stdout the agent reads (ends with a newline when there is output)
    /// and the exit code, which is always 0.
    public static async Task<(string Stdout, int ExitCode)> RunAsync(AgentKind kind, string stdin, HookOptions opts)
    {
        var spec = AgentKinds.Spec(kind);
        var store = new AskStore(opts.Dir);
        var alive = opts.PidAlive ?? ProcessChecks.IsAlive;
        var running = opts.AppRunning ?? (() => store.AppRunning(opts.Pid));
        var agent = opts.ParentPid ?? ProcessChecks.CurrentParentId() ?? 0;
        var now = opts.Now;
        var wait = TimeSpan.FromSeconds(opts.WaitSeconds);

        (string, int) Done(JsonObject input, AskReply? reply) => (Emit(HookOutput.For(reply, input, kind)), 0);

        JsonObject? input;
        try
        {
            input = JsonNode.Parse(stdin) as JsonObject;
        }
        catch (Exception e) when (e is JsonException or ArgumentException)
        {
            input = null;
        }
        if (input is null)
        {
            return Done(new JsonObject(), null);
        }
        if (AsksJson.Str(input["hook_event_name"]) is { } eventName && eventName != spec.Event)
        {
            return Done(input, null);
        }

        var tool = AsksJson.Str(input["tool_name"]) ?? AsksJson.Str((input["toolCall"] as JsonObject)?["name"]) ?? string.Empty;
        var mode = Transcript.ApprovalMode(kind, input, opts.Home);
        if (Transcript.SelfApproved(mode, tool))
        {
            // No objection: Antigravity's own permission check still runs, and "ask" would force its dialog
            return (kind == AgentKind.Antigravity ? "{\"decision\":\"allow\"}\n" : string.Empty, 0);
        }
        if (!running())
        {
            return Done(input, null);
        }

        var id = Guid.NewGuid().ToString();
        var ask = AskParser.From(input, kind, id, opts.Pid, SessionName(input, spec.Name), now());
        try
        {
            store.Write(ask);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Done(input, null);
        }

        AskReply? reply = null;
        var end = now() + wait;
        var transcript = AsksJson.Str(input["transcript_path"]);
        if (string.IsNullOrWhiteSpace(transcript))
        {
            transcript = null;
        }
        var toolInput = input["tool_input"] as JsonObject;
        string? call = null;  // the id the agent gave this tool call in its log, once found there
        var checks = 0;
        try
        {
            while (reply is null && now() < end)
            {
                await Task.Delay(PollMilliseconds);
                reply = store.ReadReply(id);
                checks++;
                if (checks % 10 == 0 && !running())
                {
                    break; // the app quit: let the agent ask itself
                }
                if (checks % 5 == 0)
                {
                    if (!alive(agent))
                    {
                        break; // the agent is gone (quit, or the ask was cancelled with it)
                    }
                    // answered in the agent itself: it moves on without stopping this hook, so watch for that
                    if (transcript is not null)
                    {
                        var lines = Transcript.Tail(transcript);
                        call ??= Transcript.PendingCall(kind, tool, toolInput, lines);
                        if (call is not null && Transcript.Settled(kind, call, lines))
                        {
                            break;
                        }
                    }
                }
            }
        }
        finally
        {
            store.Remove(id);
        }
        return Done(input, reply);
    }

    /// The name the pill shows: the last folder of cwd (Cursor: of its first workspace when cwd is empty), else the agent's name.
    private static string SessionName(JsonObject input, string agentName)
    {
        var cwd = AsksJson.Str(input["cwd"]);
        if (string.IsNullOrEmpty(cwd) && input["workspacePaths"] is JsonArray paths)
        {
            cwd = AsksJson.Str(paths.FirstOrDefault());
        }
        if (string.IsNullOrEmpty(cwd))
        {
            return agentName;
        }
        var last = cwd.TrimEnd('\\', '/').Split('\\', '/').LastOrDefault() ?? string.Empty;
        return last.Length > 0 ? last : agentName;
    }

    /// The stdout for an answer: the text and a newline, or nothing.
    private static string Emit(string? output) => output is null ? string.Empty : output + "\n";
}
