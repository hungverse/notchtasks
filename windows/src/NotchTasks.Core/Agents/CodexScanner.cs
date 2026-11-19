using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NotchTasks.Core.Asks;
using NotchTasks.Core.Limits;

namespace NotchTasks.Core.Agents;

/// The last task event of a rollout: Working is true for task_started, false for task_complete or turn_aborted.
public sealed record CodexStatus(bool Working, DateTimeOffset At);

/// A question Codex left for you (request_user_input). It stays open until you write back in that thread.
public sealed record CodexQuestion(string Thread, string CallId, string Tool, IReadOnlyList<AskQuestion> Questions, DateTimeOffset At);

/// The newest rate_limits event of a rollout.
public sealed record CodexLimitsFound(DateTimeOffset At, IReadOnlyList<LimitWindow> Windows);

/// The pure parsers of a Codex rollout (Electron: providers.js parseCodexStatus, parseCodexLimits, parseCodexQuestion,
/// codexTitleAndThread; Mac: Providers.swift CodexStatus, CodexQuestion). Each works on text of complete lines.
public static partial class CodexRollout
{
    private const string TimestampMark = "\"timestamp\":\"";
    private const int IsoLength = 24;

    [GeneratedRegex("\"cwd\":\"((?:[^\"\\\\]|\\\\.)*)\"")]
    private static partial Regex CwdPattern();

    [GeneratedRegex("\"session_id\":\"((?:[^\"\\\\]|\\\\.)*)\"")]
    private static partial Regex SessionIdPattern();

    /// The last task_started / task_complete / turn_aborted event decides the status. Null when there is none.
    public static CodexStatus? ParseStatus(string text)
    {
        var lines = text.Split('\n');
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i];
            if (!line.Contains("\"type\":\"event_msg\"", StringComparison.Ordinal))
            {
                continue;
            }
            var started = line.Contains("\"payload\":{\"type\":\"task_started\"", StringComparison.Ordinal);
            if (!(started
                || line.Contains("\"payload\":{\"type\":\"task_complete\"", StringComparison.Ordinal)
                || line.Contains("\"payload\":{\"type\":\"turn_aborted\"", StringComparison.Ordinal)))
            {
                continue;
            }
            var r = line.IndexOf(TimestampMark, StringComparison.Ordinal);
            if (r < 0)
            {
                return null;
            }
            var at = Providers.ParseIso(Slice(line, r + TimestampMark.Length, IsoLength));
            return at is { } t ? new CodexStatus(started, t) : null;
        }
        return null;
    }

    /// The newest rate_limits event: its time and windows. A window whose reset has passed has nothing used yet.
    /// A line without a timestamp is dated MinValue. Null when there is no rate_limits event.
    public static CodexLimitsFound? ParseLimits(string text, DateTimeOffset now)
    {
        var lines = text.Split('\n');
        var nowSeconds = now.ToUnixTimeMilliseconds() / 1000.0;
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i];
            if (!line.Contains("\"rate_limits\":{", StringComparison.Ordinal))
            {
                continue;
            }
            using var doc = TryParse(line);
            if (doc is null)
            {
                continue;
            }
            var rl = Get(Get(doc.RootElement, "payload"), "rate_limits");
            if (rl.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            var at = Providers.ParseIso(Str(Get(doc.RootElement, "timestamp"))) ?? DateTimeOffset.MinValue;
            var windows = new List<LimitWindow>();
            foreach (var key in new[] { "primary", "secondary" })
            {
                var w = Get(rl, key);
                var used = Number(Get(w, "used_percent"));
                var minutes = Get(w, "window_minutes");
                var resets = Number(Get(w, "resets_at"));
                if (used is null || resets is null || minutes.ValueKind != JsonValueKind.Number || !minutes.TryGetInt64(out var mins))
                {
                    continue;
                }
                windows.Add(new LimitWindow(
                    WindowLabel(mins),
                    resets.Value > nowSeconds ? used.Value : 0,
                    DateTimeOffset.FromUnixTimeMilliseconds((long)(resets.Value * 1000))));
            }
            return new CodexLimitsFound(at, windows);
        }
        return null;
    }

    /// "5h" under a day of minutes, else "7d" (providers.js windowLabel).
    public static string WindowLabel(long minutes) =>
        minutes < 1440 ? $"{minutes / 60}h" : $"{minutes / 1440}d";

    /// Reads the question lines and your messages from new rollout text. Returns the one still open: a message you wrote
    /// in the thread closes it. Options are plain strings (the desktop app) or {label} objects (the terminal one).
    public static CodexQuestion? ParseQuestion(string text, string thread, CodexQuestion? open, DateTimeOffset fallbackAt)
    {
        var current = open;
        foreach (var line in text.Split('\n'))
        {
            if (!line.Contains("\"type\":\"response_item\"", StringComparison.Ordinal))
            {
                continue;
            }
            if (line.Contains("\"role\":\"user\"", StringComparison.Ordinal))
            {
                current = null;
                continue;
            }
            if (!line.Contains("request_user_input", StringComparison.Ordinal) || !line.Contains("\"type\":\"function_call\"", StringComparison.Ordinal))
            {
                continue;
            }
            using var doc = TryParse(line);
            if (doc is null)
            {
                continue;
            }
            var payload = Get(doc.RootElement, "payload");
            var name = Str(Get(payload, "name"));
            var arguments = Str(Get(payload, "arguments"));
            if (name is null || !name.StartsWith("request_user_input", StringComparison.Ordinal) || arguments is null)
            {
                continue;
            }
            using var args = TryParse(arguments);
            if (args is null || args.RootElement.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            var questions = ReadQuestions(Get(args.RootElement, "questions"));
            var at = Providers.ParseIso(Str(Get(doc.RootElement, "timestamp"))) ?? fallbackAt;
            if (questions.Count > 0)
            {
                current = new CodexQuestion(thread, Str(Get(payload, "call_id")) ?? Guid.NewGuid().ToString("N"), name, questions, at);
            }
        }
        return current;
    }

    /// The folder name in a rollout's head line, and its thread (session id). The title is "Codex" when there is no cwd.
    public static (string Title, string Thread) TitleAndThread(string head)
    {
        var title = "Codex";
        var cwd = CwdPattern().Match(head);
        if (cwd.Success)
        {
            var value = cwd.Groups[1].Value;
            try
            {
                using var doc = JsonDocument.Parse("\"" + value + "\"");
                value = doc.RootElement.GetString() ?? value;
            }
            catch (JsonException)
            {
                // keep the raw text, as providers.js does
            }
            var last = Providers.LastComponent(value);
            if (last.Length > 0)
            {
                title = last;
            }
        }
        var sid = SessionIdPattern().Match(head);
        return (title, sid.Success ? sid.Groups[1].Value : string.Empty);
    }

    /// The questions of a request_user_input call. Only when every entry is an object; otherwise none.
    private static List<AskQuestion> ReadQuestions(JsonElement list)
    {
        var result = new List<AskQuestion>();
        if (list.ValueKind != JsonValueKind.Array)
        {
            return result;
        }
        foreach (var q in list.EnumerateArray())
        {
            if (q.ValueKind != JsonValueKind.Object)
            {
                return new List<AskQuestion>();
            }
        }
        foreach (var q in list.EnumerateArray())
        {
            var multi = Get(q, "multiSelect");
            result.Add(new AskQuestion(
                Str(Get(q, "title")) ?? Str(Get(q, "question")) ?? string.Empty,
                Str(Get(q, "header")),
                OptionList(Get(q, "options")),
                multi.ValueKind is JsonValueKind.True or JsonValueKind.False && multi.GetBoolean()));
        }
        return result;
    }

    /// Option labels: all strings, or all {label} objects. Anything else gives none.
    private static IReadOnlyList<string> OptionList(JsonElement v)
    {
        if (v.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }
        var items = v.EnumerateArray().ToList();
        if (items.All(x => x.ValueKind == JsonValueKind.String))
        {
            return items.Select(x => x.GetString()!).ToList();
        }
        if (items.All(x => x.ValueKind == JsonValueKind.Object))
        {
            return items.Select(x => Str(Get(x, "label"))).OfType<string>().ToList();
        }
        return Array.Empty<string>();
    }

    /// The line parsed, or null when it is not complete JSON (a half-written line).
    private static JsonDocument? TryParse(string text)
    {
        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Slice(string s, int start, int length) => s.Substring(start, Math.Min(length, s.Length - start));

    private static JsonElement Get(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : default;

    private static string? Str(JsonElement e) => e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    private static double? Number(JsonElement e) => e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out var d) ? d : null;
}

/// Scans a Codex sessions folder (Electron: providers.js CodexScanner; Mac: Providers.swift CodexScanner). Only the rollouts
/// written lately are read, and each is read from where the last scan stopped, to its end, in chunks of ChunkBytes.
public sealed class CodexScanner
{
    private static readonly TimeSpan JobWindow = TimeSpan.FromHours(3);
    private static readonly TimeSpan IdleLimit = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan DoneLimit = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan LimitsWindow = TimeSpan.FromDays(7);
    private static readonly TimeSpan QuestionWindow = TimeSpan.FromHours(3);

    private readonly string _root;
    private readonly Dictionary<string, Entry> _cache = new(StringComparer.Ordinal);

    private sealed class Entry
    {
        public DateTimeOffset Mtime { get; set; }
        public long Offset { get; set; }
        public CodexStatus? Status { get; set; }
        public string Title { get; init; } = "Codex";
        public bool Child { get; init; }
        public string Thread { get; init; } = string.Empty;
        public CodexQuestion? Question { get; set; }
    }

    /// How much of a rollout is read at a time: 1 MB. A rollout is never read in one buffer, however large it is.
    public const int ChunkBytes = 1_048_576;

    private readonly Action<int>? _onRead;

    /// The folder to scan (for the app, <home>\.codex\sessions). Nothing is read until a method is called.
    /// onRead, when given, is told the size of every chunk read (the tests use it to check the reads stay bounded).
    public CodexScanner(string root, Action<int>? onRead = null)
    {
        _root = root;
        _onRead = onRead;
    }

    /// Tasks running now, or finished in the last 10 min. A rollout with no writes for 20 min is dead. Child threads are not tasks.
    public IReadOnlyList<Job> Jobs(DateTimeOffset now)
    {
        var jobs = new List<Job>();
        foreach (var (path, mtime) in WalkJsonl(_root))
        {
            if (now - mtime >= JobWindow)
            {
                continue;
            }
            if (!_cache.TryGetValue(path, out var cur) || cur.Mtime != mtime)
            {
                Update(path, mtime);
            }
            if (!_cache.TryGetValue(path, out var e) || now - mtime >= IdleLimit || e.Child || e.Status is null)
            {
                continue;
            }
            var age = (now - e.Status.At).TotalSeconds;
            var id = Path.GetFileName(path);
            if (e.Status.Working)
            {
                jobs.Add(new Job(id, e.Title, L10n.L("Running · {0}", Ago.Text(age)), true));
            }
            else if (age < DoneLimit.TotalSeconds)
            {
                jobs.Add(new Job(id, e.Title, L10n.L("Done · {0} ago", Ago.Text(age)), false));
            }
        }
        return jobs;
    }

    /// The newest rate_limits event across the rollouts written in the last 7 days (the newest file may have none).
    public ProviderLimits? Limits(DateTimeOffset now)
    {
        CodexLimitsFound? best = null;
        foreach (var (path, mtime) in WalkJsonl(_root))
        {
            if (now - mtime >= LimitsWindow)
            {
                continue;
            }
            if (best is not null && !(mtime > best.At))
            {
                continue;
            }
            var found = CodexRollout.ParseLimits(Providers.ReadTail(path), now);
            if (found is null || (best is not null && !(found.At > best.At)))
            {
                continue;
            }
            best = found;
        }
        return best is null
            ? null
            : new ProviderLimits("codex", "Codex", "com.openai.codex", best.Windows, best.At);
    }

    /// The questions still waiting on you, from threads written in the last 3 hours, as asks of the Codex agent. Jobs must
    /// have run first, since it reads the rollouts the last scan saw.
    public IReadOnlyList<AgentAsk> Questions(DateTimeOffset now)
    {
        var asks = new List<AgentAsk>();
        foreach (var e in _cache.Values)
        {
            if (e.Question is null || now - e.Question.At >= QuestionWindow)
            {
                continue;
            }
            var q = e.Question;
            // the id, tool and title as the Mac and main.js make them: a g-p- folder is the desktop app's own, shown as "Codex"
            asks.Add(new AgentAsk(
                $"codex|{q.Thread}|{q.CallId}",
                0,
                e.Title.StartsWith("g-p-", StringComparison.Ordinal) ? "Codex" : e.Title,
                "request_user_input",
                q.Questions[0].Question,
                q.Questions,
                q.At,
                AgentKind.Codex.Id()));
        }
        return asks;
    }

    /// Reads a rollout from where the last scan stopped, to its end, in chunks of ChunkBytes. A new rollout is read from its
    /// head. A file that was replaced (or cut short) is read from its start. A line that spans two chunks is carried over.
    private void Update(string file, DateTimeOffset mtime)
    {
        if (!_cache.TryGetValue(file, out var entry))
        {
            var head = Providers.ReadHead(file);
            if (head is null)
            {
                return;
            }
            // guardian and subagent threads start and finish while the parent works: they are not tasks of their own
            var (title, thread) = CodexRollout.TitleAndThread(head);
            entry = new Entry
            {
                Mtime = mtime,
                Offset = 0,
                Title = title,
                Thread = thread,
                Child = head.Contains("\"parent_thread_id\"", StringComparison.Ordinal),
            };
        }

        if (!entry.Child)
        {
            try
            {
                using var fs = File.OpenRead(file);
                var size = fs.Length;
                // the file was replaced or cut short: the saved offset is past its end, so start over
                var pos = entry.Offset <= size ? entry.Offset : 0;
                byte[] carry = Array.Empty<byte>();
                while (pos < size)
                {
                    var len = (int)Math.Min(ChunkBytes, size - pos);
                    _onRead?.Invoke(len);
                    var chunk = Providers.ReadRange(fs, pos, len);
                    if (chunk.Length == 0)
                    {
                        break;
                    }
                    pos += chunk.Length;
                    var data = new byte[carry.Length + chunk.Length];
                    carry.CopyTo(data, 0);
                    chunk.CopyTo(data, carry.Length);

                    // only complete lines are fed to the parsers; the rest waits for the next chunk (or the next scan)
                    var end = Array.LastIndexOf(data, (byte)'\n') + 1;
                    if (end > 0)
                    {
                        var text = Encoding.UTF8.GetString(data, 0, end);
                        entry.Status = CodexRollout.ParseStatus(text) ?? entry.Status;
                        entry.Question = CodexRollout.ParseQuestion(text, entry.Thread, entry.Question, DateTimeOffset.UtcNow);
                    }
                    // the offset is the byte after the last full line read, so a line still being written is read next time
                    entry.Offset = pos - (data.Length - end);
                    carry = data[end..];
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return;
            }
        }

        entry.Mtime = mtime;
        _cache[file] = entry;
    }

    /// Every .jsonl file under root, with its last write time. Folders that cannot be read are skipped.
    private static List<(string Path, DateTimeOffset Mtime)> WalkJsonl(string root)
    {
        var found = new List<(string Path, DateTimeOffset Mtime)>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            try
            {
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    pending.Push(sub);
                }
                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    if (!file.EndsWith(".jsonl", StringComparison.Ordinal))
                    {
                        continue;
                    }
                    try
                    {
                        var info = new FileInfo(file);
                        found.Add((file, new DateTimeOffset(info.LastWriteTimeUtc)));
                    }
                    catch (IOException)
                    {
                        // vanished since it was listed
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // a folder that cannot be listed is skipped
            }
        }
        return found;
    }
}
