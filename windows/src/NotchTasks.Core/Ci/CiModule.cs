using System.Text.Json.Nodes;
using NotchTasks.Core.Agents;
using NotchTasks.Core.Modules;
using NotchTasks.Core.Notch;

namespace NotchTasks.Core.Ci;

/// Everything the Build & CI module reads on one tick. The caller reads the OS; the module does not.
/// GitLabLogs is null on the ticks that do not read the runner logs: the last parse is kept (Mac: every 3rd tick).
/// Each log is (path, mtime in ms, tail of its text). Workers are the live Runner.Worker processes with the tail of their
/// log. ChildJob gives the CPU row of a gitlab-runner child (threshold 0.5, idle "Waiting"). Cwd gives a process's working
/// folder, or null. GitHubJobs are the open runs (GitHubWatcher.Jobs); GitHubFinished are the runs that ended since the last tick.
public sealed record CiInputs(
    ProcSnapshot Snapshot,
    IReadOnlyList<Job> DevJobs,
    IReadOnlyList<Job> FileJobs,
    IReadOnlyList<(string File, long MtimeMs, string Tail)>? GitLabLogs,
    IReadOnlyList<(int Pid, string? Exe, string? LogTail, string? LogFile)> Workers,
    IReadOnlyList<Job> GitHubJobs,
    IReadOnlyList<GhRun> GitHubFinished,
    Func<ProcInfo, string?> Cwd,
    Func<ProcInfo, Job> ChildJob,
    double DoneAfterSeconds);

/// The Build & CI module: local tasks, file transfers, GitLab runner jobs, GitHub self-hosted workers and GitHub runs, with
/// their toasts and failure alerts. Port of electron/src/main/main.js (scanWorkers, announceFinished, computeAll,
/// pollRunners, githubFinished) and the Mac's scanRunnerLog, githubFinished and build jobs (Model.swift). Update takes a
/// manual clock and makes no OS calls: state.json is read and written through the injected delegates.
public sealed class CiModule
{
    private const string DismissedKey = "dismissedJobs";
    private const int DismissedKeep = 1000;
    private const long RunningWindowMs = 5 * 60_000;   // a GitLab log silent for longer means the runner died mid-job
    private const long GoneMs = 20_000;                 // a job missing for one poll is still running
    private const string GreenTint = "#30D158";
    private const string RedTint = "#FF453A";
    private const string ModuleId = "ci";
    private const string GitLabTool = "GitLab Runner";
    private const string GitHubTool = "GitHub Actions";
    private const string ServerRackSymbol = "server.rack";

    private readonly IAnnouncer _announce;
    private readonly Func<string, JsonNode?> _stateGet;
    private readonly Action<string, JsonNode> _stateSet;
    private readonly Action<IEnumerable<string>> _seenTools;

    private Dictionary<string, LogParse> _logs = new(StringComparer.Ordinal);
    private IReadOnlyList<RunnerJob> _runnerJobs = Array.Empty<RunnerJob>();
    private readonly List<JobAlert> _runnerAlerts = new();
    private readonly List<JobAlert> _githubAlerts = new();
    private readonly Dictionary<int, string> _workerTails = new();
    private readonly Dictionary<string, Tracked> _working = new(StringComparer.Ordinal);
    private ItemState? _buildState;

    /// stateGet and stateSet read and write one key of state.json (StateJson). seenTools receives the tools of the build
    /// jobs (the app adds them to settings' seenTools).
    public CiModule(IAnnouncer announce, Func<string, JsonNode?> stateGet, Action<string, JsonNode> stateSet, Action<IEnumerable<string>> seenTools)
    {
        _announce = announce;
        _stateGet = stateGet;
        _stateSet = stateSet;
        _seenTools = seenTools;
    }

    /// The build jobs as one row (WatchItems.BuildItem), in the order dev, files, GitLab, GitHub, workers. Null when there are none.
    public ItemState? BuildState => _buildState;

    /// The failure alerts: GitHub's first (newest first), then the GitLab ones (newest first).
    public IReadOnlyList<JobAlert> Alerts => _githubAlerts.Concat(_runnerAlerts).ToList();

    /// Samples one tick: scans the logs when given, announces what finished, and rebuilds BuildState. Returns whether
    /// BuildState or Alerts changed.
    public bool Update(CiInputs input, long nowMs)
    {
        var beforeState = _buildState;
        var beforeAlerts = Alerts;

        if (input.GitLabLogs is { } logs)
        {
            ScanGitLab(logs, nowMs);
        }
        AnnounceGitHub(input.GitHubFinished);

        var jobs = new List<Job>();
        jobs.AddRange(input.DevJobs);
        jobs.AddRange(input.FileJobs);
        jobs.AddRange(GitLabRows(input));
        jobs.AddRange(input.GitHubJobs);
        jobs.AddRange(WorkerRows(input.Workers));

        _buildState = jobs.Count == 0 ? null : new ItemState(WatchItems.BuildItem, jobs);
        AnnounceFinished(jobs, input.DoneAfterSeconds, nowMs);

        var tools = jobs.Select(j => j.Tool).OfType<string>().Where(t => t.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        if (tools.Count > 0)
        {
            _seenTools(tools);
        }

        return !SameState(beforeState, _buildState) || !beforeAlerts.SequenceEqual(Alerts);
    }

    /// Hides a failure alert for good: its id joins dismissedJobs in state.json (the last 1000 are kept).
    public void Dismiss(string id)
    {
        _githubAlerts.RemoveAll(a => a.Id == id);
        _runnerAlerts.RemoveAll(a => a.Id == id);

        var ids = IdsOf(_stateGet(DismissedKey));
        ids.Add(id);
        var array = new JsonArray();
        foreach (var kept in ids.Skip(Math.Max(0, ids.Count - DismissedKeep)))
        {
            array.Add((JsonNode?)JsonValue.Create(kept));
        }
        _stateSet(DismissedKey, array);
    }

    /// Forgets the live state (alerts, rows, cached logs and workers), as when Build & CI is turned off. dismissedJobs
    /// in state.json stays.
    public void Clear()
    {
        _logs = new Dictionary<string, LogParse>(StringComparer.Ordinal);
        _runnerJobs = Array.Empty<RunnerJob>();
        _runnerAlerts.Clear();
        _githubAlerts.Clear();
        _workerTails.Clear();
        _working.Clear();
        _buildState = null;
    }

    /// Reads the GitLab logs: a log is parsed again only when its mtime changed. Running jobs come from the logs written in
    /// the last 5 min; a running job that left the list is announced by how it ended; failures are alerts unless dismissed.
    private void ScanGitLab(IReadOnlyList<(string File, long MtimeMs, string Tail)> logs, long nowMs)
    {
        var parsed = new Dictionary<string, LogParse>(StringComparer.Ordinal);
        var running = new List<RunnerJob>();
        var failed = new List<JobAlert>();
        var succeeded = new HashSet<string>(StringComparer.Ordinal);

        foreach (var log in logs)
        {
            var entry = _logs.TryGetValue(log.File, out var cached) && cached.MtimeMs == log.MtimeMs
                ? cached
                : ParseLog(log.MtimeMs, log.Tail);
            parsed[log.File] = entry;
            failed.AddRange(entry.Failed);
            succeeded.UnionWith(entry.Succeeded);
            if (nowMs - log.MtimeMs < RunningWindowMs)
            {
                running.AddRange(entry.Running);
            }
        }
        _logs = parsed;

        foreach (var gone in _runnerJobs)
        {
            if (running.Any(r => r.Id == gone.Id))
            {
                continue;
            }
            if (failed.Any(f => f.Id == gone.Id))
            {
                _announce.Announce(new Toast(
                    Glyph: "warning",
                    Tint: RedTint,
                    Title: L10n.L("CI failed: {0}", gone.Name),
                    Subtitle: gone.Project,
                    Event: "ciFailed",
                    Url: gone.Url,
                    Action: L10n.L("View log"),
                    Module: ModuleId));
            }
            else if (succeeded.Contains(gone.Id))
            {
                _announce.Announce(new Toast(
                    Glyph: "check",
                    Tint: GreenTint,
                    Title: L10n.L("CI passed: {0}", gone.Name),
                    Subtitle: gone.Project,
                    Event: "ciPassed",
                    Url: gone.Url,
                    Module: ModuleId));
            }
        }
        _runnerJobs = running;

        // failures already in the log when the app first looks are history, not news
        if (_stateGet(DismissedKey) is not JsonArray)
        {
            var array = new JsonArray();
            foreach (var f in failed)
            {
                array.Add((JsonNode?)JsonValue.Create(f.Id));
            }
            _stateSet(DismissedKey, array);
            _runnerAlerts.Clear();
            return;
        }

        var dismissed = IdsOf(_stateGet(DismissedKey)).ToHashSet(StringComparer.Ordinal);
        var alerts = new List<JobAlert>();
        foreach (var f in failed.AsEnumerable().Reverse())
        {
            if (!dismissed.Contains(f.Id) && alerts.All(a => a.Id != f.Id))
            {
                alerts.Add(f);
            }
        }
        _runnerAlerts.Clear();
        _runnerAlerts.AddRange(alerts);
    }

    private static LogParse ParseLog(long mtimeMs, string tail)
    {
        var (running, failed, succeeded) = RunnerLogs.ParseGitLab(tail);
        return new LogParse(mtimeMs, running, failed, succeeded);
    }

    /// The GitHub runs that ended: a toast either way; a failure is an alert until dismissed (newest first).
    private void AnnounceGitHub(IReadOnlyList<GhRun> finished)
    {
        if (finished.Count == 0)
        {
            return;
        }

        var dismissed = IdsOf(_stateGet(DismissedKey)).ToHashSet(StringComparer.Ordinal);
        foreach (var r in finished)
        {
            if (r.Conclusion == "success")
            {
                _announce.Announce(new Toast(
                    Glyph: "check",
                    Tint: GreenTint,
                    Title: L10n.L("CI passed: {0}", r.Workflow),
                    Subtitle: r.Repo,
                    Event: "ciPassed",
                    Url: r.Url,
                    Module: ModuleId));
            }
            else if (r.Conclusion is "failure" or "timed_out" or "startup_failure")
            {
                _announce.Announce(new Toast(
                    Glyph: "warning",
                    Tint: RedTint,
                    Title: L10n.L("CI failed: {0}", r.Workflow),
                    Subtitle: r.Repo,
                    Event: "ciFailed",
                    Url: r.Url,
                    Action: L10n.L("View log"),
                    Module: ModuleId));

                var id = $"gh-{r.Id}";
                if (!dismissed.Contains(id) && _githubAlerts.All(a => a.Id != id))
                {
                    _githubAlerts.Insert(0, new JobAlert(id, r.Workflow, r.Repo, string.IsNullOrEmpty(r.Title) ? "failure" : r.Title, r.Url));
                }
            }
        }
    }

    /// The GitLab rows. With running jobs (from the logs), each is paired in start order with a runner child for its CPU
    /// detail. Without, each runner child is a row, titled from its working folder (P3c-R5). No runner process, no rows.
    private IReadOnlyList<Job> GitLabRows(CiInputs input)
    {
        var snapshot = input.Snapshot;
        var topRunners = snapshot.ByNormName("gitlab-runner")
            .Where(p => !(snapshot.ById.TryGetValue(p.Ppid, out var parent) && ProcTable.NormName(parent.Name) == "gitlab-runner"))
            .ToList();
        if (topRunners.Count == 0)
        {
            return Array.Empty<Job>();
        }
        var children = topRunners.SelectMany(r => snapshot.Kids(r.Pid)).OrderBy(c => c.Pid).ToList();

        var rows = new List<Job>();
        if (_runnerJobs.Count > 0)
        {
            for (var i = 0; i < _runnerJobs.Count; i++)
            {
                var rj = _runnerJobs[i];
                var detail = i < children.Count
                    ? $"{rj.Name} · {input.ChildJob(children[i]).Detail}"
                    : rj.Name;
                rows.Add(new Job($"gl:{rj.Id}", rj.Project, detail, true, rj.Url, null, ServerRackSymbol, GitLabTool));
            }
        }
        else
        {
            foreach (var child in children)
            {
                var job = input.ChildJob(child);
                var cwd = input.Cwd(child);
                var title = (cwd is null ? null : RunnerLogs.BuildsProject(cwd)) ?? "GitLab";
                rows.Add(new Job($"gl:{child.Pid}", title, job.Detail, job.Working, null, null, ServerRackSymbol, GitLabTool));
            }
        }
        return rows;
    }

    /// The GitHub self-hosted worker rows, sorted by id. A worker that is gone toasts its result from its last log (P3c-R7).
    private IReadOnlyList<Job> WorkerRows(IReadOnlyList<(int Pid, string? Exe, string? LogTail, string? LogFile)> workers)
    {
        var live = new HashSet<int>();
        foreach (var w in workers)
        {
            live.Add(w.Pid);
            if (w.LogTail is not null)
            {
                _workerTails[w.Pid] = w.LogTail;
            }
            else
            {
                _workerTails.TryAdd(w.Pid, string.Empty);
            }
        }

        foreach (var pid in _workerTails.Keys.Where(pid => !live.Contains(pid)).ToList())
        {
            var tail = _workerTails[pid];
            _workerTails.Remove(pid);
            var result = RunnerLogs.WorkerResult(tail);
            if (result is null)
            {
                continue;
            }
            var job = WorkerJob(pid, tail);
            var ok = result == "Succeeded";
            _announce.Announce(new Toast(
                Glyph: ok ? "check" : "warning",
                Tint: ok ? GreenTint : RedTint,
                Title: (ok ? L10n.L("CI passed: ") : L10n.L("CI failed: ")) + job.Detail,
                Subtitle: job.Title,
                Event: ok ? "ciPassed" : "ciFailed",
                Module: ModuleId));
        }

        return live
            .Select(pid => WorkerJob(pid, _workerTails[pid]))
            .OrderBy(j => j.Id, StringComparer.Ordinal)
            .ToList();
    }

    private static Job WorkerJob(int pid, string tail)
    {
        var repo = RunnerLogs.RepoFromWorkerLog(tail);
        var name = RunnerLogs.WorkerJobName(tail);
        return new Job(
            Id: $"ghw:{pid}",
            Title: string.IsNullOrEmpty(repo) ? "GitHub runner" : repo,
            Detail: string.IsNullOrEmpty(name) ? "job" : name,
            Working: true,
            Url: null,
            Progress: null,
            Symbol: ServerRackSymbol,
            Tool: GitHubTool);
    }

    /// The done toasts of the build jobs: a job that was working and stopped, after at least DoneAfterSeconds, gone for
    /// 20 s. CI results (gl:, gh ids) are announced by their own toasts and skipped.
    private void AnnounceFinished(IReadOnlyList<Job> jobs, double doneAfterSeconds, long now)
    {
        foreach (var job in jobs)
        {
            if (!job.Working || IsCiResult(job.Id))
            {
                continue;
            }
            var since = _working.TryGetValue(job.Id, out var prev) ? prev.Since : now;
            _working[job.Id] = new Tracked(job.Title, since, now);
        }

        foreach (var id in _working.Keys.ToList())
        {
            var w = _working[id];
            if (now - w.LastSeen < GoneMs)
            {
                continue;
            }
            _working.Remove(id);
            var took = (w.LastSeen - w.Since) / 1000.0;
            if (took < doneAfterSeconds)
            {
                continue;
            }
            _announce.Announce(new Toast(
                Glyph: "check",
                Tint: GreenTint,
                Title: L10n.L("{0} done", w.Title),
                Subtitle: Ago.Text(took),
                Event: "taskDone",
                Module: ModuleId));
        }
    }

    private static bool IsCiResult(string id) => id.StartsWith("gl:", StringComparison.Ordinal) || id.StartsWith("gh", StringComparison.Ordinal);

    private static bool SameState(ItemState? a, ItemState? b) => a is null ? b is null : b is not null && a.Equals(b);

    /// The ids in a dismissedJobs value, in order. Empty when the value is absent or not a list.
    private static List<string> IdsOf(JsonNode? node)
    {
        var ids = new List<string>();
        if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item is JsonValue v && v.TryGetValue(out string? id) && id is not null)
                {
                    ids.Add(id);
                }
            }
        }
        return ids;
    }

    /// A parsed GitLab log: the mtime it was parsed at, and what it says.
    private sealed record LogParse(long MtimeMs, IReadOnlyList<RunnerJob> Running, IReadOnlyList<JobAlert> Failed, IReadOnlySet<string> Succeeded);

    /// A build job seen working: its title, when it started, and when it was last seen working (ms).
    private sealed record Tracked(string Title, long Since, long LastSeen);
}
