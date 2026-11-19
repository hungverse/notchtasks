using System.Text;
using System.Text.Json;

namespace NotchTasks.Core.Agents;

/// Reads one Antigravity conversation through sqlite3. The app runs the query (sqlite3 -readonly -json, 2 s, found through
/// AgentSetup.FindCli); with no sqlite3 there are no Antigravity jobs (ruling P3b-R4).
public interface ISqliteReader
{
    /// The rows of a read-only query on dbFile, as sqlite3 -json prints them ("" when there are none). Null when the query
    /// failed, timed out, or there is no sqlite3.
    string? Query(string dbFile, string sql);
}

/// A conversation's state: Active while its last step is mid-turn, and the workspace folder it is in.
public sealed record AntigravityState(bool Active, string Title);

/// The pure parts of an Antigravity conversation (Electron: providers.js antigravityMidTurn, antigravityProject,
/// readAntigravityConversation, antigravityStamp; Mac: AntigravityScanner.swift midTurn, project, stamp, read).
public static class AntigravityConversation
{
    private const string LastStepSql = "select step_type, status from steps order by idx desc limit 1";
    private const string MetadataSql = "select hex(data) as h from trajectory_metadata_blob limit 1";
    private const string ProjectMarker = "file:///";

    /// Mid-turn: a step still in progress (pending, running, generating, waiting, queued), or a finished tool step the model
    /// has yet to answer. A turn ends on a finished reply (type 15, rarely 17); 6 and 7 appear after a stop or an error.
    public static bool MidTurn(int type, int status) =>
        status is 1 or 2 or 8 or 9 or 11 || (status == 3 && type is not (15 or 17));

    /// The workspace folder's last name, from the conversation's protobuf metadata. Its strings carry their length as a varint,
    /// and the byte after one can be printable. Null when there is no file:/// uri or it does not parse.
    public static string? Project(byte[] blob)
    {
        var at = blob.AsSpan().IndexOf(Encoding.UTF8.GetBytes(ProjectMarker));
        if (at <= 0)
        {
            return null;
        }
        int low = blob[at - 1];
        int high = at - 2 >= 0 ? blob[at - 2] : 0;
        var count = high >= 0x80 ? (high & 0x7f) | (low << 7) : low; // 128+ takes two varint bytes
        if (at + count > blob.Length)
        {
            return null;
        }
        try
        {
            var uri = new Uri(Encoding.UTF8.GetString(blob, at, count));
            var last = Uri.UnescapeDataString(uri.AbsolutePath).Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            return string.IsNullOrEmpty(last) ? null : last;
        }
        catch (UriFormatException)
        {
            return null;
        }
    }

    /// When the conversation last changed: its file, or its write-ahead log when that has data. Null when neither exists.
    public static DateTimeOffset? Stamp(string db)
    {
        var stamps = new List<DateTimeOffset>();
        try
        {
            var file = new FileInfo(db);
            if (file.Exists)
            {
                stamps.Add(new DateTimeOffset(file.LastWriteTimeUtc));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // no stamp from the file
        }
        try
        {
            var wal = new FileInfo(db + "-wal");
            if (wal.Exists && wal.Length > 0)
            {
                stamps.Add(new DateTimeOffset(wal.LastWriteTimeUtc));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // no stamp from the log
        }
        return stamps.Count == 0 ? null : stamps.Max();
    }

    /// Reads a conversation: its last step decides Active, its metadata gives the title ("Antigravity" when there is none).
    /// Null when the query fails or the file holds no steps (a file that is not a conversation).
    public static AntigravityState? Read(string file, ISqliteReader db)
    {
        try
        {
            using var steps = Run(db, file, LastStepSql);
            if (steps is null || steps.RootElement.ValueKind != JsonValueKind.Array || steps.RootElement.GetArrayLength() == 0)
            {
                return null;
            }
            var last = steps.RootElement[0];
            var type = JsonFields.Int(JsonFields.Field(last, "step_type")) ?? -1;
            var status = JsonFields.Int(JsonFields.Field(last, "status")) ?? -1;

            using var meta = Run(db, file, MetadataSql);
            if (meta is null)
            {
                return null;
            }
            var hex = meta.RootElement.ValueKind == JsonValueKind.Array && meta.RootElement.GetArrayLength() > 0
                ? JsonFields.Str(JsonFields.Field(meta.RootElement[0], "h"))
                : null;
            return new AntigravityState(MidTurn(type, status), ProjectFromHex(hex) ?? "Antigravity");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// The query's rows as JSON. Null when the query failed; an empty array when it printed nothing.
    private static JsonDocument? Run(ISqliteReader db, string file, string sql)
    {
        var text = db.Query(file, sql);
        if (text is null)
        {
            return null;
        }
        return JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "[]" : text);
    }

    private static string? ProjectFromHex(string? hex)
    {
        if (string.IsNullOrEmpty(hex))
        {
            return null;
        }
        try
        {
            return Project(Convert.FromHexString(hex));
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

/// Scans the Antigravity conversations folder (Electron: providers.js AntigravityScanner; Mac: AntigravityScanner.swift).
/// Only conversations written in the last 10 min are read, and a conversation is read again only when its stamp changes.
public sealed class AntigravityScanner
{
    private static readonly TimeSpan JobWindow = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan HoldWindow = TimeSpan.FromSeconds(45);

    private readonly string _root;
    private readonly ISqliteReader _db;
    private readonly Dictionary<string, Cached> _cache = new(StringComparer.Ordinal);
    private Dictionary<string, Run> _runs = new(StringComparer.Ordinal);

    private sealed record Cached(DateTimeOffset Stamp, bool Active, string Title);

    private sealed record Run(DateTimeOffset Since, DateTimeOffset Seen);

    /// The folder of conversations (for the app, <home>\.gemini\antigravity\conversations). Nothing is read until Jobs runs.
    public AntigravityScanner(string root, ISqliteReader db)
    {
        _root = root;
        _db = db;
    }

    /// The conversations running now. One that was running is held for 45 s after its last write, so a reply that calls a
    /// tool does not look finished while its tool step is still to be written.
    public IReadOnlyList<Job> Jobs(DateTimeOffset now)
    {
        var jobs = new List<Job>();
        var live = new Dictionary<string, Run>(StringComparer.Ordinal);
        foreach (var db in DbFiles(_root))
        {
            var name = Path.GetFileName(db);
            // a step left "generating" by a crash stays that way: only a conversation written lately counts
            var stamp = AntigravityConversation.Stamp(db);
            if (stamp is null || now - stamp.Value >= JobWindow)
            {
                continue;
            }
            var at = stamp.Value;

            Cached? entry = _cache.TryGetValue(name, out var found) ? found : null;
            if (entry is null || entry.Stamp != at)
            {
                var state = AntigravityConversation.Read(db, _db);
                if (state is not null)
                {
                    entry = new Cached(at, state.Active, state.Title);
                    _cache[name] = entry;
                }
            }
            if (entry is null)
            {
                continue;
            }

            Run? run = _runs.TryGetValue(name, out var seen) ? seen : null;
            // only a conversation already seen working is held, so opening an old one does not count
            var held = run is not null && now - Latest(run.Seen, at) < HoldWindow;
            if (!(entry.Active || held))
            {
                continue;
            }
            var since = run?.Since ?? now;
            live[name] = new Run(since, entry.Active ? now : (run?.Seen ?? now));
            jobs.Add(new Job(name, entry.Title, L10n.L("Running · {0}", Ago.Text((now - since).TotalSeconds)), true));
        }
        _runs = live;
        return jobs;
    }

    private static DateTimeOffset Latest(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    /// The .db files in the folder, by name. A folder that cannot be listed (or is missing) has none.
    private static List<string> DbFiles(string root)
    {
        try
        {
            return Directory.EnumerateFiles(root)
                .Where(f => f.EndsWith(".db", StringComparison.Ordinal))
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new List<string>();
        }
    }
}
