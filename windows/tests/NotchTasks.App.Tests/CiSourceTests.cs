using System.IO;
using System.Text.Json;
using System.Threading;
using System.Windows.Threading;
using NotchTasks.Core;
using NotchTasks.Core.Agents;
using NotchTasks.Core.Ci;
using NotchTasks.Core.Modules;
using NotchTasks.Core.Notch;
using NotchTasks.Core.Settings;
using NotchTasks.Services.Modules;
using Xunit;

namespace NotchTasks.AppTests;

/// The Build & CI source: its timer runs only while it is started, a tick's reads land in the module on the UI thread, the
/// runner logs and GitHub are read on their own cadence, a git push starts watching its repo, and stopping drops a read in flight.
[Collection("L10n")]
public sealed class CiSourceTests
{
    private const string GitHubRunsRoute = "/actions/runs?";

    /// Records every toast the module makes.
    private sealed class RecordingAnnouncer : IAnnouncer
    {
        public List<Toast> Toasts { get; } = new();

        public void Announce(Toast t, bool notify = true) => Toasts.Add(t);
    }

    /// A process table the test sets: the processes, their command lines and working folders, and an optional gate that holds
    /// the snapshot until the test releases it (for a read in flight).
    private sealed class FakeProcesses : IProcessReader
    {
        public List<ProcInfo> Procs { get; } = new();

        public Dictionary<int, string> Lines { get; } = new();

        public Dictionary<int, string> Folders { get; } = new();

        public ManualResetEventSlim? Hold { get; set; }

        /// The thread each snapshot was read on (the scan must not read on the UI thread).
        public List<int> SnapshotThreads { get; } = new();

        public ProcSnapshot Snapshot()
        {
            SnapshotThreads.Add(Environment.CurrentManagedThreadId);
            Hold?.Wait();
            return new ProcSnapshot(Procs.ToArray());
        }

        public string? CommandLine(ProcInfo p) => Lines.TryGetValue(p.Pid, out var line) ? line : null;

        public string? Cwd(ProcInfo p) => Folders.TryGetValue(p.Pid, out var folder) ? folder : null;

        public string? ImagePath(ProcInfo p) => null;
    }

    /// A GitHub seam that answers every route with no runs, and records the routes asked.
    private sealed class FakeGitHub : IGitHubApi
    {
        public List<string> Routes { get; } = new();

        /// When set, a request is sent but never answered (a poll that never ends).
        public bool Hang { get; set; }

        public Task<JsonDocument?> Get(string route, string? token)
        {
            Routes.Add(route);
            if (Hang)
            {
                return new TaskCompletionSource<JsonDocument?>().Task;
            }
            return Task.FromResult<JsonDocument?>(JsonDocument.Parse("{\"workflow_runs\":[]}"));
        }

        public Task<string?> Token() => Task.FromResult<string?>(null);

        public int RunListings() => Routes.Count(r => r.Contains(GitHubRunsRoute, StringComparison.Ordinal));
    }

    /// The clock of a test: real time at the start, moved only by the test.
    private sealed class Clock
    {
        public long Ms { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    private static CiReaders Readers(TempRoot root, IProcessReader processes, IGitHubApi github, Clock clock) => new(
        processes,
        github,
        Path.Combine(root.Folder, "home"),
        Path.Combine(root.Folder, "programdata"),
        Path.Combine(root.Folder, "gitlab-fallback"),
        Path.Combine(root.Folder, "runner"),
        () => clock.Ms);

    private static (CiSource Source, CiModule Module, RecordingAnnouncer Announcer) Make(
        TempRoot root, SettingsStore settings, CiReaders readers, Action publish)
    {
        var announcer = new RecordingAnnouncer();
        var module = new CiModule(announcer, CiState.Get, CiState.Set, settings.AddSeenTools,
            f => RunnerLogs.ReadTail(f, 2_000_000));
        var build = new AgentsModule(announcer, build: true);
        return (new CiSource(module, build, settings, readers, publish), module, announcer);
    }

    /// Runs one tick: the read starts on the background, and the test waits until it has been applied.
    private static void Tick(CiSource source)
    {
        Fixture.Call(source, "Tick");
        Fixture.PumpUntil(() => !Fixture.Field<bool>(source, "_busy"));
    }

    /// A GitHub Actions worker: its exe under <root>\runner\bin, its log under <root>\runner\_diag, and a job on repo myrepo.
    private static (int Pid, string Exe) AddWorker(TempRoot root, FakeProcesses processes)
    {
        var exe = Path.Combine(root.Folder, "runner", "bin", "Runner.Worker.exe");
        var log = Path.Combine(root.Folder, "runner", "_diag", "Worker_1.log");
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        File.WriteAllText(log,
            "Starting\n" +
            "workspace \"C:\\actions\\_work\\myrepo\\myrepo\"\n" +
            "\"jobDisplayName\": \"build\"\n");
        processes.Procs.Add(new ProcInfo(500, 1, "Runner.Worker.exe", 1, 0));
        processes.Lines[500] = $"\"{exe}\" spawnclient 1 2";
        return (500, exe);
    }

    private static string Checkout(TempRoot root)
    {
        var dir = Path.Combine(root.Folder, "checkout");
        Directory.CreateDirectory(Path.Combine(dir, ".git"));
        File.WriteAllText(Path.Combine(dir, ".git", "config"),
            "[core]\n\trepositoryformatversion = 0\n[remote \"origin\"]\n\turl = https://github.com/o/r.git\n");
        File.WriteAllText(Path.Combine(dir, ".git", "HEAD"), "ref: refs/heads/main\n");
        return dir;
    }

    private static void WithNoItems(SettingsStore settings) => settings.SetItems(Array.Empty<WatchItem>());

    /// A field of the source's GitHub feed (the per-start state the poll and the tick share).
    private static T FeedField<T>(CiSource source, string name) =>
        Fixture.Field<T>(Fixture.Field<object>(source, "_feed"), name);

    /// Adds a git process that pushes the checkout folder (its push is seen by the next tick's scan).
    private static void AddPush(TempRoot root, FakeProcesses processes, int pid)
    {
        processes.Procs.Add(new ProcInfo(pid, 1, "git.exe", pid, 0));
        processes.Lines[pid] = "git push origin main";
        processes.Folders[pid] = Checkout(root);
    }

    [Fact]
    public void Start_TimerOnlyWhileCiActive()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var settings = SettingsStore.Load(AppPaths.SettingsFile);
            WithNoItems(settings);
            var clock = new Clock();
            var (source, _, _) = Make(root, settings, Readers(root, new FakeProcesses(), new FakeGitHub(), clock), () => { });
            var timer = Fixture.Field<DispatcherTimer>(source, "_timer");

            Assert.False(timer.IsEnabled);
            source.Start();
            Assert.True(timer.IsEnabled);
            source.Stop();
            Assert.False(timer.IsEnabled);
        });
    }

    [Fact]
    public void Tick_FeedsModuleOnUiThread()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var settings = SettingsStore.Load(AppPaths.SettingsFile);
            WithNoItems(settings);
            var processes = new FakeProcesses();
            AddWorker(root, processes);
            var clock = new Clock();
            var uiThread = Environment.CurrentManagedThreadId;
            var publishOnUi = new List<bool>();
            var (source, module, _) = Make(root, settings, Readers(root, processes, new FakeGitHub(), clock),
                () => publishOnUi.Add(Dispatcher.CurrentDispatcher.CheckAccess()));

            source.Start();
            Tick(source);

            var job = Assert.Single(module.BuildState!.Jobs);
            Assert.Equal("myrepo", job.Title);
            Assert.Equal("build", job.Detail);
            Assert.Equal("GitHub Actions", job.Tool);
            Assert.NotEmpty(publishOnUi);
            Assert.All(publishOnUi, onUi => Assert.True(onUi));
            // the scan itself runs off the UI thread
            Assert.NotEmpty(processes.SnapshotThreads);
            Assert.All(processes.SnapshotThreads, thread => Assert.NotEqual(uiThread, thread));
            source.Stop();
        });
    }

    [Fact]
    public void RunnerLogs_ReadEveryThirdTickOnlyWithRunner()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var settings = SettingsStore.Load(AppPaths.SettingsFile);
            WithNoItems(settings);
            var processes = new FakeProcesses();
            var clock = new Clock();
            var (source, module, announcer) = Make(root, settings, Readers(root, processes, new FakeGitHub(), clock), () => { });
            var logs = Path.Combine(root.Folder, "programdata", "gitlab-runner");
            Directory.CreateDirectory(logs);
            var log = Path.Combine(logs, "runner.log");
            File.WriteAllText(log, "Running job received job=101 job_name=compile repo_url=https://gitlab.com/grp/proj.git\n");
            var parsed = () => Fixture.Field<System.Collections.IDictionary>(module, "_logs");

            source.Start();
            // no gitlab-runner process and no GitLab job shown: the logs are not read on ticks 0, 1 and 2
            Tick(source);
            Tick(source);
            Tick(source);
            Assert.Empty(parsed());

            // a runner appears: tick 3 reads the log, and its running job shows
            processes.Procs.Add(new ProcInfo(300, 1, "gitlab-runner.exe", 1, 0));
            Tick(source);
            Assert.Single(parsed());
            Assert.Contains(module.BuildState!.Jobs, j => j.Id == "gl:101");

            // the log now says the job succeeded; ticks 4 and 5 do not read it, so the job still shows
            File.WriteAllText(log, "Running job received job=101 job_name=compile repo_url=https://gitlab.com/grp/proj.git\n"
                + "Job succeeded job=101\n");
            File.SetLastWriteTimeUtc(log, DateTime.UtcNow.AddMinutes(1));
            Tick(source);
            Tick(source);
            Assert.Contains(module.BuildState!.Jobs, j => j.Id == "gl:101");
            Assert.DoesNotContain(announcer.Toasts, t => t.Event == "ciPassed");

            // tick 6 reads it: the job is gone and its success is announced
            Tick(source);
            Assert.DoesNotContain(module.BuildState?.Jobs ?? Array.Empty<Job>(), j => j.Id == "gl:101");
            Assert.Contains(announcer.Toasts, t => t.Event == "ciPassed" && t.Title == "CI passed: compile");
            source.Stop();
        });
    }

    [Fact]
    public void GitHub_PolledEvery10thTickOnlyWhileWatching()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var settings = SettingsStore.Load(AppPaths.SettingsFile);
            WithNoItems(settings);
            var github = new FakeGitHub();
            var clock = new Clock();
            var processes = new FakeProcesses();
            var (source, _, _) = Make(root, settings, Readers(root, processes, github, clock), () => { });

            source.Start();
            // nothing watched: ticks 0 to 10 ask GitHub nothing, not even on the 10th tick
            for (var i = 0; i < 11; i++)
            {
                Tick(source);
            }
            Assert.Equal(0, github.RunListings());

            // a push seen on tick 11 starts watching; ticks 11 to 19 do not poll, tick 20 does, off the tick
            AddPush(root, processes, 400);
            for (var i = 0; i < 9; i++)
            {
                Tick(source);
            }
            Assert.Equal(0, github.RunListings());
            Tick(source);
            Fixture.PumpUntil(() => FeedField<int>(source, "Busy") == 0);
            Assert.Equal(1, github.RunListings());
            source.Stop();
        });
    }

    [Fact]
    public void Stop_ClearsAndDropsReadInFlight()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var settings = SettingsStore.Load(AppPaths.SettingsFile);
            WithNoItems(settings);
            var processes = new FakeProcesses();
            AddWorker(root, processes);
            var clock = new Clock();
            var published = 0;
            var (source, module, _) = Make(root, settings, Readers(root, processes, new FakeGitHub(), clock), () => published++);

            source.Start();
            Tick(source);
            Assert.NotNull(module.BuildState);

            source.Stop();
            Assert.Null(module.BuildState);
            Assert.Empty(module.Alerts);

            // a read that started before Stop lands after it: it is dropped
            var gate = new ManualResetEventSlim(false);
            processes.Hold = gate;
            source.Start();
            Fixture.Call(source, "Tick");
            source.Stop();
            var before = published;
            gate.Set();
            Fixture.PumpUntil(() => !Fixture.Field<bool>(source, "_busy"));

            Assert.Null(module.BuildState);
            Assert.Equal(before, published);
        });
    }

    [Fact]
    public void GitPush_StartsWatchingRepo()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var settings = SettingsStore.Load(AppPaths.SettingsFile);
            WithNoItems(settings);
            var processes = new FakeProcesses();
            processes.Procs.Add(new ProcInfo(400, 1, "git.exe", 1, 0));
            processes.Lines[400] = "git push origin main";
            processes.Folders[400] = Checkout(root);
            var clock = new Clock();
            var (source, _, _) = Make(root, settings, Readers(root, processes, new FakeGitHub(), clock), () => { });

            source.Start();
            Assert.False(FeedField<bool>(source, "Watching"));
            Tick(source);

            // the push is handed to the watcher by the poll, which runs off the tick
            Fixture.PumpUntil(() => FeedField<int>(source, "Busy") == 0 && FeedField<bool>(source, "Watching"));
            Assert.True(FeedField<bool>(source, "Watching"));
            source.Stop();
        });
    }

    [Fact]
    public void PollThatNeverEnds_DoesNotStopLaterTicks()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var settings = SettingsStore.Load(AppPaths.SettingsFile);
            WithNoItems(settings);
            var github = new FakeGitHub { Hang = true };
            var processes = new FakeProcesses();
            var clock = new Clock();
            var (source, module, _) = Make(root, settings, Readers(root, processes, github, clock), () => { });

            AddPush(root, processes, 400);
            source.Start();
            Tick(source);   // tick 0: the push is watched, and the poll starts (its request never answers)
            Fixture.PumpUntil(() => github.RunListings() == 1);
            Assert.Equal(1, FeedField<int>(source, "Busy"));

            // ticks 1 to 4 go on; a worker appears on tick 5 and its row shows at once
            for (var i = 1; i < 5; i++)
            {
                Tick(source);
            }
            AddWorker(root, processes);
            Tick(source);
            Assert.NotNull(module.BuildState);

            // tick 10 does not start a second poll while the first is still in flight
            for (var i = 6; i <= 10; i++)
            {
                Tick(source);
            }
            Assert.Equal(1, github.RunListings());
            Assert.Equal(1, FeedField<int>(source, "Busy"));
            source.Stop();
        });
    }
}
