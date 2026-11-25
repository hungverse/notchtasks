using System.Text.Json.Nodes;
using NotchTasks.Core.Agents;
using NotchTasks.Core.Modules;
using NotchTasks.Core.Notch;

namespace NotchTasks.Core.Ci;

/// Everything the Build & CI module reads on one tick. The caller reads the OS; the module does not.
/// GitLabLogs is null on the ticks that do not read the runner logs: the last parse is kept (Mac: every 3rd tick).
/// Each log is (path, mtime in ms, tail of its text). Workers are the live Runner.Worker processes with their log file;
/// the module reads a worker's log itself, through the readTail it was given (see CiModule). ChildJob gives the CPU row
/// of a gitlab-runner child (threshold 0.5, idle "Waiting"). Cwd gives a process's working folder, or null. GitHubJobs are
/// the open runs (GitHubWatcher.Jobs); GitHubFinished are the runs that ended since the last tick.
public sealed record CiInputs(
    ProcSnapshot Snapshot,
    IReadOnlyList<Job> DevJobs,
    IReadOnlyList<Job> FileJobs,
    IReadOnlyList<(string File, long MtimeMs, string Tail)>? GitLabLogs,
    IReadOnlyList<(int Pid, string? Exe, string? LogFile)> Workers,
    IReadOnlyList<Job> GitHubJobs,
    IReadOnlyList<GhRun> GitHubFinished,
    Func<ProcInfo, string?> Cwd,
    Func<ProcInfo, Job> ChildJob,
    double DoneAfterSeconds);

/// The Build & CI module: local tasks, file transfers, GitLab runner jobs, GitHub self-hosted workers and GitHub runs, with
/// their toasts and failure alerts. Port of electron/src/main/main.js (scanWorkers, announceFinished, computeAll,
/// pollRunners, githubFinished) and the Mac's scanRunnerLog, githubFinished and build jobs (Model.swift). Update takes a
/// manual clock and makes no OS calls: state.json is read and written through the injected delegates, and a worker's log
/// through readTail.
public sealed class CiModule
{
    private const string DismissedKey = "dismissedJobs";
    private const int DismissedKeep = 1000;
    private const long RunningWindowMs = 5 * 60_000;   // a GitLab log silent for longer means the runner died mid-job
    private const long GoneMs = 20_000;                 // a job missing for one poll is still running
    private const int WorkerNameEvery = 10;             // Mac: a worker whose job name is unknown is read every 10th tick
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
    private readonly Func<string, string?> _readTail;

    private Dictionary<string, LogParse> _logs = new(StringComparer.Ordinal);
    private IReadOnlyList<RunnerJob> _runnerJobs = Array.Empty<RunnerJob>();
    private readonly List<JobAlert> _runnerAlerts = new();
    private readonly List<JobAlert> _githubAlerts = new();
    private readonly Dictionary<int, WorkerTrack> _workers = new();
    private readonly Dictionary<string, Tracked> _working = new(StringComparer.Ordinal);
    private HashSet<string> _lastTools = new(StringComparer.Ordinal);
    private ItemState? _buildState;
    private List<JobAlert> _alerts = new();   // the union of the two alert lists, rebuilt only when one of them changed
    private bool _alertsDirty;
    private long _updates;
    private List<string>? _dismissed;   // dismissedJobs: read from state.json once, then kept here and written through on change
    private bool _dismissedInFile;      // the key was in state.json when it was read; an absent key means a first run

    /// stateGet and stateSet read and write one key of state.json (StateJson). seenTools receives the tools of the build
    /// jobs when that set changes (the app adds them to settings' seenTools). readTail reads the last 2 000 000 bytes of a
    /// worker's log file (the app passes RunnerLogs.ReadTail).
    public CiModule(
        IAnnouncer announce,
        Func<string, JsonNode?> stateGet,
        Action<string, JsonNode> stateSet,
        Action<IEnumerable<string>> seenTools,
        Func<string, string?> readTail)
    {
        _announce = announce;
        _stateGet = stateGet;
        _stateSet = stateSet;
        _seenTools = seenTools;
        _readTail = readTail;
    }

    /// The build jobs as one row (WatchItems.BuildItem), in the order dev, files, GitLab, GitHub, workers. Null when there are none.
    public ItemState? BuildState => _buildState;

    /// True while a GitLab job is shown from the runner logs (its log lines are what keeps the logs being read, P3c M1).
    public bool HasRunnerJobs => _runnerJobs.Count > 0;

    /// The live GitHub workers whose job name is still unknown (their log has no job name yet): CiSource looks their log up
    /// again only every 10th tick while they are in this list (P3c M4).
    public IReadOnlyList<int> UnnamedWorkerPids =>
        _workers.Where(kv => !HasJobName(kv.Value.Tail)).Select(kv => kv.Key).ToList();

    /// The failure alerts: GitHub's first (newest first), then the GitLab ones (newest first). The same list object is
    /// returned until an alert changes.
    public IReadOnlyList<JobAlert> Alerts
    {
        get
        {
            if (_alertsDirty)
            {
                _alerts = _githubAlerts.Concat(_runnerAlerts).ToList();
                _alertsDirty = false;
            }
            return _alerts;
        }
    }

    /// Samples one tick: scans the logs when given, announces what finished, and rebuilds BuildState. Returns whether
    /// BuildState or Alerts changed.
    public bool Update(CiInputs input, long nowMs)
    {
        _updates++;
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

        var tools = new HashSet<string>(
            jobs.Select(j => j.Tool).OfType<string>().Where(t => t.Length > 0),
            StringComparer.Ordinal);
        if (!tools.SetEquals(_lastTools))
        {
            _lastTools = tools;
            if (tools.Count > 0)
            {
                _seenTools(tools.ToList());
            }
        }

        return !SameState(beforeState, _buildState) || !beforeAlerts.SequenceEqual(Alerts);
    }

    /// Hides a failure alert for good: its id joins dismissedJobs in state.json (the last 1000 are kept).
    public void Dismiss(string id)
    {
        var removed = _githubAlerts.RemoveAll(a => a.Id == id) + _runnerAlerts.RemoveAll(a => a.Id == id);
        if (removed > 0)
        {
            _alertsDirty = true;
        }

        Dismissed().Add(id);
        WriteDismissed();
    }

    /// The dismissed ids, read from state.json the first time they are needed.
    private List<string> Dismissed()
    {
        if (_dismissed is null)
        {
            var node = _stateGet(DismissedKey);
            _dismissedInFile = node is JsonArray;
            _dismissed = IdsOf(node);
        }
        return _dismissed;
    }

    /// Writes the dismissed ids to state.json, keeping the last 1000.
    private void WriteDismissed()
    {
        var ids = Dismissed();
        if (ids.Count > DismissedKeep)
        {
            ids.RemoveRange(0, ids.Count - DismissedKeep);
        }
        var array = new JsonArray();
        foreach (var id in ids)
        {
            array.Add((JsonNode?)JsonValue.Create(id));
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
        _alertsDirty = true;
        _workers.Clear();
        _working.Clear();
        _lastTools.Clear();
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
                    Glyph: "octagon",
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
                    Glyph: "seal",
                    Tint: GreenTint,
                    Title: L10n.L("CI passed: {0}", gone.Name),
                    Subtitle: gone.Project,
                    Event: "ciPassed",
                    Url: gone.Url,
                    Module: ModuleId));
            }
        }
        _runnerJobs = running;

        var dismissed = Dismissed();
        if (!_dismissedInFile)
        {
            // failures already in the log when the app first looks are history, not news
            _dismissedInFile = true;
            dismissed.Clear();
            dismissed.AddRange(failed.Select(f => f.Id));
            WriteDismissed();
            SetRunnerAlerts(new List<JobAlert>());
            return;
        }

        var alerts = new List<JobAlert>();
        foreach (var f in failed.AsEnumerable().Reverse())
        {
            if (!dismissed.Contains(f.Id) && alerts.All(a => a.Id != f.Id))
            {
                alerts.Add(f);
            }
        }
        SetRunnerAlerts(alerts);
    }

    /// Replaces the GitLab alerts, marking the alert list dirty only when they differ.
    private void SetRunnerAlerts(List<JobAlert> alerts)
    {
        if (alerts.SequenceEqual(_runnerAlerts))
        {
            return;
        }
        _runnerAlerts.Clear();
        _runnerAlerts.AddRange(alerts);
        _alertsDirty = true;
    }

    private static LogParse ParseLog(long mtimeMs, string tail)
    {
        var (running, failed, succeeded) = RunnerLogs.ParseGitLab(tail);
        return new LogParse(mtimeMs, running, failed, succeeded);
    }

    /// The GitHub runs that ended: a toast either way; a failure is an alert until dismissed (newest first). A run already
    /// dismissed still toasts, but gives no alert.
    private void AnnounceGitHub(IReadOnlyList<GhRun> finished)
    {
        if (finished.Count == 0)
        {
            return;
        }

        var dismissed = Dismissed();
        foreach (var r in finished)
        {
            if (r.Conclusion == "success")
            {
                _announce.Announce(new Toast(
                    Glyph: "seal",
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
                    Glyph: "octagon",
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
                    _alertsDirty = true;
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

    /// The GitHub self-hosted worker rows, sorted by id. The log is read when a worker is first seen, every 10th update
    /// while its job name is still unknown, and once more when it is gone, for its result (P3c-R7 toast, no URL).
    private IReadOnlyList<Job> WorkerRows(IReadOnlyList<(int Pid, string? Exe, string? LogFile)> workers)
    {
        var live = new HashSet<int>();
        foreach (var w in workers)
        {
            live.Add(w.Pid);
            if (!_workers.TryGetValue(w.Pid, out var track))
            {
                _workers[w.Pid] = new WorkerTrack(w.LogFile, ReadTail(w.LogFile));
                continue;
            }
            track.LogFile = w.LogFile ?? track.LogFile;
            if (_updates % WorkerNameEvery == 0 && !HasJobName(track.Tail))
            {
                track.Tail = ReadTail(track.LogFile);
            }
        }

        foreach (var pid in _workers.Keys.Where(p => !live.Contains(p)).ToList())
        {
            var track = _workers[pid];
            _workers.Remove(pid);
            var tail = track.LogFile is null ? track.Tail : ReadTail(track.LogFile);
            var result = RunnerLogs.WorkerResult(tail);
            if (result is null)
            {
                continue;
            }
            var job = WorkerJob(pid, tail);
            var ok = result == "Succeeded";
            _announce.Announce(new Toast(
                Glyph: ok ? "seal" : "octagon",
                Tint: ok ? GreenTint : RedTint,
                Title: (ok ? L10n.L("CI passed: ") : L10n.L("CI failed: ")) + job.Detail,
                Subtitle: job.Title,
                Event: ok ? "ciPassed" : "ciFailed",
                Module: ModuleId));
        }

        return live
            .Select(pid => WorkerJob(pid, _workers[pid].Tail))
            .OrderBy(j => j.Id, StringComparer.Ordinal)
            .ToList();
    }

    /// The log text of a worker's file, or "" when there is no file or the read gives nothing.
    private string ReadTail(string? file) => file is null ? string.Empty : _readTail(file) ?? string.Empty;

    private static bool HasJobName(string tail) => !string.IsNullOrEmpty(RunnerLogs.WorkerJobName(tail));

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
                Module: ModuleId,
                ToPhone: true));
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

    /// A live or just-gone worker: its log file and the last log text read from it.
    private sealed class WorkerTrack
    {
        public WorkerTrack(string? logFile, string tail)
        {
            LogFile = logFile;
            Tail = tail;
        }

        public string? LogFile { get; set; }

        public string Tail { get; set; }
    }
}
