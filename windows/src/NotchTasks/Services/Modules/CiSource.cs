using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using NotchTasks.Core;
using NotchTasks.Core.Agents;
using NotchTasks.Core.Ci;
using NotchTasks.Core.Modules;
using NotchTasks.Core.Settings;

namespace NotchTasks.Services.Modules;

/// What the Build & CI feed reads (AppStateOptions.Ci): the process table (with the working folders), GitHub, the user's profile
/// folder (Home, where the file transfers and gh are found), ProgramData (the GitLab logs), the GitLab fallback folder, the GitHub
/// runner folder, and the clock in milliseconds.
public sealed record CiReaders(
    IProcessReader Processes,
    IGitHubApi GitHub,
    string Home,
    string ProgramData,
    string GitLabFallbackDir,
    string RunnerDir,
    Func<long> Clock)
{
    /// This PC's readers: the process table, GitHub through gh's token, and the real folders.
    public static CiReaders Production(IProcessReader processes, string home) => new(
        processes,
        new GitHubApi(home),
        home,
        Environment.GetEnvironmentVariable("ProgramData") ?? string.Empty,
        @"C:\GitLab-Runner",
        RunnerLogs.DefaultRunnerDir,
        () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
}

/// The state.json keys of Build & CI (taskHistory, dismissedJobs). StateJson holds the lock, so the scan's history writes on
/// the background and the module's dismissed ids on the UI thread are safe without a lock here.
public static class CiState
{
    public static JsonNode? Get(string key) => StateJson.Get(AppPaths.StateFile, key);

    public static void Set(string key, JsonNode? value) => StateJson.Set(AppPaths.StateFile, key, value);
}

/// The Build & CI feed (module "ci"). While it is started, a 2 s DispatcherTimer reads the process table, the dev tasks, the
/// file transfers, the runner logs and GitHub, off the UI thread and one read at a time, and hands the result to CiModule.Update
/// and to the build AgentsModule.Update on the UI thread. Cadences (P3c-R4): the runner logs every 3rd tick, only while a
/// gitlab-runner process exists or a GitLab job is shown, and a log is read again only when its mtime changed; GitHub every 10th
/// tick, only while a repo is watched. A git push starts watching its repo (DevScanner.Pushed). Stop stops the timer and clears
/// the module; a read in flight is dropped when it lands.
public sealed class CiSource : IModuleSource
{
    private static readonly TimeSpan TickEvery = TimeSpan.FromSeconds(2);
    private const int GitLabEvery = 3;
    private const int WorkerNameEvery = 10;
    private const int GitHubEvery = 10;
    private const int GitLabTail = 512_000;
    private const double ChildThreshold = 0.5;   // % CPU of a gitlab-runner child's tree that counts as working
    private const string GitLabTool = "GitLab Runner";
    private const string WaitingWord = "Waiting";

    private readonly CiModule _module;
    private readonly AgentsModule _buildAgents;
    private readonly SettingsStore _settings;
    private readonly CiReaders _reads;
    private readonly Action _publish;
    private readonly CpuMeter _cpu = new();        // the gitlab-runner children, sampled by the scan
    private readonly CpuMeter _buildCpu = new();   // the build watch items, sampled by AgentsModule.Update
    private readonly DispatcherTimer _timer = new() { Interval = TickEvery };
    private bool _started;
    private bool _busy;
    private int _generation;  // every Start and Stop: a read that started under an older one is dropped when it lands
    private int _tick;
    private DevScanner? _dev;
    private FileProgressWatcher? _files;
    private GitHubFeed? _feed;
    private Dictionary<string, long> _logMtimes = new(StringComparer.Ordinal);
    private Dictionary<int, WorkerLog> _workerLogs = new();

    /// module gets the rows, alerts and toasts; buildAgents gets the build watch items; publish is called when either changed.
    public CiSource(CiModule module, AgentsModule buildAgents, SettingsStore settings, CiReaders reads, Action publish)
    {
        _module = module;
        _buildAgents = buildAgents;
        _settings = settings;
        _reads = reads;
        _publish = publish;
        _timer.Tick += (_, _) => Tick();
    }

    public void Start()
    {
        _generation++;
        _started = true;
        _tick = 0;
        var feed = new GitHubFeed(new GitHubWatcher(_reads.GitHub, _reads.Clock));
        var dev = new DevScanner(new TaskHistory(LoadHistory(), SaveHistory, ErrorLog.Write));
        dev.Pushed += dir => feed.EnqueuePush(dir);   // the poll hands pushes to the watcher, off the tick
        _dev = dev;
        _feed = feed;
        _files = new FileProgressWatcher(FileProgressWatcher.DefaultFolders(_reads.Home), _reads.Clock);
        _logMtimes = new Dictionary<string, long>(StringComparer.Ordinal);
        _workerLogs = new Dictionary<int, WorkerLog>();
        if (!_timer.IsEnabled)
        {
            _timer.Start();
        }
    }

    /// Stops the timer and clears the module and the build rows. A read still in flight is dropped when it lands.
    public void Stop()
    {
        _generation++;
        _started = false;
        _timer.Stop();
        if (!_busy)
        {
            _files?.Stop();   // a read in flight still owns the watcher; it is dropped with its generation
        }
        _files = null;
        _dev = null;
        _feed = null;
        _module.Clear();
        ClearBuild();
    }

    /// One timer tick: the reads start in the background; the result is applied here, on the UI thread.
    private async void Tick()
    {
        if (_busy || !_started)
        {
            return;
        }
        _busy = true;
        try
        {
            var request = MakeRequest();
            var generation = _generation;
            var reading = await Task.Run(() => Gather(request)).ConfigureAwait(true);
            if (_started && generation == _generation)
            {
                Apply(request, reading);
            }
        }
        catch (Exception e)
        {
            ErrorLog.Write(e);
        }
        finally
        {
            _busy = false;
        }
    }

    /// The GitHub side of one start: the watcher, the pushes the scan saw (queued, and handed to the watcher by the poll), one
    /// poll in flight at a time (Busy, 1 while it runs), the runs a poll finished (drained on the UI thread), and whether the
    /// watcher watches a repo as of the last poll.
    private sealed class GitHubFeed
    {
        public GitHubFeed(GitHubWatcher watcher) => Watcher = watcher;

        /// At most this many pushes wait for the next poll; the oldest is dropped beyond it.
        public const int MaxPushes = 32;

        public GitHubWatcher Watcher { get; }

        public ConcurrentQueue<string> Pushes { get; } = new();

        /// Queues a push for the next poll, dropping the oldest when the queue is full.
        public void EnqueuePush(string dir)
        {
            Pushes.Enqueue(dir);
            while (Pushes.Count > MaxPushes && Pushes.TryDequeue(out _))
            {
            }
        }

        public ConcurrentQueue<GhRun> Finished { get; } = new();

        public int Busy;

        public volatile bool Watching;
    }

    /// What one tick reads, decided on the UI thread. RunnerJobs: CiModule has GitLab jobs from the logs (keeps the logs read).
    /// WorkerLogs and UnnamedWorkers: the worker log lookups so far, and the workers whose job name is still unknown.
    private sealed record Request(
        int Number,
        DevScanner Dev,
        FileProgressWatcher Files,
        GitHubFeed GitHub,
        Dictionary<string, long> LogMtimes,
        bool RunnerJobs,
        Dictionary<int, WorkerLog> WorkerLogs,
        IReadOnlySet<int> UnnamedWorkers,
        IReadOnlyList<WatchItem> Items,
        double DoneAfter);

    /// A GitHub worker's executable and its newest Runner.Worker log (null when none yet).
    private sealed record WorkerLog(string? Exe, string? Log);

    private Request MakeRequest()
    {
        var unnamed = _module.UnnamedWorkerPids.ToHashSet();
        return new Request(_tick++, _dev!, _files!, _feed!, _logMtimes, _module.HasRunnerJobs, _workerLogs, unnamed,
            _settings.Items, _settings.DoneAfter);
    }

    /// The reads of one tick, made on a background thread.
    private sealed record Reading(
        long NowMs,
        ProcSnapshot Snapshot,
        IReadOnlyList<Job> DevJobs,
        IReadOnlyList<Job> FileJobs,
        IReadOnlyList<(string File, long MtimeMs, string Tail)>? GitLabLogs,
        IReadOnlyList<(int Pid, string? Exe, string? LogFile)> Workers,
        IReadOnlyList<Job> GitHubJobs,
        IReadOnlyDictionary<int, Job> ChildRows,
        IReadOnlyDictionary<int, string?> ChildFolders,
        Func<ProcInfo, IReadOnlyList<string>> BuildArgv);

    private Reading Gather(Request r)
    {
        var processes = _reads.Processes;
        var nowMs = _reads.Clock();
        var snapshot = processes.Snapshot();
        _cpu.Sample(snapshot, nowMs);

        IReadOnlyList<string> Argv(ProcInfo p) => ProcTable.SplitArgs(processes.CommandLine(p));
        string? Cwd(ProcInfo p) => processes.Cwd(p);

        var devJobs = r.Dev.Scan(snapshot, Argv, Cwd, nowMs);
        var fileJobs = r.Files.Jobs();

        var runners = snapshot.ByNormName("gitlab-runner");
        IReadOnlyList<(string File, long MtimeMs, string Tail)>? logs = null;
        if (r.Number % GitLabEvery == 0 && (runners.Count > 0 || r.RunnerJobs))
        {
            logs = ReadGitLabLogs(r.LogMtimes);
        }

        var childRows = new Dictionary<int, Job>();
        var childFolders = new Dictionary<int, string?>();
        foreach (var child in runners.SelectMany(runner => snapshot.Kids(runner.Pid)))
        {
            childRows[child.Pid] = ChildRow(child, snapshot);
            childFolders[child.Pid] = Cwd(child);
        }

        // A worker's log is looked up at first sight, then every 10th tick only while its job name is unknown (P3c M4).
        var workers = new List<(int Pid, string? Exe, string? LogFile)>();
        var liveWorkers = new HashSet<int>();
        foreach (var worker in snapshot.ByNormName("runner.worker"))
        {
            liveWorkers.Add(worker.Pid);
            var seen = r.WorkerLogs.TryGetValue(worker.Pid, out var cached);
            var look = !seen || (r.UnnamedWorkers.Contains(worker.Pid) && r.Number % WorkerNameEvery == 0);
            WorkerLog entry;
            if (look)
            {
                var words = Argv(worker);
                var exe = words.Count > 0 && words[0].Length > 0 ? words[0] : null;
                var log = exe is null
                    ? RunnerLogs.NewestWorkerLog(Path.Combine(_reads.RunnerDir, "_diag"))
                    : RunnerLogs.NewestWorkerLog(RunnerLogs.DiagFromWorkerExe(exe));
                entry = new WorkerLog(exe, log);
                r.WorkerLogs[worker.Pid] = entry;
            }
            else
            {
                entry = cached!;
            }
            workers.Add((worker.Pid, entry.Exe, entry.Log));
        }
        foreach (var gone in r.WorkerLogs.Keys.Where(p => !liveWorkers.Contains(p)).ToList())
        {
            r.WorkerLogs.Remove(gone);
        }

        // the poll runs on its own task: this tick never waits for GitHub
        var feed = r.GitHub;
        if (r.Number % GitHubEvery == 0 && (feed.Watching || !feed.Pushes.IsEmpty)
            && Interlocked.CompareExchange(ref feed.Busy, 1, 0) == 0)
        {
            StartPoll(feed);
        }

        return new Reading(
            nowMs,
            snapshot,
            devJobs,
            fileJobs,
            logs,
            workers,
            feed.Watcher.Jobs,
            childRows,
            childFolders,
            PrefetchArgv(processes, snapshot, r.Items));
    }

    /// One GitHub poll on a task of its own. The pushes the scan saw go to the watcher first; then, while it watches a repo, the
    /// runs are polled, and the runs that finished queue for the UI thread. Busy is cleared when the poll ends, so a poll that never
    /// answers holds only itself: the ticks go on. A push reaches the watcher at the next poll, up to about 20 s after the push
    /// (every 10th tick); the watcher's EarlyStartMs (90 s) covers that delay, so a run started just before the push still counts.
    private static void StartPoll(GitHubFeed feed)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                while (feed.Pushes.TryDequeue(out var dir))
                {
                    feed.Watcher.Pushed(dir);
                }
                feed.Watching = feed.Watcher.Watching;
                if (feed.Watching)
                {
                    await feed.Watcher.Poll(list =>
                    {
                        foreach (var run in list)
                        {
                            feed.Finished.Enqueue(run);
                        }
                    });
                }
                feed.Watching = feed.Watcher.Watching;
            }
            catch (Exception e)
            {
                ErrorLog.Write(e);
            }
            finally
            {
                Interlocked.Exchange(ref feed.Busy, 0);
            }
        });
    }

    /// Hands a reading to the module and to the build agents, on the UI thread. Publishes when either changed.
    private void Apply(Request r, Reading g)
    {
        var rows = g.ChildRows;
        var folders = g.ChildFolders;
        var finished = new List<GhRun>();
        while (r.GitHub.Finished.TryDequeue(out var run))
        {
            finished.Add(run);
        }
        var changed = _module.Update(
            new CiInputs(
                g.Snapshot,
                g.DevJobs,
                g.FileJobs,
                g.GitLabLogs,
                g.Workers,
                g.GitHubJobs,
                finished,
                p => folders.TryGetValue(p.Pid, out var folder) ? folder : null,
                p => rows.TryGetValue(p.Pid, out var row) ? row : new Job($"gl:{p.Pid}", string.Empty, L10n.L(WaitingWord), false),
                r.DoneAfter),
            g.NowMs);

        var buildChanged = _buildAgents.Update(
            new AgentInputs(
                r.Items,
                true,
                g.Snapshot,
                _buildCpu,
                g.BuildArgv,
                Array.Empty<Job>(),
                Array.Empty<Job>(),
                Array.Empty<Job>(),
                new Dictionary<string, IReadOnlyList<string>>(),
                r.DoneAfter),
            g.NowMs);

        if (changed || buildChanged)
        {
            _publish();
        }
    }

    /// Empties the build agents' states. Their toasts were already made; nothing is announced here.
    private void ClearBuild()
    {
        var empty = new AgentInputs(
            Array.Empty<WatchItem>(),
            false,
            new ProcSnapshot(Array.Empty<ProcInfo>()),
            _buildCpu,
            _ => Array.Empty<string>(),
            Array.Empty<Job>(),
            Array.Empty<Job>(),
            Array.Empty<Job>(),
            new Dictionary<string, IReadOnlyList<string>>(),
            0);
        _buildAgents.Update(empty, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    /// The GitLab logs, newest first (at most 3). A log's text is read only when its mtime changed since the last read; an
    /// unchanged log gets an empty tail, which CiModule does not parse (it keeps the parse it has for that mtime).
    private IReadOnlyList<(string File, long MtimeMs, string Tail)> ReadGitLabLogs(Dictionary<string, long> mtimes)
    {
        var previous = new Dictionary<string, long>(mtimes, StringComparer.Ordinal);
        mtimes.Clear();
        var logs = new List<(string File, long MtimeMs, string Tail)>();
        foreach (var (file, mtime) in RunnerLogs.GitLabLogs(_reads.ProgramData, _reads.GitLabFallbackDir))
        {
            var unchanged = previous.TryGetValue(file, out var last) && last == mtime;
            mtimes[file] = mtime;
            logs.Add((file, mtime, unchanged ? string.Empty : RunnerLogs.ReadTail(file, GitLabTail)));
        }
        return logs;
    }

    /// The row of a gitlab-runner child: its tree's CPU, working above 0.5 %, else "Waiting" (P3c-R5).
    private Job ChildRow(ProcInfo child, ProcSnapshot snapshot)
    {
        var total = TreeCpu(child.Pid, snapshot);
        var working = total > ChildThreshold;
        var detail = working
            ? L10n.L("Working · {0}", Math.Round(total, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture) + "% CPU")
            : L10n.L(WaitingWord);
        return new Job($"gl:{child.Pid}", string.Empty, detail, working);
    }

    /// The CPU % of a process and everything under it.
    private double TreeCpu(int root, ProcSnapshot snapshot)
    {
        var total = _cpu.Percent(root);
        var seen = new HashSet<int> { root };
        var stack = new Stack<int>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            foreach (var kid in snapshot.Kids(stack.Pop()))
            {
                if (seen.Add(kid.Pid))
                {
                    total += _cpu.Percent(kid.Pid);
                    stack.Push(kid.Pid);
                }
            }
        }
        return total;
    }

    /// The command-line words of the build watch items' processes, read now so AgentsModule.Update needs no OS calls: the named
    /// processes and their children, or every process when an item matches by arguments alone (as AgentsSource does).
    private static Func<ProcInfo, IReadOnlyList<string>> PrefetchArgv(IProcessReader processes, ProcSnapshot snapshot, IReadOnlyList<WatchItem> items)
    {
        var processItems = items
            .Where(i => i.Enabled && WatchItems.InBuild(i) && i.Kind == WatchKind.Process && i.Cli != "agy")
            .ToList();
        var names = processItems
            .Select(i => ProcTable.NormName(i.Process))
            .Where(n => n.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        var everyProcess = processItems.Any(i => ProcTable.NormName(i.Process).Length == 0 && !string.IsNullOrEmpty(i.Args));
        var wanted = everyProcess
            ? snapshot.All.ToList()
            : snapshot.All
                .Where(p => names.Contains(ProcTable.NormName(p.Name)))
                .SelectMany(p => snapshot.Kids(p.Pid).Prepend(p))
                .ToList();
        var words = new Dictionary<int, IReadOnlyList<string>>();
        foreach (var p in wanted)
        {
            words[p.Pid] = ProcTable.SplitArgs(processes.CommandLine(p));
        }
        return p => words.TryGetValue(p.Pid, out var list) ? list : Array.Empty<string>();
    }

    /// The task history from state.json: each key's runs, or null when there is none.
    private static IReadOnlyDictionary<string, IReadOnlyList<double>>? LoadHistory()
    {
        if (CiState.Get("taskHistory") is not JsonObject saved)
        {
            return null;
        }
        var map = new Dictionary<string, IReadOnlyList<double>>(StringComparer.Ordinal);
        foreach (var (key, node) in saved)
        {
            var runs = new List<double>();
            if (node is JsonArray array)
            {
                foreach (var item in array)
                {
                    if (item is JsonValue value && value.TryGetValue(out double seconds))
                    {
                        runs.Add(seconds);
                    }
                }
            }
            map[key] = runs;
        }
        return map;
    }

    private static void SaveHistory(IReadOnlyDictionary<string, IReadOnlyList<double>> runs)
    {
        var saved = new JsonObject();
        foreach (var (key, list) in runs)
        {
            var array = new JsonArray();
            foreach (var seconds in list)
            {
                array.Add((JsonNode?)JsonValue.Create(seconds));
            }
            saved[key] = array;
        }
        CiState.Set("taskHistory", saved);
    }
}
