using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace NotchTasks.Core.Asks;

/// Port of the parsing half of electron/src/core/asks.js: what an agent's hook payload asks, and what the tool does.
public static class AskParser
{
    private const int SummaryElements = 600;

    private static readonly string[] RunTools =
        ["Bash", "run_shell_command", "run_command", "shell", "exec_command", "local_shell", "Execute", "bash"];

    private static readonly string[] EditTools =
    [
        "Edit", "Write", "MultiEdit", "NotebookEdit", "write_file", "replace", "write_to_file", "replace_file_content",
        "multi_replace_file_content", "apply_patch", "Create", "ApplyPatch", "edit", "write",
    ];

    private static readonly string[] ReadTools = ["Read", "read_file", "view_file"];
    private static readonly string[] WebTools = ["WebFetch", "WebSearch", "web_fetch", "FetchUrl", "webfetch"];

    /// The fields that hold the thing an agent is about to do, in the order asks.js looks for them.
    private static readonly string[] SummaryKeys =
        ["command", "CommandLine", "file_path", "TargetFile", "AbsolutePath", "url", "Url", "path", "pattern", "query", "description"];

    /// What an ask's tool does, in words a non-developer reads.
    public static string ActionLabel(string tool)
    {
        if (Array.IndexOf(RunTools, tool) >= 0) return L10n.L("Run command");
        if (Array.IndexOf(EditTools, tool) >= 0) return L10n.L("Edit file");
        if (Array.IndexOf(ReadTools, tool) >= 0) return L10n.L("Read file");
        if (Array.IndexOf(WebTools, tool) >= 0) return L10n.L("Web access");
        if (tool.StartsWith("mcp__", StringComparison.Ordinal))
        {
            var last = tool.Split('_', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty;
            return L10n.L("Use tool ") + last;
        }
        return tool;
    }

    /// The ask a hook payload makes. `input` is the parsed stdin JSON object; `session` is the name the pill shows.
    public static AgentAsk From(JsonObject input, AgentKind kind, string id, int pid, string session, DateTimeOffset now)
    {
        // Antigravity speaks camelCase: {"toolCall": {"name", "args"}}; the others {"tool_name", "tool_input"}.
        // Cursor sends the shell command itself at the top level.
        var call = input["toolCall"] as JsonObject;
        var tool = AsksJson.Str(input["tool_name"]) ?? AsksJson.Str(call?["name"]) ?? FallbackTool(input, kind);

        var ti = input["tool_input"] as JsonObject
            ?? call?["args"] as JsonObject
            ?? (kind == AgentKind.Cursor ? input : new JsonObject());
        if (kind == AgentKind.OpenCode)
        {
            ti = OpenCodeInput(input);
        }

        var questions = ReadQuestions(ti);
        var field = SummaryField(ti);
        var compact = AsksJson.ToText(ti);

        var summary = questions.Count > 0 ? questions[0].Question : field ?? compact;
        return new AgentAsk(
            id, pid, session, tool, Head(summary, SummaryElements), questions, now, kind.Id());
    }

    private static string FallbackTool(JsonObject input, AgentKind kind) => kind switch
    {
        AgentKind.Cursor => "shell",
        AgentKind.OpenCode => AsksJson.Str(input["action"]) ?? AsksJson.Str(input["type"]) ?? "?",
        _ => "?",
    };

    /// OpenCode sends its permission evaluation: {action: "bash"|"edit"|"webfetch", resources, metadata: {command…}}.
    private static JsonObject OpenCodeInput(JsonObject input)
    {
        var ti = new JsonObject();
        if (input["metadata"] is JsonObject metadata)
        {
            foreach (var kv in metadata)
            {
                ti[kv.Key] = kv.Value?.DeepClone();
            }
        }
        if (!ti.ContainsKey("command"))
        {
            string? p = null;
            if (input["resources"] is JsonArray resources && resources.All(r => AsksJson.Str(r) is not null))
            {
                p = string.Join(" ", resources.Select(r => AsksJson.Str(r)));
            }
            p ??= AsksJson.Str(input["pattern"]) ?? AsksJson.Str(input["title"]);
            if (p is not null)
            {
                ti["description"] = p;
            }
        }
        return ti;
    }

    /// Questions when every entry of tool_input.questions is an object; none otherwise.
    private static List<AskQuestion> ReadQuestions(JsonObject ti)
    {
        var result = new List<AskQuestion>();
        if (ti["questions"] is not JsonArray list || !list.All(q => q is JsonObject))
        {
            return result;
        }
        foreach (var q in list.Cast<JsonObject>())
        {
            result.Add(new AskQuestion(
                AsksJson.Str(q["question"]) ?? AsksJson.Str(q["title"]) ?? string.Empty,
                AsksJson.Str(q["header"]),
                OptionList(q["options"]),
                AsksJson.Bool(q["multiSelect"]) ?? AsksJson.Bool(q["is_multi_select"]) ?? false));
        }
        return result;
    }

    /// Options come as [{label}] (Claude, Gemini) or plain strings (Antigravity, Codex app).
    private static IReadOnlyList<string> OptionList(JsonNode? node)
    {
        if (node is not JsonArray list)
        {
            return [];
        }
        if (list.All(x => AsksJson.Str(x) is not null))
        {
            return list.Select(x => AsksJson.Str(x)!).ToList();
        }
        if (list.All(x => x is JsonObject))
        {
            return list.Cast<JsonObject>().Select(o => AsksJson.Str(o["label"])).OfType<string>().ToList();
        }
        return [];
    }

    /// The first summary field the payload has: a string, or an array of strings joined by spaces (Codex argv).
    private static string? SummaryField(JsonObject ti)
    {
        foreach (var key in SummaryKeys)
        {
            var v = ti[key];
            if (AsksJson.Str(v) is { } text)
            {
                return text;
            }
            if (v is JsonArray items && items.All(x => AsksJson.Str(x) is not null))
            {
                return string.Join(" ", items.Select(x => AsksJson.Str(x)));
            }
        }
        return null;
    }

    /// The first `count` text elements (user-perceived characters) of `s`.
    private static string Head(string s, int count)
    {
        var sb = new StringBuilder();
        var e = StringInfo.GetTextElementEnumerator(s);
        for (var n = 0; n < count && e.MoveNext(); n++)
        {
            sb.Append(e.GetTextElement());
        }
        return sb.ToString();
    }
}
