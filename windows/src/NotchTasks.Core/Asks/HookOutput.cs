using System.Text.Json.Nodes;

namespace NotchTasks.Core.Asks;

/// Port of `output` in electron/src/core/asks.js: the stdout each agent expects for the user's reply.
public static class HookOutput
{
    /// What the agent reads when the user denied the ask on the notch.
    public const string Denied = "The user denied this from the NotchTasks notch.";

    /// The hook's stdout for a reply (compact JSON, no trailing newline), or null = print nothing, so the agent asks as usual.
    /// A null reply is a pass. `input` is the hook payload the ask came from.
    public static string? For(AskReply? reply, JsonObject input, AgentKind kind)
    {
        var r = reply?.Kind ?? ReplyKinds.Pass;
        var answers = reply?.Answers;
        JsonObject output;
        switch (kind)
        {
            case AgentKind.Claude:
            case AgentKind.Codex:
            {
                if (r == ReplyKinds.Pass)
                {
                    return null;
                }
                JsonObject decision;
                if (r == ReplyKinds.Allow)
                {
                    decision = new JsonObject { ["behavior"] = "allow" };
                }
                else if (r == ReplyKinds.Deny)
                {
                    decision = new JsonObject { ["behavior"] = "deny", ["message"] = Denied };
                }
                else
                {
                    var updated = input["tool_input"] is JsonObject ti ? ti.DeepClone() as JsonObject ?? new JsonObject() : new JsonObject();
                    if (answers is not null)
                    {
                        updated["answers"] = AnswersObject(answers);
                    }
                    decision = new JsonObject { ["behavior"] = "allow", ["updatedInput"] = updated };
                }
                output = new JsonObject
                {
                    ["hookSpecificOutput"] = new JsonObject { ["hookEventName"] = "PermissionRequest", ["decision"] = decision },
                };
                break;
            }
            case AgentKind.Gemini:
            case AgentKind.Antigravity:
            {
                if (r == ReplyKinds.Answers)
                {
                    // Their question tools take no answer from a hook: block the dialog and hand the answer over in its place
                    output = new JsonObject { ["decision"] = "deny", ["reason"] = Answered(answers) };
                }
                else if (r == ReplyKinds.Allow)
                {
                    // Antigravity's own permission check still runs after an allow: grant this one call
                    var grant = kind == AgentKind.Antigravity ? Grant(input) : null;
                    output = grant is null
                        ? new JsonObject { ["decision"] = "allow" }
                        : new JsonObject { ["decision"] = "allow", ["permissionOverrides"] = new JsonArray(grant) };
                }
                else if (r == ReplyKinds.Deny)
                {
                    output = new JsonObject { ["decision"] = "deny", ["reason"] = Denied };
                }
                else
                {
                    // Gemini: no output = carry on and ask as usual; Antigravity needs to be told
                    if (kind == AgentKind.Gemini)
                    {
                        return null;
                    }
                    output = new JsonObject { ["decision"] = "ask" };
                }
                break;
            }
            case AgentKind.Cursor:
                if (r == ReplyKinds.Allow || r == ReplyKinds.Answers)
                {
                    output = new JsonObject { ["permission"] = "allow" };
                }
                else if (r == ReplyKinds.Deny)
                {
                    output = new JsonObject
                    {
                        ["permission"] = "deny",
                        ["user_message"] = L10n.L("Denied on the notch"),
                        ["agent_message"] = Denied,
                    };
                }
                else
                {
                    output = new JsonObject { ["permission"] = "ask" };
                }
                break;
            case AgentKind.OpenCode:
                output = new JsonObject
                {
                    ["status"] = r == ReplyKinds.Allow || r == ReplyKinds.Answers ? "allow"
                        : r == ReplyKinds.Deny ? "deny" : "ask",
                };
                break;
            default:
                return null;
        }
        return AsksJson.ToText(output);
    }

    /// The answers as an object, in the order given.
    private static JsonObject AnswersObject(IReadOnlyDictionary<string, string> answers)
    {
        var o = new JsonObject();
        foreach (var (question, label) in answers)
        {
            o[question] = label;
        }
        return o;
    }

    /// What Gemini and Antigravity read as the deny reason when the answers were given on the notch.
    /// The entries are sorted by their formatted text, as asks.js does (ordinal, like JavaScript's sort).
    private static string Answered(IReadOnlyDictionary<string, string>? answers)
    {
        var entries = (answers ?? new Dictionary<string, string>())
            .Select(kv => $"\"{kv.Key}\" → \"{kv.Value}\"")
            .ToList();
        entries.Sort(StringComparer.Ordinal);
        return "The user already answered this on the NotchTasks notch, so the dialog was not shown. Their answers: "
            + string.Join("; ", entries)
            + ". Continue with these answers; do not ask again.";
    }

    /// Antigravity's permission override for the call: a command line, or a file it writes.
    private static string? Grant(JsonObject input)
    {
        var args = input["toolCall"] is JsonObject call && call["args"] is JsonObject a ? a : new JsonObject();
        if (AsksJson.Str(args["CommandLine"]) is { } command)
        {
            return $"command({command})";
        }
        if (AsksJson.Str(args["TargetFile"]) is { } file)
        {
            return $"write_file({file})";
        }
        return null;
    }
}
