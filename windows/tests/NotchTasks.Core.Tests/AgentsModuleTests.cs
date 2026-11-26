using NotchTasks.Core.Agents;
using NotchTasks.Core.Modules;
using NotchTasks.Core.Notch;
using Xunit;

namespace NotchTasks.Core.Tests;

// Ported from electron/src/main/main.js (treeCpu, procJob, itemStates, announceFinished) and the Mac's announceFinished.
// Pure: hand-built snapshots, a manual clock, and a CpuMeter fed by the module itself. No OS calls.
[Collection("L10n")]
public class AgentsModuleTests
{
    // the assertions compare English copy: the strings are loaded in English for every test of the class
    public AgentsModuleTests() => AsksFixtures.InitEnglish();

    /// An announcer that keeps every toast.
    private sealed class RecordingAnnouncer : IAnnouncer
    {
        public List<Toast> Toasts { get; } = new();

        public void Announce(Toast t, bool notify = true) => Toasts.Add(t);
    }

    private static readonly Func<ProcInfo, IReadOnlyList<string>> NoArgv = _ => Array.Empty<string>();

    /// A process with a stable start time (so CpuMeter does not read it as a reused pid).
    private static ProcInfo P(int pid, int ppid, string name, long cpuTicks = 0) => new(pid, ppid, name, pid * 1000L, cpuTicks);

    /// CPU ticks (100 ns units) that make a process use pct % over dtMs.
    private static long Ticks(double pct, long dtMs) => (long)(pct * dtMs * 100);

    private static ProcSnapshot Snap(params ProcInfo[] procs) => new(procs);

    private static AgentInputs Inputs(
        IReadOnlyList<WatchItem> items,
        ProcSnapshot snap,
        CpuMeter cpu,
        bool agentsOn = true,
        IReadOnlyList<Job>? claude = null,
        IReadOnlyList<Job>? codex = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? models = null,
        double doneAfter = 30,
        Func<ProcInfo, IReadOnlyList<string>>? argv = null) =>
        new(items, agentsOn, snap, cpu, argv ?? NoArgv, claude ?? Array.Empty<Job>(), codex ?? Array.Empty<Job>(),
            Array.Empty<Job>(), models ?? new Dictionary<string, IReadOnlyList<string>>(), doneAfter);

    private static WatchItem ProcessItem(string process, bool childTasks = false) =>
        new("proc-1", process, WatchKind.Process, Process: process, ChildTasks: childTasks);

    private static readonly WatchItem Claude = new("claude-1", "Claude", WatchKind.Claude);

    [Fact]
    public void ProcessItem_WorkingAboveThreshold_ShowsTopChild()
    {
        // root uses 10 %, child "git.exe" 30 %: the tree is 40 %, and git is the busiest child
        var module = new AgentsModule(new RecordingAnnouncer());
        var cpu = new CpuMeter();
        var item = ProcessItem("node");

        module.Update(Inputs(new[] { item }, Snap(P(100, 1, "node"), P(101, 100, "git.exe")), cpu), 0);
        module.Update(Inputs(new[] { item }, Snap(P(100, 1, "node", Ticks(10, 1000)), P(101, 100, "git.exe", Ticks(30, 1000))), cpu), 1000);

        var job = Assert.Single(Assert.Single(module.States).Jobs);
        Assert.True(job.Working);
        Assert.Equal("▶ git · 40% CPU", job.Detail);
    }

    [Fact]
    public void Working_Sticky6s_ThenWaiting()
    {
        var module = new AgentsModule(new RecordingAnnouncer());
        var cpu = new CpuMeter();
        var item = ProcessItem("node");
        var busy = Snap(P(100, 1, "node", Ticks(10, 1000)), P(101, 100, "git.exe", Ticks(30, 1000)));
        var quiet = Snap(P(100, 1, "node", Ticks(10, 1000)), P(101, 100, "git.exe", Ticks(30, 1000)));

        module.Update(Inputs(new[] { item }, Snap(P(100, 1, "node"), P(101, 100, "git.exe")), cpu), 0);
        module.Update(Inputs(new[] { item }, busy, cpu), 1000);

        // no CPU after the busy sample at 1000 ms: still working until 6 s have passed
        module.Update(Inputs(new[] { item }, quiet, cpu), 4000);
        Assert.True(Assert.Single(Assert.Single(module.States).Jobs).Working);
        module.Update(Inputs(new[] { item }, quiet, cpu), 6999);
        Assert.True(Assert.Single(Assert.Single(module.States).Jobs).Working);

        module.Update(Inputs(new[] { item }, quiet, cpu), 7000);
        var job = Assert.Single(Assert.Single(module.States).Jobs);
        Assert.False(job.Working);
        Assert.Equal("Waiting", job.Detail);
    }

    [Fact]
    public void AppItem_SkipsChildProcessesOfSameExe()
    {
        var module = new AgentsModule(new RecordingAnnouncer());
        var cpu = new CpuMeter();
        var item = new WatchItem("cursor-1", "Cursor", WatchKind.App, BundleID: "com.todesktop.230313mzl4w4u92");

        module.Update(Inputs(new[] { item }, Snap(
            P(200, 1, "Cursor.exe"),
            P(201, 200, "Cursor.exe"),
            P(202, 200, "Cursor.exe"),
            P(300, 1, "Code.exe")), cpu), 0);

        var state = Assert.Single(module.States);
        var job = Assert.Single(state.Jobs);
        Assert.Equal("Cursor", job.Title);
        Assert.Equal("Open", job.Detail);
    }

    [Fact]
    public void ChildTasks_OneJobPerChild_TitledByFolder()
    {
        var module = new AgentsModule(new RecordingAnnouncer());
        var cpu = new CpuMeter();
        var item = ProcessItem("node", childTasks: true);
        var argv = new Dictionary<int, string[]>
        {
            [100] = new[] { "node" },
            [101] = new[] { "node", @"C:\src\Shop\Shop.sln" },
            [102] = new[] { "node", @"C:\src\Api\server.ts" },
        };

        module.Update(Inputs(new[] { item }, Snap(
            P(100, 1, "node"),
            P(101, 100, "node"),
            P(102, 100, "node")), cpu, argv: p => argv[p.Pid]), 0);

        var state = Assert.Single(module.States);
        Assert.Equal(new[] { "Shop", "Api" }, state.Jobs.Select(j => j.Title));
        Assert.All(state.Jobs, j => Assert.Equal("Waiting", j.Detail));
    }

    [Fact]
    public void Done_CarriesTheAgentItemAsItsIcon()
    {
        var announcer = new RecordingAnnouncer();
        var module = new AgentsModule(announcer);
        var cpu = new CpuMeter();
        var busy = new[] { new Job("s1", "proj", "Working · 1 sec", true) };

        module.Update(Inputs(new[] { Claude }, Snap(), cpu, claude: busy, doneAfter: 30), 0);
        module.Update(Inputs(new[] { Claude }, Snap(), cpu, claude: busy, doneAfter: 30), 40_000);
        module.Update(Inputs(new[] { Claude }, Snap(), cpu, doneAfter: 30), 60_000);

        var toast = Assert.Single(announcer.Toasts);
        Assert.Equal(Claude, toast.Icon);
    }

    [Fact]
    public void DoneNaming_AgentNamesTheItemAndKeepsItsIcon()
    {
        var (title, icon) = AgentsModule.DoneNaming(Claude, "proj");

        Assert.Equal("Claude done", title);
        Assert.Equal(Claude, icon);
    }

    [Fact]
    public void DoneNaming_DevRowNamesTheJobAndHasNoIcon()
    {
        // a dev row's job names its tool ("Gradle done"), and the row's own icon is not drawn
        var build = new WatchItem("build", "Build & CI", WatchKind.Dev);

        var (title, icon) = AgentsModule.DoneNaming(build, "Gradle");

        Assert.Equal("Gradle done", title);
        Assert.Null(icon);
    }

    [Fact]
    public void Done_AfterGone20s_IfRanLongEnough()
    {
        var announcer = new RecordingAnnouncer();
        var module = new AgentsModule(announcer);
        var cpu = new CpuMeter();
        var busy = new[] { new Job("s1", "proj", "Working · 1 sec", true) };

        module.Update(Inputs(new[] { Claude }, Snap(), cpu, claude: busy, doneAfter: 30), 0);
        module.Update(Inputs(new[] { Claude }, Snap(), cpu, claude: busy, doneAfter: 30), 20_000);
        module.Update(Inputs(new[] { Claude }, Snap(), cpu, claude: busy, doneAfter: 30), 40_000);
        Assert.Empty(announcer.Toasts);

        // gone at 60 s: 20 s after the last busy sample, and it ran 40 s (at least 30 s)
        module.Update(Inputs(new[] { Claude }, Snap(), cpu, doneAfter: 30), 60_000);

        var toast = Assert.Single(announcer.Toasts);
        Assert.Equal("Claude done", toast.Title);
        Assert.Equal("proj · 40 sec", toast.Subtitle);
        Assert.Equal("check", toast.Glyph);
        Assert.Equal("#30D158", toast.Tint);
        Assert.Equal("taskDone", toast.Event);
        Assert.Equal("agents", toast.Module);
    }

    [Fact]
    public void Gone_19999ms_NoToast_20000ms_Toast()
    {
        var announcer = new RecordingAnnouncer();
        var module = new AgentsModule(announcer);
        var cpu = new CpuMeter();
        var busy = new[] { new Job("s1", "proj", "Working", true) };

        module.Update(Inputs(new[] { Claude }, Snap(), cpu, claude: busy, doneAfter: 30), 0);
        module.Update(Inputs(new[] { Claude }, Snap(), cpu, claude: busy, doneAfter: 30), 30_000);  // ran 30 s
        module.Update(Inputs(new[] { Claude }, Snap(), cpu, doneAfter: 30), 49_999);               // gone 19.999 s

        Assert.Empty(announcer.Toasts);

        module.Update(Inputs(new[] { Claude }, Snap(), cpu, doneAfter: 30), 50_000);               // gone 20 s

        Assert.Single(announcer.Toasts);
    }

    [Fact]
    public void Ran_ExactlyDoneAfter_Toasts()
    {
        var announcer = new RecordingAnnouncer();
        var module = new AgentsModule(announcer);
        var cpu = new CpuMeter();
        var busy = new[] { new Job("s1", "proj", "Working", true) };

        module.Update(Inputs(new[] { Claude }, Snap(), cpu, claude: busy, doneAfter: 30), 0);
        module.Update(Inputs(new[] { Claude }, Snap(), cpu, claude: busy, doneAfter: 30), 30_000);  // ran exactly 30 s
        module.Update(Inputs(new[] { Claude }, Snap(), cpu, doneAfter: 30), 50_000);

        var toast = Assert.Single(announcer.Toasts);
        Assert.Equal("Claude done", toast.Title);
        Assert.Equal("proj · 30 sec", toast.Subtitle);
    }

    [Fact]
    public void AgentsOffThenOn_BusyMemoryCleared_ReadsWaiting()
    {
        var module = new AgentsModule(new RecordingAnnouncer());
        var cpu = new CpuMeter();
        var item = ProcessItem("node");
        var busy = Snap(P(100, 1, "node", Ticks(10, 1000)), P(101, 100, "git.exe", Ticks(30, 1000)));

        module.Update(Inputs(new[] { item }, Snap(P(100, 1, "node"), P(101, 100, "git.exe")), cpu), 0);
        module.Update(Inputs(new[] { item }, busy, cpu), 1000);
        Assert.True(Assert.Single(Assert.Single(module.States).Jobs).Working);

        // agents off at 2 s; the process keeps the same CPU, so the next sample reads 0 %
        module.Update(Inputs(new[] { item }, busy, cpu, agentsOn: false), 2000);
        module.Update(Inputs(new[] { item }, busy, cpu), 3000);

        var job = Assert.Single(Assert.Single(module.States).Jobs);
        Assert.False(job.Working);
        Assert.Equal("Waiting", job.Detail);
    }

    [Fact]
    public void ChildTaskTitle_DropsExeFromTheName()
    {
        var module = new AgentsModule(new RecordingAnnouncer());
        var cpu = new CpuMeter();
        var item = ProcessItem("msbuild", childTasks: true);

        module.Update(Inputs(new[] { item }, Snap(P(100, 1, "msbuild"), P(101, 100, "MSBuild.EXE")), cpu), 0);

        Assert.Equal("MSBuild", Assert.Single(Assert.Single(module.States).Jobs).Title);
    }

    [Fact]
    public void DisplayName_DropsExeCaseInsensitively_KeepsCase()
    {
        Assert.Equal("git", ProcTable.DisplayName("git.exe"));
        Assert.Equal("MSBuild", ProcTable.DisplayName("MSBuild.EXE"));
        Assert.Equal("node", ProcTable.DisplayName("node"));
        Assert.Equal("exe", ProcTable.DisplayName("exe"));
    }

    [Fact]
    public void Blink_Under20s_NoToast()
    {
        var announcer = new RecordingAnnouncer();
        var module = new AgentsModule(announcer);
        var cpu = new CpuMeter();
        var busy = new[] { new Job("s1", "proj", "Working", true) };

        module.Update(Inputs(new[] { Claude }, Snap(), cpu, claude: busy), 0);
        module.Update(Inputs(new[] { Claude }, Snap(), cpu), 10_000);   // gone for one poll
        module.Update(Inputs(new[] { Claude }, Snap(), cpu, claude: busy), 15_000);
        module.Update(Inputs(new[] { Claude }, Snap(), cpu), 30_000);   // gone 15 s, under 20 s

        Assert.Empty(announcer.Toasts);
    }

    [Fact]
    public void Short_UnderDoneAfter_NoToast()
    {
        var announcer = new RecordingAnnouncer();
        var module = new AgentsModule(announcer);
        var cpu = new CpuMeter();
        var busy = new[] { new Job("s1", "proj", "Working", true) };

        module.Update(Inputs(new[] { Claude }, Snap(), cpu, claude: busy, doneAfter: 30), 0);
        module.Update(Inputs(new[] { Claude }, Snap(), cpu, claude: busy, doneAfter: 30), 10_000);  // ran 10 s
        module.Update(Inputs(new[] { Claude }, Snap(), cpu, doneAfter: 30), 40_000);                // gone 30 s

        Assert.Empty(announcer.Toasts);
    }

    [Fact]
    public void AgentsOff_NoStates_NoToasts_ClearsMemory()
    {
        var announcer = new RecordingAnnouncer();
        var module = new AgentsModule(announcer);
        var cpu = new CpuMeter();
        var busy = new[] { new Job("s1", "proj", "Working", true) };

        module.Update(Inputs(new[] { Claude }, Snap(), cpu, claude: busy), 0);
        module.Update(Inputs(new[] { Claude }, Snap(), cpu, claude: busy), 40_000);

        var changed = module.Update(Inputs(new[] { Claude }, Snap(), cpu, agentsOn: false, claude: busy), 45_000);

        Assert.True(changed);
        Assert.Empty(module.States);
        Assert.Empty(announcer.Toasts);

        // the memory is gone: with agents back on and the job gone, there is no finished job to announce
        module.Update(Inputs(new[] { Claude }, Snap(), cpu, doneAfter: 30), 70_000);
        Assert.Empty(announcer.Toasts);
    }

    [Fact]
    public void LocalModel_TitleIsModel_DetailLoaded()
    {
        var module = new AgentsModule(new RecordingAnnouncer());
        var cpu = new CpuMeter();
        var item = new WatchItem("lm-1", "LM Studio", WatchKind.App, BundleID: "ai.elementlabs.lmstudio");
        var procs = Snap(P(300, 1, "LM Studio.exe"));
        var models = new Dictionary<string, IReadOnlyList<string>>
        {
            ["lmstudio"] = new[] { "qwen3-8b", "llama-3" },
        };

        module.Update(Inputs(new[] { item }, procs, cpu, models: models), 0);

        var job = Assert.Single(Assert.Single(module.States).Jobs);
        Assert.Equal("qwen3-8b, llama-3", job.Title);
        Assert.Equal("Loaded qwen3-8b, llama-3", job.Detail);

        module.Update(Inputs(new[] { item }, procs, cpu), 2000);
        Assert.Equal("No model loaded", Assert.Single(Assert.Single(module.States).Jobs).Detail);
    }

    [Fact]
    public void Running_KeepsOnlyWorkingJobs()
    {
        var module = new AgentsModule(new RecordingAnnouncer());
        var cpu = new CpuMeter();
        var codex = new WatchItem("codex-1", "Codex", WatchKind.Codex);
        var claudeJobs = new[]
        {
            new Job("idle", "quiet", "Waiting", false),
            new Job("busy", "proj", "Working · 1 sec", true),
        };
        var codexJobs = new[] { new Job("c1", "repo", "Waiting", false) };

        module.Update(Inputs(new[] { Claude, codex }, Snap(), cpu, claude: claudeJobs, codex: codexJobs), 0);

        // working jobs come first in each state
        Assert.Equal(new[] { "busy", "idle" }, module.States[0].Jobs.Select(j => j.Id));

        var running = Assert.Single(module.Running);
        Assert.Equal("Claude", running.Item.Name);
        var job = Assert.Single(running.Jobs);
        Assert.Equal("busy", job.Id);
        Assert.True(job.Working);
    }

    [Fact]
    public void Update_ReturnsWhetherStatesChanged()
    {
        var module = new AgentsModule(new RecordingAnnouncer());
        var cpu = new CpuMeter();
        var jobs = new[] { new Job("s1", "proj", "Waiting", false) };

        Assert.True(module.Update(Inputs(new[] { Claude }, Snap(), cpu, claude: jobs), 0));
        Assert.False(module.Update(Inputs(new[] { Claude }, Snap(), cpu, claude: jobs), 1000));
    }

    [Fact]
    public void BuildItemsAndDisabledItems_AreSkipped()
    {
        var module = new AgentsModule(new RecordingAnnouncer());
        var cpu = new CpuMeter();
        var msbuild = new WatchItem("msb-1", "MSBuild", WatchKind.Process, Process: "msbuild", Build: true);
        var disabled = Claude with { Enabled = false };

        module.Update(Inputs(new[] { msbuild, disabled, WatchItems.BuildItem }, Snap(P(500, 1, "msbuild")), cpu,
            claude: new[] { new Job("s1", "proj", "Waiting", false) }), 0);

        Assert.Empty(module.States);
    }

    [Fact]
    public void BuildMode_TakesOnlyBuildItems_AndAnnouncesThemByName()
    {
        var announcer = new RecordingAnnouncer();
        var module = new AgentsModule(announcer, build: true);
        var cpu = new CpuMeter();
        var msbuild = new WatchItem("msb-1", "MSBuild", WatchKind.Process, Process: "msbuild", Build: true, Threshold: 20);
        var disabled = msbuild with { Id = "msb-2", Enabled = false };
        IReadOnlyList<WatchItem> items = new[] { msbuild, disabled, Claude, WatchItems.BuildItem };

        module.Update(Inputs(items, Snap(P(500, 1, "msbuild")), cpu, doneAfter: 1), 0);
        module.Update(Inputs(items, Snap(P(500, 1, "msbuild", Ticks(50, 1000))), cpu, doneAfter: 1), 1000);
        module.Update(Inputs(items, Snap(P(500, 1, "msbuild", Ticks(50, 1000) * 2)), cpu, doneAfter: 1), 2000);
        module.Update(Inputs(items, Snap(P(500, 1, "msbuild", Ticks(50, 1000) * 2)), cpu, doneAfter: 1), 4000);
        // last busy at 2 s, still counted as working until 4 s; gone for 20 s after its last working sample (4 s)
        module.Update(Inputs(items, Snap(P(500, 1, "msbuild", Ticks(50, 1000) * 2)), cpu, doneAfter: 1), 25_000);

        // only the enabled build item is a state; the Claude item and the disabled copy are left out
        Assert.Equal("msb-1", Assert.Single(module.States).Item.Id);
        var toast = Assert.Single(announcer.Toasts);
        Assert.Equal("MSBuild done", toast.Title);
        Assert.Equal("ci", toast.Module);  // the build instance belongs to Build & CI
    }

    [Fact]
    public void AgentsMode_StillSkipsBuildItems()
    {
        var module = new AgentsModule(new RecordingAnnouncer());
        var cpu = new CpuMeter();
        var msbuild = new WatchItem("msb-1", "MSBuild", WatchKind.Process, Process: "msbuild", Build: true);

        module.Update(Inputs(new[] { msbuild, Claude }, Snap(P(500, 1, "msbuild")), cpu,
            claude: new[] { new Job("s1", "proj", "Waiting", false) }), 0);

        Assert.Equal(new[] { "claude-1" }, module.States.Select(s => s.Item.Id).ToArray());
    }

    [Fact]
    public void ItemState_ValueEquality_ComparesJobs()
    {
        var job = new Job("s1", "proj", "Waiting", false);
        var a = new ItemState(Claude, new[] { job });
        var b = new ItemState(Claude, new[] { job });
        var c = new ItemState(Claude, new[] { job with { Detail = "Working" } });

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, c);
    }
}
