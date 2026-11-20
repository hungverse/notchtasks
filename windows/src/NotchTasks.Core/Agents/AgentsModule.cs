using System.Globalization;
using NotchTasks.Core.Modules;
using NotchTasks.Core.Notch;

namespace NotchTasks.Core.Agents;

/// Everything the agents module reads on one tick. The caller reads the OS; the module does not.
/// Argv gives a process's command-line words. ClaudeJobs, CodexJobs and AntigravityJobs come from their own readers.
/// LocalModels is keyed "lmstudio" and "ollama" and lists the models each runner has loaded.
public sealed record AgentInputs(
    IReadOnlyList<WatchItem> Items,
    bool AgentsOn,
    ProcSnapshot Snapshot,
    /// Sampled by AgentsModule.Update itself, at its nowMs, on every call (even when AgentsOn is false). The caller must
    /// NOT call Cpu.Sample for the same tick: a second sample at the same time reads every process as 0 %.
    CpuMeter Cpu,
    Func<ProcInfo, IReadOnlyList<string>> Argv,
    IReadOnlyList<Job> ClaudeJobs,
    IReadOnlyList<Job> CodexJobs,
    IReadOnlyList<Job> AntigravityJobs,
    IReadOnlyDictionary<string, IReadOnlyList<string>> LocalModels,
    double DoneAfterSeconds);

/// The agents module (Electron: main.js treeCpu, procJob, itemStates, announceFinished; Mac: Model.swift announceFinished).
/// Each enabled, non-build watch item becomes an ItemState with its jobs. A job that was working and then stopped, after
/// running at least DoneAfterSeconds, is announced as done. Update takes a manual clock and makes no OS calls: the caller
/// reads the process table into AgentInputs.Snapshot and the module samples AgentInputs.Cpu itself at nowMs.
public sealed class AgentsModule
{
    /// A job keeps reading as working for this long after its last busy sample.
    private const long StickyMs = 6_000;

    /// A pid's last busy sample is forgotten after this long.
    private const long BusyMemoryMs = 60_000;

    /// A job that has not been seen working for this long is finished.
    private const long GoneMs = 20_000;

    private const string DoneTint = "#30D158";
    private const string LmStudioBundleId = "ai.elementlabs.lmstudio";

    private readonly IAnnouncer _announce;
    private readonly bool _build;
    private readonly Dictionary<string, long> _busyAt = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Tracked> _workingSince = new(StringComparer.Ordinal);
    private IReadOnlyList<ItemState> _states = Array.Empty<ItemState>();
    private IReadOnlyList<ItemState> _running = Array.Empty<ItemState>();

    /// The toasts for finished jobs go to the announcer. With build, the module takes only the enabled build items
    /// (InBuild: Build & CI rows and items marked as build tools), which the Build & CI module shows through this instance.
    /// Without it, those items are left out (they are the Build & CI module's).
    public AgentsModule(IAnnouncer announce, bool build = false)
    {
        _announce = announce;
        _build = build;
    }

    /// The items with their jobs, in the order of the watch list. Empty while agents are off.
    public IReadOnlyList<ItemState> States => _states;

    /// Only the working jobs, and only the items that have one (the pill's running row). Built once per Update.
    public IReadOnlyList<ItemState> Running => _running;

    /// Samples the CPU at nowMs, recomputes the states and announces the finished jobs. Returns whether States changed.
    /// Update samples input.Cpu itself, on every call, even when AgentsOn is false; the caller must not sample it too.
    public bool Update(AgentInputs input, long nowMs)
    {
        input.Cpu.Sample(input.Snapshot, nowMs);
        foreach (var pid in _busyAt.Where(kv => nowMs - kv.Value > BusyMemoryMs).Select(kv => kv.Key).ToList())
        {
            _busyAt.Remove(pid);
        }

        var next = input.AgentsOn ? BuildStates(input, nowMs) : new List<ItemState>();
        AnnounceFinished(input.AgentsOn, next, input.DoneAfterSeconds, nowMs);

        var changed = !next.SequenceEqual(_states);
        _states = next;
        _running = next
            .Select(s => new ItemState(s.Item, s.Jobs.Where(j => j.Working).ToList()))
            .Where(s => s.Jobs.Count > 0)
            .ToList();
        return changed;
    }

    private List<ItemState> BuildStates(AgentInputs input, long now)
    {
        var result = new List<ItemState>();
        foreach (var item in input.Items)
        {
            // Build items belong to the build instance (CiSource, 3c) and the others to the agents instance
            if (!item.Enabled || WatchItems.InBuild(item) != _build)
            {
                continue;
            }

            var alive = false;
            var jobs = new List<Job>();
            var threshold = WatchItems.CpuThreshold(item);
            var models = ModelTitle(item, input.LocalModels);

            if (item.Kind == WatchKind.Process && item.Cli == "agy")
            {
                jobs.AddRange(input.AntigravityJobs);
            }
            else if (item.Kind == WatchKind.Process)
            {
                foreach (var root in WatchItems.Roots(item, input.Snapshot, input.Argv))
                {
                    alive = true;
                    if (item.ChildTasks)
                    {
                        foreach (var child in input.Snapshot.Kids(root.Pid))
                        {
                            var title = Folders.FolderFromArgv(input.Argv(child)) ?? ProcTable.DisplayName(child.Name);
                            jobs.Add(ProcJob(child, input.Snapshot, input.Cpu, title, threshold, "Waiting", null, now));
                        }
                    }
                    else
                    {
                        var title = models ?? Folders.FolderFromArgv(input.Argv(root)) ?? item.Name;
                        jobs.Add(ProcJob(root, input.Snapshot, input.Cpu, title, threshold,
                            ModelLine(item, input.LocalModels) ?? L10n.L("Waiting"),
                            models is null ? null : L10n.L("Running"), now));
                    }
                }
            }
            else if (item.Kind == WatchKind.App)
            {
                var want = ProcTable.NormName(WatchItems.AppExe(item) ?? string.Empty);
                foreach (var p in input.Snapshot.All)
                {
                    if (want.Length == 0 || ProcTable.NormName(p.Name) != want)
                    {
                        continue;
                    }
                    // a helper process of the same exe is part of its parent, which is counted
                    if (input.Snapshot.ById.TryGetValue(p.Ppid, out var parent) && ProcTable.NormName(parent.Name) == want)
                    {
                        continue;
                    }
                    alive = true;
                    jobs.Add(ProcJob(p, input.Snapshot, input.Cpu, models ?? item.Name, threshold,
                        ModelLine(item, input.LocalModels) ?? L10n.L("Open"),
                        models is null ? null : L10n.L("Running"), now));
                }
            }
            else if (item.Kind == WatchKind.Codex)
            {
                jobs.AddRange(input.CodexJobs);
            }
            else if (item.Kind == WatchKind.Claude)
            {
                jobs.AddRange(input.ClaudeJobs);
            }

            if (alive || jobs.Count > 0)
            {
                result.Add(new ItemState(item, jobs.OrderByDescending(j => j.Working).ToList()));
            }
        }
        return result;
    }

    /// One row for a process: working while its tree uses more than the threshold, and for 6 s after the last such sample.
    private Job ProcJob(
        ProcInfo p,
        ProcSnapshot snapshot,
        CpuMeter cpu,
        string title,
        double threshold,
        string idle,
        string? busy,
        long now)
    {
        var key = p.Pid.ToString(CultureInfo.InvariantCulture);
        var (total, top) = TreeCpu(p.Pid, snapshot, cpu);
        if (total > threshold)
        {
            _busyAt[key] = now;
        }
        var working = _busyAt.TryGetValue(key, out var at) && now - at < StickyMs;
        var pct = Math.Round(total, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture) + "% CPU";
        var detail = !working
            ? idle
            : busy is not null ? $"{busy} · {pct}"
            : top is not null ? $"▶ {top} · {pct}"
            : L10n.L("Working · {0}", pct);
        return new Job(key, title, detail, working);
    }

    /// The CPU % of a process tree (the root and all its descendants) and the busiest child above 1 %, if any.
    private static (double Total, string? Top) TreeCpu(int root, ProcSnapshot snapshot, CpuMeter cpu)
    {
        var total = cpu.Percent(root);
        string? top = null;
        var topCpu = 1.0;
        var seen = new HashSet<int> { root };
        var stack = new Stack<int>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            foreach (var child in snapshot.Kids(stack.Pop()))
            {
                if (!seen.Add(child.Pid))
                {
                    continue;
                }
                var v = cpu.Percent(child.Pid);
                total += v;
                if (v > topCpu)
                {
                    top = ProcTable.DisplayName(child.Name);
                    topCpu = v;
                }
                stack.Push(child.Pid);
            }
        }
        return (total, top);
    }

    /// The loaded model names of a local model runner, joined; null when the item is not a runner or has none loaded.
    private static string? ModelTitle(WatchItem item, IReadOnlyDictionary<string, IReadOnlyList<string>> models)
    {
        var names = LocalNames(item, models);
        return names.Count > 0 ? string.Join(", ", names) : null;
    }

    /// The idle line of a local model runner: the loaded models, or that none is. Null for other items.
    private static string? ModelLine(WatchItem item, IReadOnlyDictionary<string, IReadOnlyList<string>> models)
    {
        if (LocalKey(item) is null)
        {
            return null;
        }
        var names = LocalNames(item, models);
        return names.Count > 0 ? L10n.L("Loaded ") + string.Join(", ", names) : L10n.L("No model loaded");
    }

    private static IReadOnlyList<string> LocalNames(WatchItem item, IReadOnlyDictionary<string, IReadOnlyList<string>> models)
    {
        var key = LocalKey(item);
        return key is not null && models.TryGetValue(key, out var names) ? names : Array.Empty<string>();
    }

    private static string? LocalKey(WatchItem item) =>
        item.BundleID == LmStudioBundleId ? "lmstudio" : item.Process == "ollama" ? "ollama" : null;

    /// Drops the tracking of finished jobs and announces those that ran long enough. With agents off, forgets everything.
    private void AnnounceFinished(bool agentsOn, IReadOnlyList<ItemState> states, double doneAfterSeconds, long now)
    {
        if (!agentsOn)
        {
            _workingSince.Clear();
            _busyAt.Clear();
            return;
        }

        foreach (var state in states)
        {
            foreach (var job in state.Jobs)
            {
                if (!job.Working)
                {
                    continue;
                }
                var key = $"{state.Item.Id}/{job.Id}";
                var since = _workingSince.TryGetValue(key, out var prev) ? prev.Since : now;
                _workingSince[key] = new Tracked(state.Item, job.Title, since, now);
            }
        }

        foreach (var key in _workingSince.Keys.ToList())
        {
            var w = _workingSince[key];
            // a job that is missing for one poll is still running
            if (now - w.LastSeen < GoneMs)
            {
                continue;
            }
            _workingSince.Remove(key);
            var took = (w.LastSeen - w.Since) / 1000.0;
            if (took < doneAfterSeconds)
            {
                continue;
            }
            _announce.Announce(new Toast(
                Glyph: "check",
                Tint: DoneTint,
                Title: L10n.L("{0} done", w.Item.Name),
                Subtitle: $"{w.Title} · {Ago.Text(took)}",
                Event: "taskDone",
                Module: "agents"));
        }
    }

    /// A job seen working: when it started, and when it was last seen working (both in ms).
    private sealed record Tracked(WatchItem Item, string Title, long Since, long LastSeen);
}
