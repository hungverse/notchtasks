using System.Globalization;
using System.Text.Json;

namespace NotchTasks.Core.Agents;

/// One Claude Code session from <claude dir>\sessions\<pid>.json, written by Claude Code itself ("busy" while a turn runs).
public sealed record ClaudeSession(int Pid, string? Name, string? Cwd, string? Status, long? StatusUpdatedAt);

/// Port of the Claude Code half of electron/src/core/providers.js (claudeDir, claudeSessions) and of main.js claudeJobs.
public static class ClaudeSessions
{
    private const string ClaudeName = "claude";

    /// The Claude config folder: configDir when it is set (CLAUDE_CONFIG_DIR), else <home>\.claude.
    public static string Dir(string home, string? configDir) =>
        string.IsNullOrEmpty(configDir) ? Path.Combine(home, ".claude") : configDir;

    /// The sessions in <dir>\sessions. Files that are not JSON, or have no integer pid, are skipped. Empty when the folder is missing.
    public static IReadOnlyList<ClaudeSession> Read(string dir)
    {
        var folder = Path.Combine(dir, "sessions");
        var files = new List<string>();
        try
        {
            foreach (var f in Directory.EnumerateFiles(folder))
            {
                if (f.EndsWith(".json", StringComparison.Ordinal))
                {
                    files.Add(f);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<ClaudeSession>();
        }

        var list = new List<ClaudeSession>();
        foreach (var file in files)
        {
            var session = ReadSession(file);
            if (session is not null)
            {
                list.Add(session);
            }
        }
        return list;
    }

    /// The sessions whose process is alive and named "claude", as jobs. The title is the session name, else the last part of
    /// the folder, else "Claude". A busy session is working; its detail says how long ago its status changed.
    public static IReadOnlyList<Job> Jobs(IReadOnlyList<ClaudeSession> sessions, ProcSnapshot procs, long nowMs)
    {
        var jobs = new List<Job>();
        foreach (var s in sessions)
        {
            if (!procs.ById.TryGetValue(s.Pid, out var live) || ProcTable.NormName(live.Name) != ClaudeName)
            {
                continue;
            }
            var busy = s.Status == "busy";
            var sinceSeconds = Math.Max(0, (nowMs - (s.StatusUpdatedAt ?? nowMs)) / 1000.0);
            var folder = string.IsNullOrEmpty(s.Cwd) ? string.Empty : Providers.LastComponent(s.Cwd);
            var title = !string.IsNullOrEmpty(s.Name) ? s.Name : folder.Length > 0 ? folder : "Claude";
            var detail = busy ? L10n.L("Working · {0}", Ago.Text(sinceSeconds)) : L10n.L("Waiting");
            jobs.Add(new Job(s.Pid.ToString(CultureInfo.InvariantCulture), title, detail, busy));
        }
        return jobs;
    }

    /// One session file, or null when it cannot be read, is not an object, or has no integer pid.
    private static ClaudeSession? ReadSession(string file)
    {
        try
        {
            using var doc = JsonDocument.Parse(Providers.ReadAllTextShared(file));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            var pid = Get(root, "pid");
            if (pid.ValueKind != JsonValueKind.Number || !pid.TryGetInt32(out var id))
            {
                return null;
            }
            var updated = Get(root, "statusUpdatedAt");
            long? updatedAt = updated.ValueKind == JsonValueKind.Number && updated.TryGetDouble(out var ms) ? (long)ms : null;
            return new ClaudeSession(id, Str(Get(root, "name")), Str(Get(root, "cwd")), Str(Get(root, "status")), updatedAt);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// The property, or a default element (ValueKind Undefined) when the object has none.
    private static JsonElement Get(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : default;

    private static string? Str(JsonElement e) => e.ValueKind == JsonValueKind.String ? e.GetString() : null;
}
