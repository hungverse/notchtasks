using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NotchTasks.Core.Asks;

/// Port of the session-log half of electron/src/core/asks.js: how the agent is set to approve, the end of its log,
/// and whether the call an ask is about has already run (answered in the agent instead of on the notch).
public static class Transcript
{
    private const long TailBytes = 262144;
    private static readonly string[] SelfModes = ["auto_review", "bypassPermissions", "dontAsk", "AGENT_PERMISSION_PRESET_TURBO"];
    private static readonly string[] QuestionTools = ["request_user_input", "ask_question"];

    /// How the agent is set to approve: the hook runs ahead of it, and answering here would replace its own approval.
    /// Codex: permission_mode, else the approvals reviewer of its rollout. Antigravity: the preset in ~/.gemini/config/config.json.
    public static string? ApprovalMode(AgentKind kind, JsonObject input, string home)
    {
        switch (kind)
        {
            case AgentKind.Codex:
            {
                var mode = AsksJson.Str(input["permission_mode"]);
                if (mode is not null && mode != "default")
                {
                    return mode;
                }
                var transcript = AsksJson.Str(input["transcript_path"]);
                return string.IsNullOrWhiteSpace(transcript) ? null : AsksJson.Str(CodexTurn(transcript)?["approvals_reviewer"]);
            }
            case AgentKind.Antigravity:
            {
                var root = AsksJson.ReadJsonOrNull(Path.Combine(home, ".gemini", "config", "config.json")) as JsonObject;
                var settings = root?["userSettings"] as JsonObject;
                return AsksJson.Str(settings?["permissionPreset"]);
            }
            default:
                return null;
        }
    }

    /// Commands and edits are the agent's to approve in these modes; a question still needs you.
    public static bool SelfApproved(string? mode, string tool) =>
        mode is not null && Array.IndexOf(SelfModes, mode) >= 0 && Array.IndexOf(QuestionTools, tool) < 0;

    /// The end of the agent's session log (JSON lines), at most the last 256 KB. A partial first line is skipped.
    public static IReadOnlyList<JsonObject> Tail(string file)
    {
        var lines = new List<JsonObject>();
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var size = stream.Length;
            var from = size > TailBytes ? size - TailBytes : 0;
            stream.Seek(from, SeekOrigin.Begin);
            var buffer = new byte[size - from];
            var read = ReadFully(stream, buffer);
            foreach (var line in Encoding.UTF8.GetString(buffer, 0, read).Split('\n'))
            {
                if (line.Length == 0)
                {
                    continue;
                }
                try
                {
                    if (JsonNode.Parse(line) is JsonObject json)
                    {
                        lines.Add(json);
                    }
                }
                catch (Exception ex) when (ex is JsonException or ArgumentException)
                {
                    // the partial first line of the window
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
        return lines;
    }

    /// The id of the tool call this ask is about: the newest call of that tool with that input. Null: not found (yet).
    /// Claude matches on the tool name and input; Codex takes its newest function call. Other agents keep no call ids.
    public static string? PendingCall(AgentKind kind, string tool, JsonObject? toolInput, IReadOnlyList<JsonObject> lines)
    {
        switch (kind)
        {
            case AgentKind.Claude:
            {
                var uses = lines.SelectMany(Content)
                    .OfType<JsonObject>()
                    .Where(c => AsksJson.Str(c["type"]) == "tool_use" && AsksJson.Str(c["name"]) == tool)
                    .ToList();
                var same = uses
                    .Where(u => toolInput is null || AsksJson.Canonical(u["input"] as JsonObject ?? new JsonObject()) == AsksJson.Canonical(toolInput))
                    .ToList();
                var hit = same.Count > 0 ? same[^1] : toolInput is null && uses.Count > 0 ? uses[^1] : null;
                return AsksJson.Str(hit?["id"]);
            }
            case AgentKind.Codex:
            {
                var calls = lines
                    .Select(l => l["payload"])
                    .OfType<JsonObject>()
                    .Where(p => AsksJson.Str(p["type"]) == "function_call")
                    .ToList();
                return calls.Count > 0 ? AsksJson.Str(calls[^1]["call_id"]) : null;
            }
            default:
                // Gemini, Antigravity rewrite their whole session file, Cursor and OpenCode send none: only the agent
                // quitting ends their wait early
                return null;
        }
    }

    /// That call has its result in the log: it ran (allowed in the agent) or was refused there, so the ask is settled.
    public static bool Settled(AgentKind kind, string call, IReadOnlyList<JsonObject> lines)
    {
        switch (kind)
        {
            case AgentKind.Claude:
                return lines.Any(l => Content(l).OfType<JsonObject>()
                    .Any(c => AsksJson.Str(c["type"]) == "tool_result" && AsksJson.Str(c["tool_use_id"]) == call));
            case AgentKind.Codex:
                return lines.Any(l => l["payload"] is JsonObject p
                    && AsksJson.Str(p["type"]) == "function_call_output" && AsksJson.Str(p["call_id"]) == call);
            default:
                return false;
        }
    }

    /// The message content entries of one log line (empty when there are none, or it is a string).
    private static IEnumerable<JsonNode?> Content(JsonObject line)
    {
        if ((line["message"] as JsonObject)?["content"] is JsonArray content)
        {
            return content;
        }
        return Array.Empty<JsonNode?>();
    }

    /// The last turn_context entry of a Codex rollout file. Quotes inside recorded output are escaped, so only real entries match.
    private static JsonObject? CodexTurn(string transcript)
    {
        string text;
        try
        {
            text = File.ReadAllText(transcript);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
        const string mark = "\"type\":\"turn_context\"";
        var hit = text.LastIndexOf(mark, StringComparison.Ordinal);
        if (hit < 0)
        {
            return null;
        }
        var start = hit == 0 ? 0 : text.LastIndexOf('\n', hit - 1) + 1;
        var after = hit + mark.Length;
        var newline = text.IndexOf('\n', after);
        var end = newline < 0 ? text.Length : newline;
        try
        {
            return (JsonNode.Parse(text[start..end]) as JsonObject)?["payload"] as JsonObject;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            return null;
        }
    }

    private static int ReadFully(Stream stream, byte[] buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = stream.Read(buffer, total, buffer.Length - total);
            if (n == 0)
            {
                break;
            }
            total += n;
        }
        return total;
    }
}
