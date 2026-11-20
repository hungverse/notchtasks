using System.Globalization;
using System.IO;
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

/// The state.json keys of Build & CI (taskHistory, dismissedJobs), behind one lock: the scan writes the task history on the
/// background, and the module writes the dismissed ids on the UI thread.
public static class CiState
{
    private static readonly object Gate = new();

    public static JsonNode? Get(string key)
    {
        lock (Gate)
        {
            return StateJson.Get(AppPaths.StateFile, key);
        }
    }

    public static void Set(string key, JsonNode? value)
    {
        lock (Gate)
        {
            StateJson.Set(AppPaths.StateFile, key, value);
        }
    }
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
    private GitHubWatcher? _github;
    private Dictionary<string, long> _logMtimes = new(StringComparer.Ordinal);

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
        var github = new GitHubWatcher(_reads.GitHub, _reads.Clock);
        var dev = new DevScanner(new TaskHistory(LoadHistory(), SaveHistory));
        dev.Pushed += dir => github.Pushed(dir);
        _dev = dev;
        _github = github;
        _files = new FileProgressWatcher(FileProgressWatcher.DefaultFolders(_reads.Home), _reads.Clock);
        _logMtimes = new Dictionary<string, long>(StringComparer.Ordinal);
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
        _github = null;
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

    /// What one tick reads, decided on the UI thread.
    private sealed record Request(
        int Number,
        DevScanner Dev,
        FileProgressWatcher Files,
        GitHubWatcher GitHub,
        Dictionary<string, long> LogMtimes,
        bool GitLabShown,
        IReadOnlyList<WatchItem> Items,
        double DoneAfter);

    private Request MakeRequest()
    {
        var gitLabShown = _module.BuildState?.Jobs.Any(j => j.Tool == GitLabTool) ?? false;
        return new Request(_tick++, _dev!, _files!, _github!, _logMtimes, gitLabShown, _settings.Items, _settings.DoneAfter);
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
        IReadOnlyList<GhRun> GitHubFinished,
        IReadOnlyDictionary<int, Job> ChildRows,
        IReadOnlyDictionary<int, string?> ChildFolders,
        Func<ProcInfo, IReadOnlyList<string>> BuildArgv);

    private async Task<Reading> Gather(Request r)
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
        if (r.Number % GitLabEvery == 0 && (runners.Count > 0 || r.GitLabShown))
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

        var workers = new List<(int Pid, string? Exe, string? LogFile)>();
        foreach (var worker in snapshot.ByNormName("runner.worker"))
        {
            var words = Argv(worker);
            var exe = words.Count > 0 && words[0].Length > 0 ? words[0] : null;
            var log = exe is null
                ? RunnerLogs.NewestWorkerLog(Path.Combine(_reads.RunnerDir, "_diag"))
                : RunnerLogs.NewestWorkerLog(RunnerLogs.DiagFromWorkerExe(exe));
            workers.Add((worker.Pid, exe, log));
        }

        var finished = new List<GhRun>();
        if (r.Number % GitHubEvery == 0 && r.GitHub.Watching)
        {
            await r.GitHub.Poll(list => finished.AddRange(list));
        }

        return new Reading(
            nowMs,
            snapshot,
            devJobs,
            fileJobs,
            logs,
            workers,
            r.GitHub.Jobs,
            finished,
            childRows,
            childFolders,
            PrefetchArgv(processes, snapshot, r.Items));
    }

    /// Hands a reading to the module and to the build agents, on the UI thread. Publishes when either changed.
    private void Apply(Request r, Reading g)
    {
        var rows = g.ChildRows;
        var folders = g.ChildFolders;
        var changed = _module.Update(
            new CiInputs(
                g.Snapshot,
                g.DevJobs,
                g.FileJobs,
                g.GitLabLogs,
                g.Workers,
                g.GitHubJobs,
                g.GitHubFinished,
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
        foreach (var file in RunnerLogs.GitLabLogs(_reads.ProgramData, _reads.GitLabFallbackDir))
        {
            var mtime = new DateTimeOffset(File.GetLastWriteTimeUtc(file)).ToUnixTimeMilliseconds();
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
