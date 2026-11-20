using NotchTasks.Core.Agents;

namespace NotchTasks.Core.Ci;

/// Finds the running dev tasks on every tick. Port of electron/src/core/devscan.js DevScanner (lines 381-563) and
/// Mac: Sources/DevTasks.swift. Owns its own CpuMeter. Reads argv and cwd through the injected functions.
public sealed class DevScanner
{
    private const long QuickMs = 3000;          // a command under 3 s is not a task
    private const long BusyMemoryMs = 8000;     // a busy-only row stays this long after its last busy sample
    private const double TaskBusyCpu = 40;      // % of the tree: the Gradle daemon counts as busy above this
    private const double RenderBusyCpu = 150;   // % of the tree: a render app counts as busy above this
    private const double RenderShownSeconds = 10;
    private const int MaxAncestors = 40;

    private readonly TaskHistory history;
    private readonly CpuMeter meter = new();
    // Keyed by (pid, start time): a reused pid is a new process, so its arguments and git state are read again.
    private readonly Dictionary<(int Pid, long Start), DevMatch?> argvCache = new();
    private readonly HashSet<(int Pid, long Start)> seenGit = new();
    private Dictionary<string, Running> running = new(StringComparer.Ordinal);

    public DevScanner(TaskHistory history)
    {
        this.history = history;
    }

    /// Raised once per `git push` process, with its working folder when known.
    public event Action<string>? Pushed;

    /// The task rows for one snapshot, sorted by id. nowMs is the clock of the snapshot (milliseconds).
    public IReadOnlyList<Job> Scan(ProcSnapshot snapshot, Func<ProcInfo, IReadOnlyList<string>> argv, Func<ProcInfo, string?> cwd, long nowMs)
    {
        meter.Sample(snapshot, nowMs);
        var live = snapshot.All.Select(p => (p.Pid, p.StartTicks)).ToHashSet();
        foreach (var key in argvCache.Keys.ToList())
        {
            if (!live.Contains(key))
            {
                argvCache.Remove(key);
            }
        }
        seenGit.RemoveWhere(key => !live.Contains(key));

        var found = new Dictionary<string, Running>(StringComparer.Ordinal);

        void Keep(string id, DevMatch m, string title, long since, string key, bool busy)
        {
            if (!running.TryGetValue(id, out var r))
            {
                r = new Running(m, title, since, key);
            }
            r.Match = m;
            if (busy)
            {
                r.LastBusy = nowMs;
            }
            found[id] = r;
        }

        // Matched by the normalized name; the arguments are read once per pid and name.
        DevMatch? Match(ProcInfo p, string name)
        {
            var key = (p.Pid, p.StartTicks);
            if (argvCache.TryGetValue(key, out var cached))
            {
                return cached;
            }
            var m = DevRules.Names.Contains(name) || DevRules.IsPython(name) ? DevRules.Classify(name, argv(p)) : null;
            argvCache[key] = m;
            return m;
        }

        // CI jobs are shown by their runner; nested tools belong to the outermost task (npm -> node next build).
        bool UnderRunnerOrTask(ProcInfo p)
        {
            var cur = snapshot.ById.TryGetValue(p.Ppid, out var first) ? (ProcInfo?)first : null;
            for (var depth = 0; cur is { } c && c.Pid > 1 && depth < MaxAncestors; depth++)
            {
                var cn = ProcTable.NormName(c.Name);
                if (cn is "gitlab-runner" or "runner.worker" || Match(c, cn) is not null)
                {
                    return true;
                }
                cur = snapshot.ById.TryGetValue(c.Ppid, out var up) ? (ProcInfo?)up : null;
            }
            return false;
        }

        // CPU % of a process and everything under it.
        double TreeCpu(int root)
        {
            var total = meter.Percent(root);
            var seen = new HashSet<int> { root };
            var stack = new Stack<int>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                foreach (var c in snapshot.Kids(stack.Pop()))
                {
                    if (!seen.Add(c.Pid))
                    {
                        continue;
                    }
                    total += meter.Percent(c.Pid);
                    stack.Push(c.Pid);
                }
            }
            return total;
        }

        long StartMs(ProcInfo p) =>
            p.StartTicks > 0 ? DateTimeOffset.FromFileTime(p.StartTicks).ToUnixTimeMilliseconds() : nowMs;

        // Command-line tools.
        foreach (var p in snapshot.All)
        {
            var name = ProcTable.NormName(p.Name);
            if (name == "git" && seenGit.Add((p.Pid, p.StartTicks)))
            {
                var args = argv(p);
                if (args.Count > 1 && args[1] == "push")
                {
                    var dir = cwd(p);
                    if (!string.IsNullOrEmpty(dir))
                    {
                        Pushed?.Invoke(dir);
                    }
                }
            }

            var m = Match(p, name);
            if (m is null || UnderRunnerOrTask(p))
            {
                continue;
            }
            var id = $"dev:{p.Pid}";
            var dirOfCwd = cwd(p);
            var folder = dirOfCwd is not null ? Folders.Project(dirOfCwd) : Folders.FolderFromArgv(argv(p));
            if (string.IsNullOrEmpty(folder))
            {
                folder = m.Tool;
            }
            var title = string.IsNullOrEmpty(m.Subject) ? folder : m.Subject;
            var key = string.Join('|', m.Tool, m.Action, folder);

            if (m.BusyOnly)
            {
                var busy = TreeCpu(p.Pid) > TaskBusyCpu;
                // the Gradle daemon only counts while it works, and for 8 s after
                running.TryGetValue(id, out var prev);
                if (busy || (prev?.LastBusy is { } lastBusy && nowMs - lastBusy < BusyMemoryMs))
                {
                    Keep(id, m, title, prev?.Since ?? nowMs, key, busy);
                }
            }
            else if (nowMs - StartMs(p) >= QuickMs)
            {
                Keep(id, m, title, StartMs(p), key, true);
            }
        }

        // The Gradle daemon is hidden while a Gradle client is already shown.
        if (found.Values.Any(r => r.Match.Tool == "Gradle" && !r.Match.BusyOnly))
        {
            foreach (var id in found.Where(kv => kv.Value.Match.Tool == "Gradle" && kv.Value.Match.BusyOnly).Select(kv => kv.Key).ToList())
            {
                found.Remove(id);
            }
        }

        // GUI render and edit apps: a task only while they work hard, sustained, so scrubbing a timeline does not count.
        foreach (var p in snapshot.All)
        {
            var name = ProcTable.NormName(p.Name);
            var label = RenderLabel(name);
            if (label is null)
            {
                continue;
            }
            var id = $"app:{p.Pid}";
            var load = TreeCpu(p.Pid);
            running.TryGetValue(id, out var prev);
            var busy = load > RenderBusyCpu;
            long? since = busy ? prev?.Since ?? nowMs : prev?.Since;
            if (since is null || !(busy || (prev?.LastBusy is { } lastBusy && nowMs - lastBusy < BusyMemoryMs)))
            {
                continue;
            }
            var m = new DevMatch(label, L10n.L("working · {0}% CPU", (long)Math.Truncate(load)), "film.stack", null, true);
            Keep(id, m, label, since.Value, $"app|{label}", busy);
        }

        // Tasks that ended are recorded in the history. running is replaced first, so a record that throws can never repeat.
        var ended = running
            .Where(kv => !found.ContainsKey(kv.Key))
            .Select(kv => (Key: kv.Value.Key, Seconds: (nowMs - kv.Value.Since) / 1000.0))
            .ToList();
        running = found;
        foreach (var (key, seconds) in ended)
        {
            history.Record(key, seconds);
        }

        var jobs = new List<Job>();
        foreach (var (id, r) in found)
        {
            var elapsed = (nowMs - r.Since) / 1000.0;
            var shown = !r.Match.BusyOnly || id.StartsWith("dev:", StringComparison.Ordinal) || elapsed >= RenderShownSeconds;
            if (!shown)
            {
                continue;
            }
            var (progress, time) = TaskHistory.Estimate(elapsed, history.Expected(r.Key));
            jobs.Add(new Job(
                id,
                $"{r.Match.Tool} · {r.Title}",
                $"{r.Match.Action} · {time}",
                true,
                null,
                progress,
                r.Match.Symbol,
                r.Match.Tool));
        }
        jobs.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        return jobs;
    }

    private static string? RenderLabel(string name)
    {
        foreach (var (exe, label) in DevRules.RenderApps)
        {
            if (exe == name)
            {
                return label;
            }
        }
        return null;
    }

    /// A task seen by the scanner: its match, title, start (or the first busy sample), history key and last busy sample.
    private sealed class Running
    {
        public Running(DevMatch match, string title, long since, string key)
        {
            Match = match;
            Title = title;
            Since = since;
            Key = key;
        }

        public DevMatch Match { get; set; }

        public string Title { get; }

        public long Since { get; }

        public string Key { get; }

        public long? LastBusy { get; set; }
    }
}
