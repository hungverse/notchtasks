using NotchTasks.Core.Agents;
using NotchTasks.Core.Ci;
using Xunit;

namespace NotchTasks.Core.Tests.Ci;

// Ported from electron/test/devscan.test.js: the TaskHistory and DevScanner cases, on fake snapshots and a manual clock.
// Plus the Windows cases: pid reuse, history once per task, cwd folder, git push, runner skip, quick commands.
[Collection("L10n")]
public sealed class DevScannerTests
{
    private const long UnixToFileTimeMs = 11644473600000L;

    public DevScannerTests()
    {
        L10n.Init(AppContext.BaseDirectory, "en");
    }

    // ---- helpers -------------------------------------------------------------

    /// A process: name as Windows gives it, start in unix ms, CPU in 100 ns ticks.
    private static ProcInfo Proc(int pid, int ppid, string name, long startMs, long cpuTicks = 0) =>
        new(pid, ppid, name, (startMs + UnixToFileTimeMs) * 10_000L, cpuTicks);

    private static ProcSnapshot Snap(params ProcInfo[] procs) => new(procs);

    /// argv per pid, from command lines; edited in place to simulate a reused pid.
    private sealed class Lines
    {
        public Dictionary<int, string> ByPid { get; } = new();

        /// How many times argv was asked for each pid.
        public Dictionary<int, int> Calls { get; } = new();

        public IReadOnlyList<string> Argv(ProcInfo p)
        {
            Calls[p.Pid] = Calls.GetValueOrDefault(p.Pid) + 1;
            return ByPid.TryGetValue(p.Pid, out var cmd) ? ProcTable.SplitArgs(cmd) : Array.Empty<string>();
        }
    }

    private static Func<ProcInfo, string?> NoCwd => _ => null;

    private static Job? Find(IReadOnlyList<Job> jobs, string id) => jobs.FirstOrDefault(j => j.Id == id);

    private const string NextBuild = @"""C:\Program Files\nodejs\node.exe"" C:\Users\hung\proj\node_modules\next\dist\bin\next build";
    private const string JestWorker = @"""C:\Program Files\nodejs\node.exe"" C:\Users\hung\proj\node_modules\next\dist\compiled\jest-worker\processChild.js";
    private const string MsBuild = @"""C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"" /t:Restore /p:Configuration=Release C:\src\Shop\Shop.sln";
    private const string Blender = @"""C:\Program Files\Blender Foundation\Blender 4.2\blender.exe"" -b C:\art\hall.blend -a";

    /// The processes of electron/test/fixtures/builds/win32-processes.json (CPU in 100 ns ticks, kernel + user).
    private static ProcInfo[] Fixture() =>
    [
        Proc(812, 640, "node.exe", 1731500600000, 5_000_000 + 91_000_000),
        Proc(2044, 812, "node.exe", 1731500601000, 100_000 + 4_000_000),
        Proc(3300, 1500, "MSBuild.exe", 1731501000000, 300_000 + 1_200_000),
        Proc(5120, 1500, "Blender.exe", 1731499000000, 2_000_000 + 500_000_000),
        Proc(6000, 1500, "claude.exe", 1731490000000, 10_000 + 20_000),
        Proc(6400, 1500, "git.exe", 1731501500000, 1000 + 2000),
    ];

    private static Lines FixtureArgv()
    {
        var lines = new Lines();
        lines.ByPid[812] = NextBuild;
        lines.ByPid[2044] = JestWorker;
        lines.ByPid[3300] = MsBuild;
        lines.ByPid[5120] = Blender;
        lines.ByPid[6000] = @"C:\Users\hung\AppData\Roaming\npm\claude.exe";
        lines.ByPid[6400] = "git push origin main";
        return lines;
    }

    // ---- TaskHistory -----------------------------------------------------------

    [Fact]
    public void TaskHistory_MedianOfLastRuns_RecordsAndKeepsFive()
    {
        var saved = new List<IReadOnlyDictionary<string, IReadOnlyList<double>>>();
        var loaded = new Dictionary<string, IReadOnlyList<double>> { ["k"] = new double[] { 10, 100, 60, 61 } };
        var h = new TaskHistory(loaded, s => saved.Add(s));

        Assert.Equal(61.0, h.Expected("k"));            // sorted [10,60,61,100], index 2
        Assert.Null(h.Expected("none"));

        h.Record("k", 3);                               // under 5 s: ignored
        Assert.Empty(saved);

        h.Record("k", 90);
        Assert.Equal(new double[] { 10, 100, 60, 61, 90 }, h.Runs["k"]);

        h.Record("k", 200);
        Assert.Equal(new double[] { 100, 60, 61, 90, 200 }, h.Runs["k"]);   // keeps the last 5
        Assert.Equal(2, saved.Count);
    }

    [Fact]
    public void TaskHistory_Over300Keys_TrimsTo250()
    {
        var loaded = new Dictionary<string, IReadOnlyList<double>>();
        for (var i = 0; i < 300; i++)
        {
            loaded[$"k{i}"] = new double[] { 30 };
        }
        var saved = 0;
        var h = new TaskHistory(loaded, _ => saved++);

        h.Record("new", 30);                            // 301 keys
        Assert.Equal(250, h.Runs.Count);
        Assert.Equal(1, saved);
    }

    [Fact]
    public void TaskHistory_SaveThrows_RunIsKeptAndTheFailureReportedOnce()
    {
        var reported = new List<Exception>();
        var saves = 0;
        var h = new TaskHistory(null, _ =>
        {
            saves++;
            throw new IOException("disk full");
        }, reported.Add);

        h.Record("k", 30);
        h.Record("k", 40);

        Assert.Equal(2, saves);
        Assert.Equal(new double[] { 30, 40 }, h.Runs["k"]);
        Assert.Single(reported);
        Assert.IsType<IOException>(reported[0]);
    }

    [Fact]
    public void TaskHistory_Estimate_ProgressAndLabel()
    {
        var (p1, l1) = TaskHistory.Estimate(30, 60);
        Assert.Equal(0.5, p1);
        Assert.Equal("0:30 · ≈ 30s" + L10n.L(" left"), l1);

        var (p2, l2) = TaskHistory.Estimate(70, 60);
        Assert.Equal(0.99, p2);
        Assert.Equal("1:10" + L10n.L(" · longer than usual"), l2);

        var (p3, l3) = TaskHistory.Estimate(5, null);
        Assert.Null(p3);
        Assert.Equal("0:05", l3);

        var (p4, l4) = TaskHistory.Estimate(5, 9);     // under 10 s: no estimate
        Assert.Null(p4);
        Assert.Equal("0:05", l4);
    }

    // ---- DevScanner: port -------------------------------------------------------

    [Fact]
    public void Scan_StartAndFinish_RecordsHistory()
    {
        var saved = new List<IReadOnlyDictionary<string, IReadOnlyList<double>>>();
        var scanner = new DevScanner(new TaskHistory(null, s => saved.Add(s)));
        var lines = FixtureArgv();
        var procs = Fixture();
        long t0 = 1731500600000 + 10_000;               // next build started 10 s ago

        var first = scanner.Scan(Snap(procs), lines.Argv, NoCwd, t0);
        var next = Find(first, "dev:812");
        Assert.NotNull(next);
        Assert.Equal("next · proj", next!.Title);
        Assert.Equal("next", next.Tool);
        Assert.StartsWith("build · 0:10", next.Detail);
        Assert.Null(Find(first, "dev:2044"));          // jest worker belongs to the build

        // a minute later the build process is gone
        var later = procs.Where(p => p.Pid != 812 && p.Pid != 2044).ToArray();
        var second = scanner.Scan(Snap(later), lines.Argv, NoCwd, t0 + 60_000);
        Assert.Null(Find(second, "dev:812"));
        Assert.Single(saved);
        Assert.Single(saved[0]["next|build|proj"]);
        Assert.True(saved[0]["next|build|proj"][0] >= 60);
    }

    [Fact]
    public void Scan_HistorySaveThrows_ScanReturnsAndTheEndedTaskIsRecordedOnce()
    {
        var saves = 0;
        var scanner = new DevScanner(new TaskHistory(null, _ =>
        {
            saves++;
            throw new IOException("disk full");
        }));
        var lines = FixtureArgv();
        var procs = Fixture();
        long t0 = 1731500600000 + 10_000;

        var first = scanner.Scan(Snap(procs), lines.Argv, NoCwd, t0);
        Assert.NotNull(Find(first, "dev:812"));

        // the build ends: the scan returns its rows although the save throws, and records the run once
        var later = procs.Where(p => p.Pid != 812 && p.Pid != 2044).ToArray();
        var second = scanner.Scan(Snap(later), lines.Argv, NoCwd, t0 + 60_000);
        Assert.Null(Find(second, "dev:812"));
        Assert.Equal(1, saves);

        // the next scan does not record it again
        var third = scanner.Scan(Snap(later), lines.Argv, NoCwd, t0 + 120_000);
        Assert.Null(Find(third, "dev:812"));
        Assert.Equal(1, saves);
    }

    [Fact]
    public void Scan_MsBuild_ShowsSolutionName()
    {
        var scanner = new DevScanner(new TaskHistory(null, _ => { }));
        var lines = FixtureArgv();
        var jobs = scanner.Scan(Snap(Fixture()), lines.Argv, NoCwd, 1731501000000 + 60_000);
        var ms = Find(jobs, "dev:3300");
        Assert.NotNull(ms);
        Assert.Equal("MSBuild · Shop", ms!.Title);
    }

    [Fact]
    public void Scan_BusyOnlyRenderApp_ShowsAfterTenSecondsOfLoad()
    {
        var scanner = new DevScanner(new TaskHistory(null, _ => { }));
        var lines = FixtureArgv();
        // Blender at 400 % CPU: 4 ms of CPU per ms of wall clock = 40 000 ticks per ms
        ProcSnapshot At(long cpuTicks) =>
            Snap(Fixture().Select(p => p.Pid == 5120 ? p with { CpuTicks = cpuTicks } : p).ToArray());

        IReadOnlyList<Job> jobs = Array.Empty<Job>();
        for (long t = 1000; t <= 14_000; t += 1000)
        {
            jobs = scanner.Scan(At((t - 1000) * 40_000), lines.Argv, NoCwd, t);
        }
        var rendered = Find(jobs, "app:5120");
        Assert.NotNull(rendered);
        Assert.Equal("Blender", rendered!.Tool);
        Assert.NotNull(Find(scanner.Scan(At(14_000 * 40_000), lines.Argv, NoCwd, 15_000), "app:5120"));

        // load stops: stays for 8 s after the last busy sample, then goes
        long idle = 14_000L * 40_000;
        Assert.NotNull(Find(scanner.Scan(At(idle), lines.Argv, NoCwd, 16_000), "app:5120"));
        Assert.Null(Find(scanner.Scan(At(idle), lines.Argv, NoCwd, 30_000), "app:5120"));
    }

    [Fact]
    public void Scan_GitPushRaisesPushedOnce_AndShowsAfterThreeSeconds()
    {
        var pushed = new List<string>();
        var scanner = new DevScanner(new TaskHistory(null, _ => { }));
        scanner.Pushed += pushed.Add;
        var lines = FixtureArgv();
        var procs = Snap(Fixture());
        Func<ProcInfo, string?> cwd = p => p.Pid == 6400 ? @"C:\src\shop" : null;
        long now = 1731501500000 + 1000;

        var jobs = scanner.Scan(procs, lines.Argv, cwd, now);
        Assert.Equal(new[] { @"C:\src\shop" }, pushed);
        Assert.Null(Find(jobs, "dev:6400"));            // 1 s old: too quick to show

        var later = scanner.Scan(procs, lines.Argv, cwd, now + 5000);
        var git = Find(later, "dev:6400");
        Assert.NotNull(git);
        Assert.Equal("Git · shop", git!.Title);
        scanner.Scan(procs, lines.Argv, cwd, now + 7000);
        Assert.Single(pushed);
    }

    // ---- DevScanner: Windows cases ----------------------------------------------

    [Fact]
    public void Scan_PidReusedByOtherName_Reclassifies()
    {
        var scanner = new DevScanner(new TaskHistory(null, _ => { }));
        var lines = new Lines();
        lines.ByPid[50] = "cargo build";
        long t0 = 1_731_500_000_000;
        // cargo started 20 s before t0: shown at t0, so a stale match would show again later
        Assert.NotNull(Find(scanner.Scan(Snap(Proc(50, 1, "cargo.exe", t0 - 20_000)), lines.Argv, NoCwd, t0), "dev:50"));

        // the pid now belongs to notepad, started 10 s before this scan: 14 s old, so a stale match would show
        lines.ByPid[50] = "notepad";
        var notepad = Snap(Proc(50, 1, "notepad.exe", t0 - 10_000));
        Assert.Empty(scanner.Scan(notepad, lines.Argv, NoCwd, t0 + 4000));
    }

    [Fact]
    public void Scan_PidReusedBySameName_ArgvAskedAgain()
    {
        var scanner = new DevScanner(new TaskHistory(null, _ => { }));
        var lines = new Lines();
        lines.ByPid[50] = "cargo build";
        long t0 = 1_731_500_000_000;
        Assert.NotNull(Find(scanner.Scan(Snap(Proc(50, 1, "cargo.exe", t0 - 20_000)), lines.Argv, NoCwd, t0), "dev:50"));
        var callsBefore = lines.Calls[50];

        // same exe name, new process (new start time): the arguments are read again and are not a finite task
        lines.ByPid[50] = "cargo run";
        Assert.Empty(scanner.Scan(Snap(Proc(50, 1, "cargo.exe", t0 - 10_000)), lines.Argv, NoCwd, t0 + 4000));
        Assert.True(lines.Calls[50] > callsBefore, "argv asked again for the reused pid");
    }

    [Fact]
    public void Scan_GradleClientPresent_HidesDaemonRow()
    {
        var scanner = new DevScanner(new TaskHistory(null, _ => { }));
        var lines = new Lines();
        lines.ByPid[60] = "java -cp gradle-wrapper.jar org.gradle.wrapper.GradleWrapperMain assembleDebug";
        lines.ByPid[61] = "java -cp gradle-launcher.jar org.gradle.launcher.daemon.bootstrap.GradleDaemon 8.5";
        long t0 = 1_731_500_000_000;
        Func<ProcInfo, string?> cwd = _ => @"C:\src\mobile-app\android";
        // the daemon works at 100 % (10 000 ticks per ms) from t0 on
        ProcSnapshot At(long t) => Snap(
            Proc(60, 1, "java.exe", t0 - 60_000, 0),
            Proc(61, 1, "java.exe", t0 - 60_000, (t - t0) * 10_000));

        scanner.Scan(At(t0), lines.Argv, cwd, t0);
        var jobs = scanner.Scan(At(t0 + 2000), lines.Argv, cwd, t0 + 2000);
        Assert.NotNull(Find(jobs, "dev:60"));
        Assert.Null(Find(jobs, "dev:61"));
    }

    [Fact]
    public void Scan_BusyDaemonAlone_ShowsWhileBusy_AndEightSecondsAfter()
    {
        var scanner = new DevScanner(new TaskHistory(null, _ => { }));
        var lines = new Lines();
        lines.ByPid[61] = "java -cp gradle-launcher.jar org.gradle.launcher.daemon.bootstrap.GradleDaemon 8.5";
        long t0 = 1_731_500_000_000;
        Func<ProcInfo, string?> cwd = _ => @"C:\src\mobile-app\android";
        // busy from t0 to t0 + 2 s at 100 %, then idle (CPU stops growing)
        ProcSnapshot At(long t) => Snap(Proc(61, 1, "java.exe", t0 - 60_000, Math.Min(t - t0, 2000) * 10_000));

        scanner.Scan(At(t0), lines.Argv, cwd, t0);
        Assert.NotNull(Find(scanner.Scan(At(t0 + 2000), lines.Argv, cwd, t0 + 2000), "dev:61"));
        Assert.NotNull(Find(scanner.Scan(At(t0 + 9000), lines.Argv, cwd, t0 + 9000), "dev:61"));   // 7 s after the last busy sample
        Assert.Null(Find(scanner.Scan(At(t0 + 10_000), lines.Argv, cwd, t0 + 10_000), "dev:61"));  // 8 s after: gone
    }

    [Fact]
    public void Scan_GitFetch_RaisesNoPushed()
    {
        var pushed = new List<string>();
        var scanner = new DevScanner(new TaskHistory(null, _ => { }));
        scanner.Pushed += pushed.Add;
        var lines = new Lines();
        lines.ByPid[70] = "git fetch origin";
        long t0 = 1_731_500_000_000;
        Func<ProcInfo, string?> cwd = _ => @"C:\src\repo";
        var procs = Snap(Proc(70, 1, "git.exe", t0 - 60_000));
        scanner.Scan(procs, lines.Argv, cwd, t0);
        scanner.Scan(procs, lines.Argv, cwd, t0 + 2000);
        Assert.Empty(pushed);
    }

    [Fact]
    public void Scan_TaskEnds_RecordsHistoryOnce()
    {
        var saved = new List<IReadOnlyDictionary<string, IReadOnlyList<double>>>();
        var scanner = new DevScanner(new TaskHistory(null, s => saved.Add(s)));
        var lines = new Lines();
        lines.ByPid[30] = "cargo build --release";
        long t0 = 1_731_500_000_000;
        Func<ProcInfo, string?> cwd = _ => @"C:\src\shop";

        // the cargo process starts at t0 and runs until t0 + 20 s, then is gone for three scans
        for (long t = t0 + 4000; t <= t0 + 18_000; t += 2000)
        {
            scanner.Scan(Snap(Proc(30, 1, "cargo.exe", t0)), lines.Argv, cwd, t);
        }
        for (long t = t0 + 20_000; t <= t0 + 24_000; t += 2000)
        {
            scanner.Scan(Snap(), lines.Argv, cwd, t);
        }

        Assert.Single(saved);
        var runs = saved[0]["Cargo|build|shop"];
        Assert.Single(runs);
        Assert.InRange(runs[0], 18, 22);
    }

    [Fact]
    public void Scan_CwdKnown_TitleUsesProjectFolder()
    {
        var scanner = new DevScanner(new TaskHistory(null, _ => { }));
        var lines = new Lines();
        lines.ByPid[7000] = "java -cp gradle-wrapper.jar org.gradle.wrapper.GradleWrapperMain assembleDebug";
        long t0 = 1_731_500_000_000;
        Func<ProcInfo, string?> cwd = _ => @"C:\src\mobile-app\android";
        var jobs = scanner.Scan(Snap(Proc(7000, 1, "java.exe", t0 - 60_000)), lines.Argv, cwd, t0);
        var gradle = Find(jobs, "dev:7000");
        Assert.NotNull(gradle);
        Assert.Equal("Gradle · mobile-app", gradle!.Title);
    }

    [Theory]
    [InlineData("gitlab-runner.exe")]
    [InlineData("runner.worker.exe")]
    public void Scan_UnderRunner_Skipped(string runnerExe)
    {
        var scanner = new DevScanner(new TaskHistory(null, _ => { }));
        var lines = new Lines();
        lines.ByPid[101] = @"""C:\Program Files\nodejs\node.exe"" C:\p\node_modules\npm\bin\npm-cli.js test";
        long t0 = 1_731_500_000_000;
        var underRunner = Snap(Proc(100, 1, runnerExe, t0 - 60_000), Proc(101, 100, "node.exe", t0 - 60_000));
        Assert.Null(Find(scanner.Scan(underRunner, lines.Argv, NoCwd, t0), "dev:101"));

        // the same process with no runner above it is a task
        var alone = Snap(Proc(101, 1, "node.exe", t0 - 60_000));
        Assert.NotNull(Find(scanner.Scan(alone, lines.Argv, NoCwd, t0), "dev:101"));
    }

    [Fact]
    public void Scan_QuickCommand_HiddenUnderThreeSeconds()
    {
        var scanner = new DevScanner(new TaskHistory(null, _ => { }));
        var lines = new Lines();
        lines.ByPid[40] = "cargo build";
        long t0 = 1_731_500_000_000;
        var procs = Snap(Proc(40, 1, "cargo.exe", t0 - 2000));
        Assert.Null(Find(scanner.Scan(procs, lines.Argv, NoCwd, t0), "dev:40"));
        Assert.NotNull(Find(scanner.Scan(procs, lines.Argv, NoCwd, t0 + 1000), "dev:40"));
    }
}
