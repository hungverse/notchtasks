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

    /// The program that runs the app, and the programs that can own an ask: the hook, and the old Electron hook (which ran as the app).
    private static readonly string[] AppPrograms = ["NotchTasks.exe"];
    private static readonly string[] AskPrograms = ["NotchTasksHook.exe", "NotchTasks.exe"];

    // Caps on what the app reads from an ask file (the hook writes more only when an agent sends more).
    private const int MaxQuestions = 10;
    private const int MaxOptions = 20;
    private const int MaxText = 2000;

    private readonly Func<int, string[], bool> _runsAs;

    /// runsAs(pid, fileNames) says whether the process is alive and runs one of the file names. Null = ProcessChecks.RunsAs.
    public AskStore(string dir, Func<int, string[], bool>? runsAs = null)
    {
        Dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));
        _runsAs = runsAs ?? ProcessChecks.RunsAs;
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
        return pid > 0 && pid != selfPid && _runsAs(pid, AppPrograms);
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
    /// are cleared; answered asks stay until the hook picks the reply up. Reply and temp files older than the wait
    /// (left by a crash between the write and the rename) are swept too.
    public IReadOnlyList<AgentAsk> Pending(DateTimeOffset now, int waitSeconds = DefaultWaitSeconds)
    {
        string[] files;
        try
        {
            files = Directory.GetFiles(Dir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
        var wait = TimeSpan.FromSeconds(waitSeconds);
        var asks = new List<AgentAsk>();
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            if (!name.EndsWith(".json", StringComparison.Ordinal))
            {
                if (name.EndsWith(".reply", StringComparison.Ordinal) || name.Contains(".tmp-", StringComparison.Ordinal))
                {
                    SweepIfOlder(file, now, wait);
                }
                continue;
            }
            if (AsksJson.ReadJsonOrNull(file) is not JsonObject j || ParseAsk(j) is not { } ask)
            {
                continue;
            }
            if (!_runsAs(ask.Pid, AskPrograms) || now - ask.At > wait)
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

    /// Deletes a reply or temp file that was last written more than the wait ago.
    private static void SweepIfOlder(string file, DateTimeOffset now, TimeSpan wait)
    {
        try
        {
            if (now - new DateTimeOffset(File.GetLastWriteTimeUtc(file)) > wait)
            {
                File.Delete(file);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Locked for a moment, or already gone: the next scan tries again.
        }
    }

    /// An ask file as an AgentAsk, or null when the required fields are missing or wrong. Text is capped at MaxText
    /// characters and the lists at MaxQuestions and MaxOptions; the rest is dropped.
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
            foreach (var q in list.OfType<JsonObject>().Take(MaxQuestions))
            {
                var options = new List<string>();
                if (q["options"] is JsonArray optionNodes)
                {
                    options.AddRange(optionNodes.Select(AsksJson.Str).OfType<string>().Take(MaxOptions).Select(Cap));
                }
                questions.Add(new AskQuestion(
                    Cap(AsksJson.Str(q["question"]) ?? string.Empty),
                    AsksJson.Str(q["header"]) is { } header ? Cap(header) : null,
                    options,
                    AsksJson.Bool(q["multiSelect"]) ?? false));
            }
        }
        return new AgentAsk(
            id,
            pid,
            Cap(AsksJson.Str(j["session"]) ?? string.Empty),
            Cap(AsksJson.Str(j["tool"]) ?? string.Empty),
            Cap(AsksJson.Str(j["summary"]) ?? string.Empty),
            questions,
            at,
            Cap(AsksJson.Str(j["agent"]) ?? string.Empty));
    }

    /// The text cut to MaxText characters, not splitting a surrogate pair.
    private static string Cap(string text)
    {
        if (text.Length <= MaxText)
        {
            return text;
        }
        var length = MaxText;
        if (char.IsHighSurrogate(text[length - 1]))
        {
            length--;
        }
        return text[..length];
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
