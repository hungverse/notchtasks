using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NotchTasks.Core.Asks;

/// Port of the folder half of electron/src/core/asks.js: the Asks folder, the app's app.pid next to it, and the
/// ask and reply files. Asks are Asks\<id>.json, replies Asks\<id>.reply; only our own code reads them.
public sealed class AskStore
{
    /// The seconds an agent waits for a reply (asks.js WAIT).
    public const int DefaultWaitSeconds = 590;

    private static readonly string[] Kinds = [ReplyKinds.Allow, ReplyKinds.Deny, ReplyKinds.Pass, ReplyKinds.Answers];

    public AskStore(string dir)
    {
        Dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));
    }

    /// The Asks folder.
    public string Dir { get; }

    public string AskPath(string id) => Path.Combine(Dir, id + ".json");

    public string ReplyPath(string id) => Path.Combine(Dir, id + ".reply");

    /// app.pid sits next to the Asks folder (asks.js appPidFile).
    private string PidFile => Path.Combine(Path.GetDirectoryName(Dir)!, "app.pid");

    /// The app writes its pid while it runs, so the hooks know to wait for it. Call it at start-up.
    public void MarkAppRunning(int pid) =>
        AtomicFile.WriteAllText(PidFile, pid.ToString(CultureInfo.InvariantCulture));

    /// The app is running = its pid file names a live process other than selfPid (asks.js appRunning).
    public bool AppRunning(int selfPid)
    {
        int pid;
        try
        {
            var text = AtomicFile.ReadAllTextOrNull(PidFile);
            if (text is null || !int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out pid))
            {
                return false;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        return pid > 0 && pid != selfPid && ProcessChecks.IsAlive(pid);
    }

    /// Writes an ask file with the shape the Electron build and the Mac read.
    public void Write(AgentAsk ask)
    {
        var questions = new JsonArray();
        foreach (var q in ask.Questions)
        {
            var options = new JsonArray();
            foreach (var option in q.Options)
            {
                options.Add((JsonNode?)JsonValue.Create(option));
            }
            var item = new JsonObject { ["question"] = q.Question };
            if (q.Header is not null)
            {
                item["header"] = q.Header;
            }
            item["options"] = options;
            item["multiSelect"] = q.MultiSelect;
            questions.Add((JsonNode?)item);
        }
        var json = new JsonObject
        {
            ["id"] = ask.Id,
            ["pid"] = ask.Pid,
            ["session"] = ask.Session,
            ["tool"] = ask.Tool,
            ["summary"] = ask.Summary,
            ["questions"] = questions,
            ["at"] = ask.At.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            ["agent"] = ask.Agent,
        };
        AtomicFile.WriteAllText(AskPath(ask.Id), AsksJson.ToText(json));
    }

    /// The reply for an ask, or null when there is none yet or the file is not a reply (asks.js readReply).
    public AskReply? ReadReply(string id)
    {
        if (AsksJson.ReadJsonOrNull(ReplyPath(id)) is not JsonObject j)
        {
            return null;
        }
        var kind = AsksJson.Str(j["kind"]);
        if (kind is null || Array.IndexOf(Kinds, kind) < 0)
        {
            return null;
        }
        IReadOnlyDictionary<string, string>? answers = null;
        if (j["answers"] is JsonObject a)
        {
            var map = new Dictionary<string, string>();
            foreach (var (question, label) in a)
            {
                if (AsksJson.Str(label) is { } text)
                {
                    map[question] = text;
                }
            }
            answers = map;
        }
        return new AskReply(kind, answers);
    }

    /// Writes the answer for an ask (asks.js reply).
    public void Reply(string id, AskReply reply)
    {
        var json = new JsonObject { ["kind"] = reply.Kind };
        if (reply.Answers is not null)
        {
            var answers = new JsonObject();
            foreach (var (question, label) in reply.Answers)
            {
                answers[question] = label;
            }
            json["answers"] = answers;
        }
        AtomicFile.WriteAllText(ReplyPath(id), AsksJson.ToText(json));
    }

    /// Deletes an ask and its reply. Missing files are fine.
    public void Remove(string id)
    {
        TryDelete(AskPath(id));
        TryDelete(ReplyPath(id));
    }

    /// Asks still waiting, oldest first (asks.js pending). Asks of hooks that died, and asks older than the wait,
    /// are cleared; answered asks stay until the hook picks the reply up.
    public IReadOnlyList<AgentAsk> Pending(DateTimeOffset now, int waitSeconds = DefaultWaitSeconds)
    {
        string[] files;
        try
        {
            files = Directory.GetFiles(Dir, "*.json");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
        var asks = new List<AgentAsk>();
        foreach (var file in files)
        {
            if (AsksJson.ReadJsonOrNull(file) is not JsonObject j || ParseAsk(j) is not { } ask)
            {
                continue;
            }
            if (!ProcessChecks.IsAlive(ask.Pid) || now - ask.At > TimeSpan.FromSeconds(waitSeconds))
            {
                Remove(ask.Id);
                continue;
            }
            if (File.Exists(ReplyPath(ask.Id)))
            {
                continue;
            }
            asks.Add(ask);
        }
        return asks.OrderBy(a => a.At).ToList();
    }

    /// An ask file as an AgentAsk, or null when the required fields are missing or wrong.
    private static AgentAsk? ParseAsk(JsonObject j)
    {
        var id = AsksJson.Str(j["id"]);
        if (id is null || j["pid"] is not JsonValue pidNode || pidNode.GetValueKind() != JsonValueKind.Number
            || !pidNode.TryGetValue(out int pid))
        {
            return null;
        }
        if (AsksJson.Str(j["at"]) is not { } atText
            || !DateTimeOffset.TryParse(atText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at))
        {
            return null;
        }
        var questions = new List<AskQuestion>();
        if (j["questions"] is JsonArray list)
        {
            foreach (var q in list.OfType<JsonObject>())
            {
                var options = new List<string>();
                if (q["options"] is JsonArray optionNodes)
                {
                    options.AddRange(optionNodes.Select(AsksJson.Str).OfType<string>());
                }
                questions.Add(new AskQuestion(
                    AsksJson.Str(q["question"]) ?? string.Empty,
                    AsksJson.Str(q["header"]),
                    options,
                    AsksJson.Bool(q["multiSelect"]) ?? false));
            }
        }
        return new AgentAsk(
            id,
            pid,
            AsksJson.Str(j["session"]) ?? string.Empty,
            AsksJson.Str(j["tool"]) ?? string.Empty,
            AsksJson.Str(j["summary"]) ?? string.Empty,
            questions,
            at,
            AsksJson.Str(j["agent"]) ?? string.Empty);
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Locked for a moment, or already gone: the next scan clears it.
        }
    }
}
