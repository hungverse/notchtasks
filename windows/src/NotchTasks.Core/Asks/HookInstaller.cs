using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NotchTasks.Core.Asks;

/// Installs, checks and removes the NotchTasks hook in each agent's settings file (asks.js isInstalled, setInstalled,
/// sortedJson, openCodePlugin). Only our hook is added or removed; the rest of the file is kept. The old file is saved
/// beside it as <file>.notchtasks-backup. A file we cannot parse is never overwritten.
public static class HookInstaller
{
    /// Antigravity keys its hooks by this name, and Gemini lists its hook under it.
    public const string HookName = "notchtasks";

    /// The agent waits this long for a hook (AskStore.DefaultWaitSeconds); the hook's own timeout is 10 s longer.
    private const int TimeoutSeconds = AskStore.DefaultWaitSeconds + 10;

    private const string BackupSuffix = ".notchtasks-backup";

    // JSON.stringify(v, null, 2) writes "\n" and two spaces; the default newline is the platform's (CRLF on Windows).
    private static readonly JsonSerializerOptions IndentedOptions = new()
    {
        WriteIndented = true,
        IndentSize = 2,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private const string OpenCodeTemplate = """
        // Written by NotchTasks: answers OpenCode's permission prompts from the notch. Turn it off in NotchTasks.
        import { spawn } from "node:child_process"

        const ask = (request) => new Promise((resolve) => {
          const p = spawn(__EXE__, ["--hook", "opencode"], { stdio: ["pipe", "pipe", "ignore"] })
          let out = ""
          p.stdout.on("data", (d) => (out += d))
          p.on("close", () => resolve(out))
          p.on("error", () => resolve(""))
          p.stdin.end(JSON.stringify(request))
        })

        export default {
          id: "notchtasks",
          async setup(ctx) {
            await ctx.permission.hook("evaluate", async (event) => {
              if (event.effect !== "ask") return  // already allowed by your rules: nothing to ask
              const reply = await ask({ action: event.action, resources: event.resources, metadata: event.metadata, sessionID: event.sessionID, agent: event.agent })
              try {
                const status = JSON.parse(reply).status
                if (status === "allow" || status === "deny") event.effect = status
                if (status === "deny") event.message = "The user denied this from the NotchTasks notch."
              } catch {}
            })
          },
        }
        """;

    /// True when the agent's settings hold a NotchTasks hook (the old Electron one or the current one).
    public static bool IsInstalled(AgentKind kind, string home)
    {
        var file = SettingsFile(kind, home);
        if (kind == AgentKind.OpenCode)
        {
            return (ReadTextOrNull(file) ?? "").Contains("--hook", StringComparison.Ordinal);
        }
        if (AsksJson.ReadJsonOrNull(file) is not JsonObject root)
        {
            return false;
        }
        if (kind == AgentKind.Antigravity)
        {
            return root[HookName] is not null;
        }
        return EventGroups(kind, root).Any(IsOurs);
    }

    /// True when the agent's settings can be changed by Install or Uninstall: the file is missing or empty, or it parses as a
    /// JSON object. OpenCode's plugin file is always ours to write. False when the file exists but cannot be read or parsed,
    /// which is when Install and Uninstall would throw and leave it untouched.
    public static bool CanEdit(AgentKind kind, string home)
    {
        if (kind == AgentKind.OpenCode)
        {
            return true;
        }
        string? raw;
        try
        {
            raw = AtomicFile.ReadAllTextOrNull(SettingsFile(kind, home));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        if (string.IsNullOrEmpty(raw))
        {
            return true;
        }
        try
        {
            return JsonNode.Parse(raw) is JsonObject;
        }
        catch (Exception e) when (e is JsonException or ArgumentException)
        {
            return false;
        }
    }

    /// True when the agent's settings run exactly `"<hookExe>" --hook <agent>` (OpenCode: its plugin spawns that exe).
    public static bool PointsAt(AgentKind kind, string hookExe, string home)
    {
        var file = SettingsFile(kind, home);
        if (kind == AgentKind.OpenCode)
        {
            var text = ReadTextOrNull(file) ?? "";
            return text.Contains(JsonString(hookExe), StringComparison.Ordinal)
                && text.Contains("[\"--hook\", \"opencode\"]", StringComparison.Ordinal);
        }
        if (AsksJson.ReadJsonOrNull(file) is not JsonObject root)
        {
            return false;
        }
        var want = Command(kind, hookExe);
        return EventGroups(kind, root).Any(group => CommandsOf(group).Contains(want, StringComparer.Ordinal));
    }

    /// Adds our hook to the agent's settings, replacing an older NotchTasks hook. Throws InvalidDataException, leaving the
    /// file untouched, when the settings file is not a JSON object.
    public static void Install(AgentKind kind, string hookExe, string home)
    {
        ArgumentException.ThrowIfNullOrEmpty(hookExe);
        SetInstalled(on: true, kind, hookExe, home);
    }

    /// Removes our hook from the agent's settings; everything else is kept. Same file rules as Install.
    public static void Uninstall(AgentKind kind, string home) => SetInstalled(on: false, kind, hookExe: "", home);

    /// Removes our hook from each agent whose settings run exactly this hookExe, for the uninstaller. Each agent is tried on
    /// its own, and a failure is ignored, so one broken settings file does not keep the others from being cleaned. Returns the
    /// agents whose hook was removed.
    public static IReadOnlyList<AgentKind> UninstallPointingAt(string hookExe, string home)
    {
        var removed = new List<AgentKind>();
        foreach (var kind in Enum.GetValues<AgentKind>())
        {
            try
            {
                if (IsInstalled(kind, home) && PointsAt(kind, hookExe, home))
                {
                    Uninstall(kind, home);
                    removed.Add(kind);
                }
            }
            catch (Exception)
            {
                // The file is left as it was (Uninstall writes nothing it cannot parse). The uninstall goes on.
            }
        }
        return removed;
    }

    /// The hook command an agent runs: the quoted exe path, then --hook and the agent's id (asks.js command).
    public static string Command(AgentKind kind, string hookExe) => $"\"{hookExe}\" --hook {kind.Id()}";

    /// The OpenCode plugin text that hands each permission prompt to "<exe> --hook opencode" (asks.js openCodePlugin).
    public static string OpenCodePlugin(string hookExe) =>
        OpenCodeTemplate.Replace("__EXE__", JsonString(hookExe)).Replace("\r\n", "\n") + "\n";

    /// JSON with object keys sorted at every level and a 2-space indent, with no trailing newline:
    /// JSON.stringify(sort(v), null, 2) in asks.js. Non-ASCII stays as it is; only JSON's own escapes are written.
    public static string SortedJson(JsonNode? node) => Sorted(node)?.ToJsonString(IndentedOptions) ?? "null";

    private static void SetInstalled(bool on, AgentKind kind, string hookExe, string home)
    {
        var file = SettingsFile(kind, home);
        if (kind == AgentKind.OpenCode)
        {
            SetOpenCode(on, file, hookExe, home);
            return;
        }

        var root = ReadRootForWrite(file, out var existed);
        if (!on && !existed)
        {
            return;  // nothing to remove, and no file is written
        }
        if (existed)
        {
            Backup(file);
        }

        var spec = AgentKinds.Spec(kind);
        if (kind == AgentKind.Antigravity)
        {
            if (on)
            {
                root[HookName] = new JsonObject { [spec.Event] = new JsonArray(Group(kind, spec, hookExe)) };
            }
            else
            {
                root.Remove(HookName);
            }
        }
        else if (kind == AgentKind.Cursor)
        {
            // Cursor's list is flat: the command sits on the entry itself, and the file keeps its version
            var hooks = HooksObject(root);
            var list = Without(hooks[spec.Event], IsOurs);
            if (on)
            {
                list.Add(new JsonObject { ["command"] = Command(kind, hookExe) });
            }
            if (list.Count > 0)
            {
                hooks[spec.Event] = new JsonArray(list.ToArray());
            }
            else
            {
                hooks.Remove(spec.Event);
            }
            root["version"] ??= 1;
        }
        else
        {
            var hooks = HooksObject(root);
            var groups = Without(hooks[spec.Event], IsOurs);
            if (on)
            {
                groups.Add(Group(kind, spec, hookExe));
            }
            if (groups.Count > 0)
            {
                hooks[spec.Event] = new JsonArray(groups.ToArray());
            }
            else
            {
                hooks.Remove(spec.Event);
            }
            if (hooks.Count == 0)
            {
                root.Remove("hooks");
            }
        }

        AtomicFile.WriteAllText(file, SortedJson(root));
    }

    private static void SetOpenCode(bool on, string file, string hookExe, string home)
    {
        if (on)
        {
            if (File.Exists(file))
            {
                Backup(file);
            }
            AtomicFile.WriteAllText(file, OpenCodePlugin(hookExe));
        }
        else if (IsInstalled(AgentKind.OpenCode, home))
        {
            File.Delete(file);  // only ever our own file
        }
    }

    /// The matcher group that holds our handler (Gemini's handler is named and its timeout is in milliseconds).
    private static JsonObject Group(AgentKind kind, AgentSpec spec, string hookExe)
    {
        var handler = new JsonObject
        {
            ["type"] = "command",
            ["command"] = Command(kind, hookExe),
            ["timeout"] = kind == AgentKind.Gemini ? TimeoutSeconds * 1000 : TimeoutSeconds,
        };
        if (kind == AgentKind.Gemini)
        {
            handler["name"] = HookName;
        }
        return new JsonObject
        {
            ["matcher"] = spec.Matcher,
            ["hooks"] = new JsonArray(handler),
        };
    }

    /// The matcher groups under the agent's event (Antigravity: under its name). Empty when there are none.
    private static IEnumerable<JsonNode?> EventGroups(AgentKind kind, JsonObject root)
    {
        var holder = kind == AgentKind.Antigravity ? root[HookName] as JsonObject : root["hooks"] as JsonObject;
        var groups = holder?[AgentKinds.Spec(kind).Event] as JsonArray;
        return groups ?? Enumerable.Empty<JsonNode?>();
    }

    /// The commands a group runs: its own handlers, and the group itself when it is a flat Cursor entry.
    private static IEnumerable<string> CommandsOf(JsonNode? group)
    {
        var entries = new List<JsonNode?>();
        if (group is JsonObject g && g["hooks"] is JsonArray handlers)
        {
            entries.AddRange(handlers);
        }
        entries.Add(group);
        return entries.OfType<JsonObject>().Select(e => AsksJson.Str(e["command"])).OfType<string>();
    }

    /// A group is ours when one of its commands names NotchTasks and --hook (both the Electron and the current command).
    private static bool IsOurs(JsonNode? group) =>
        CommandsOf(group).Any(c => c.Contains("NotchTasks", StringComparison.Ordinal) && c.Contains("--hook", StringComparison.Ordinal));

    /// root.hooks as an object. A missing or non-object value is replaced by {} (asks.js: isObj(root.hooks) ? root.hooks : {}).
    private static JsonObject HooksObject(JsonObject root)
    {
        if (root["hooks"] is JsonObject hooks)
        {
            return hooks;
        }
        var fresh = new JsonObject();
        root["hooks"] = fresh;
        return fresh;
    }

    /// Copies of the array's items that are not dropped. A JsonNode can sit under one parent only, so the copies are detached.
    private static List<JsonNode?> Without(JsonNode? node, Func<JsonNode?, bool> drop)
    {
        var kept = new List<JsonNode?>();
        if (node is JsonArray items)
        {
            foreach (var item in items)
            {
                if (!drop(item))
                {
                    kept.Add(item?.DeepClone());
                }
            }
        }
        return kept;
    }

    /// The settings object to change. A missing or empty file gives {} (existed = false). A file that does not parse, or whose
    /// root is not an object, throws and is never written.
    internal static JsonObject ReadRootForWrite(string file, out bool existed)
    {
        var raw = AtomicFile.ReadAllTextOrNull(file);
        existed = !string.IsNullOrEmpty(raw);
        if (!existed)
        {
            return new JsonObject();
        }

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(raw!);
        }
        catch (Exception e) when (e is JsonException or ArgumentException)
        {
            throw new InvalidDataException($"NotchTasks: {file} is not valid JSON, so it was left unchanged.", e);
        }
        return parsed as JsonObject
            ?? throw new InvalidDataException("NotchTasks: the agent settings file is not a JSON object, so it was left unchanged.");
    }

    /// The old file, saved beside it. Overwritten on every change, as asks.js does.
    internal static void Backup(string file) => File.Copy(file, file + BackupSuffix, overwrite: true);

    private static JsonNode? Sorted(JsonNode? node) => node switch
    {
        null => null,
        JsonObject obj => SortedObject(obj),
        JsonArray arr => new JsonArray(arr.Select(Sorted).ToArray()),
        _ => node.DeepClone(),
    };

    private static JsonObject SortedObject(JsonObject obj)
    {
        var result = new JsonObject();
        foreach (var key in obj.Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal))
        {
            result[key] = Sorted(obj[key]);
        }
        return result;
    }

    /// A string as a JSON literal, the way JSON.stringify writes it (quotes and backslashes escaped, non-ASCII kept).
    private static string JsonString(string text) => AsksJson.ToText(JsonValue.Create(text)!);

    private static string SettingsFile(AgentKind kind, string home) => AgentKinds.Spec(kind).SettingsFile(home);

    /// The file's text, or null when it is missing or cannot be read.
    private static string? ReadTextOrNull(string file)
    {
        try
        {
            return AtomicFile.ReadAllTextOrNull(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
