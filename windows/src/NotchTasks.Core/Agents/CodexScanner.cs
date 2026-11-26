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

/// One window as a rollout writes it: Used is the percent used and ResetSeconds the reset in Unix seconds, before any reset applies.
public sealed record RawLimitWindow(string Label, double Used, double ResetSeconds);

/// The newest rate_limits event of a rollout as written (see CodexRollout.ParseRawLimits).
public sealed record CodexLimitsRaw(DateTimeOffset At, IReadOnlyList<RawLimitWindow> Windows);

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
        var raw = ParseRawLimits(text);
        return raw is null ? null : ApplyLimits(raw, now);
    }

    /// The newest rate_limits event as written, before any reset is applied. It does not depend on the time it is read at,
    /// so the scanner can keep it for as long as the file does not change.
    public static CodexLimitsRaw? ParseRawLimits(string text)
    {
        var lines = text.Split('\n');
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
            var windows = new List<RawLimitWindow>();
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
                windows.Add(new RawLimitWindow(WindowLabel(mins), used.Value, resets.Value));
            }
            return new CodexLimitsRaw(at, windows);
        }
        return null;
    }

    /// The windows of a raw reading, at the time now: a window whose reset (epoch seconds) has passed has nothing used yet.
    public static CodexLimitsFound ApplyLimits(CodexLimitsRaw raw, DateTimeOffset now)
    {
        var nowSeconds = now.ToUnixTimeMilliseconds() / 1000.0;
        return new CodexLimitsFound(raw.At, raw.Windows
            .Select(w => new LimitWindow(
                w.Label,
                w.ResetSeconds > nowSeconds ? w.Used : 0,
                DateTimeOffset.FromUnixTimeMilliseconds((long)(w.ResetSeconds * 1000))))
            .ToList());
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
    /// The raw limits of each rollout, keyed by path, for the mtime and size it had when it was read. An unchanged file is not read again.
    private readonly Dictionary<string, (DateTimeOffset Mtime, long Size, CodexLimitsRaw? Raw)> _limitsCache = new(StringComparer.Ordinal);

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
    private readonly Action<string>? _onFolder;

    /// The folder to scan (for the app, <home>\.codex\sessions). Nothing is read until a method is called.
    /// onRead, when given, is told the size of every chunk read (the tests use it to check the reads stay bounded).
    /// onFolder, when given, is told the path of every folder the scan lists (the tests use it to check every folder is walked).
    public CodexScanner(string root, Action<int>? onRead = null, Action<string>? onFolder = null)
    {
        _root = root;
        _onRead = onRead;
        _onFolder = onFolder;
    }

    /// Tasks running now, or finished in the last 10 min. A rollout with no writes for 20 min is dead. Child threads are not tasks.
    public IReadOnlyList<Job> Jobs(DateTimeOffset now)
    {
        var jobs = new List<Job>();
        foreach (var (path, mtime, _) in WalkJsonl(now))
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
        var listed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (path, mtime, size) in WalkJsonl(now))
        {
            if (now - mtime >= LimitsWindow)
            {
                continue;
            }
            listed.Add(path);
            if (!_limitsCache.TryGetValue(path, out var cached) || cached.Mtime != mtime || cached.Size != size)
            {
                // a rollout older than the newest reading found so far cannot change the answer: it is not read
                if (best is not null && !(mtime > best.At))
                {
                    continue;
                }
                var text = Providers.ReadTail(path);
                _onRead?.Invoke((int)Math.Min(size, Providers.TailBytes));
                cached = (mtime, size, CodexRollout.ParseRawLimits(text));
                _limitsCache[path] = cached;
            }
            var found = cached.Raw is null ? null : CodexRollout.ApplyLimits(cached.Raw, now);
            if (found is null || (best is not null && !(found.At > best.At)))
            {
                continue;
            }
            best = found;
        }
        foreach (var gone in _limitsCache.Keys.Where(k => !listed.Contains(k)).ToList())
        {
            _limitsCache.Remove(gone);
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
                using var fs = Providers.OpenShared(file);
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

    /// The write time of a rollout as the listing gives it, and the time and size from an open handle (see Refine) for a rollout
    /// written within LimitsWindow, since Windows keeps the listing stale while the writer holds the file. Callers filter by each
    /// file's mtime. The listing's values stay when the handle cannot be opened. Every folder is entered, whatever its date: a
    /// rollout is written in the folder of the day its thread started, so a thread resumed days later keeps writing there (the
    /// Mac walks every folder too). Folders that cannot be read are skipped.
    private List<(string Path, DateTimeOffset Mtime, long Size)> WalkJsonl(DateTimeOffset now)
    {
        var found = new List<(string Path, DateTimeOffset Mtime, long Size)>();
        var pending = new Stack<string>();
        pending.Push(_root);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            _onFolder?.Invoke(dir);
            try
            {
                foreach (var info in new DirectoryInfo(dir).EnumerateFileSystemInfos())
                {
                    if (info is DirectoryInfo sub)
                    {
                        pending.Push(sub.FullName);
                    }
                    else if (info is FileInfo file && file.Name.EndsWith(".jsonl", StringComparison.Ordinal))
                    {
                        try
                        {
                            var mtime = new DateTimeOffset(file.LastWriteTimeUtc);
                            var size = file.Length;
                            if (now - mtime < LimitsWindow)
                            {
                                (mtime, size) = HandleStamp(file.FullName, mtime, size);
                            }
                            found.Add((file.FullName, mtime, size));
                        }
                        catch (IOException)
                        {
                            // vanished since it was listed
                        }
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

    /// The time and size of a rollout the writer may hold open: its write time from the open handle, which is current when the
    /// directory entry is not. The listing's values when the handle cannot be opened.
    private static (DateTimeOffset Mtime, long Size) HandleStamp(string path, DateTimeOffset listed, long listedSize)
    {
        try
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Refine(listed, new DateTimeOffset(File.GetLastWriteTimeUtc(handle)), RandomAccess.GetLength(handle));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (listed, listedSize);
        }
    }

    /// The stamp of a rollout from its listing and its open handle: the later of the two write times, and the handle's size.
    internal static (DateTimeOffset Mtime, long Size) Refine(DateTimeOffset listed, DateTimeOffset handleTime, long handleSize) =>
        (handleTime > listed ? handleTime : listed, handleSize);
}
