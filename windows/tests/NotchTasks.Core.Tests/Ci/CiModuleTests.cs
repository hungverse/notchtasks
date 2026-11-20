using System.Text.Json.Nodes;
using NotchTasks.Core.Agents;
using NotchTasks.Core.Ci;
using NotchTasks.Core.Modules;
using NotchTasks.Core.Notch;
using Xunit;

namespace NotchTasks.Core.Tests.Ci;

// Ported from electron/src/main/main.js (scanWorkers, announceFinished, computeAll, pollRunners, githubFinished) and the
// Mac's scanRunnerLog, githubFinished, build jobs and worker log reads (Model.swift). Pure: fixture logs, a manual clock,
// an in-memory state file, a fake log reader and a recording announcer. No OS calls.
[Collection("L10n")]
public class CiModuleTests
{
    public CiModuleTests() => AsksFixtures.InitEnglish();

    private sealed class Recorder : IAnnouncer
    {
        public List<Toast> Toasts { get; } = new();

        public void Announce(Toast t, bool notify = true) => Toasts.Add(t);
    }

    /// The state file: key to value, copied on read and write like the real file.
    private sealed class State
    {
        private readonly Dictionary<string, JsonNode?> values = new(StringComparer.Ordinal);

        public JsonNode? Get(string key) => values.TryGetValue(key, out var v) ? v?.DeepClone() : null;

        public void Set(string key, JsonNode value) => values[key] = value.DeepClone();

        public JsonArray Array(string key) => (JsonArray)values[key]!;
    }

    private static readonly ProcSnapshot NoProcs = new(Array.Empty<ProcInfo>());
    private static readonly ProcSnapshot RunnerProcs = new(new[] { new ProcInfo(1, 0, "gitlab-runner.exe", 1000, 0) });

    private static readonly string Received7 =
        "Feb 10 10:00:01 received job=7 job_name=\"unit tests\" repo_url=\"https://gitlab.com/shop/api.git\" runner=ab12cd";
    private static readonly string Succeeded7 = "Feb 10 10:02:30 job=7 Job succeeded duration=2m29s";
    private static readonly string Failed8 =
        "Feb 10 10:03:00 job=8 Job failed failure_reason=\"script_failure\" exit_code=1 project_full_path=\"shop/web\" runner=ab12cd";
    private static readonly string Failed9 = Failed8.Replace("job=8", "job=9", StringComparison.Ordinal);

    private static CiModule Make(
        out Recorder announcer,
        out State state,
        out List<List<string>> seen,
        Func<string, string?>? readTail = null)
    {
        announcer = new Recorder();
        state = new State();
        seen = new List<List<string>>();
        var captured = seen;
        return new CiModule(announcer, state.Get, state.Set, tools => captured.Add(tools.ToList()), readTail ?? (_ => string.Empty));
    }

    private static CiInputs Input(
        ProcSnapshot? snapshot = null,
        IReadOnlyList<(string File, long MtimeMs, string Tail)>? logs = null,
        IReadOnlyList<Job>? dev = null,
        IReadOnlyList<Job>? files = null,
        IReadOnlyList<Job>? github = null,
        IReadOnlyList<GhRun>? finished = null,
        IReadOnlyList<(int Pid, string? Exe, string? LogFile)>? workers = null,
        double doneAfter = 30,
        Func<ProcInfo, string?>? cwd = null,
        Func<ProcInfo, Job>? child = null) =>
        new(snapshot ?? NoProcs,
            dev ?? Array.Empty<Job>(),
            files ?? Array.Empty<Job>(),
            logs,
            workers ?? Array.Empty<(int Pid, string? Exe, string? LogFile)>(),
            github ?? Array.Empty<Job>(),
            finished ?? Array.Empty<GhRun>(),
            cwd ?? (_ => null),
            child ?? (p => new Job($"p{p.Pid}", "child", "Waiting", false)),
            doneAfter);

    private static GhRun Run(long id, string conclusion, string title = "fix build") => new(
        Id: id, Repo: "shop/api", Workflow: "ci", Title: title, Status: "completed", Conclusion: conclusion,
        Url: $"https://github.com/shop/api/actions/runs/{id}", StartedMs: 0, Step: null, Progress: null);

    [Fact]
    public void GitLabJobPassed_ToastsPassedWithUrl()
    {
        var module = Make(out var announcer, out _, out _);

        module.Update(Input(RunnerProcs, logs: new[] { ("a.log", 1000L, Received7) }), 1000);
        module.Update(Input(RunnerProcs, logs: new[] { ("a.log", 2000L, Received7 + "\n" + Succeeded7) }), 2000);

        var toast = Assert.Single(announcer.Toasts);
        Assert.Equal("seal", toast.Glyph);
        Assert.Equal("#30D158", toast.Tint);
        Assert.Equal("CI passed: unit tests", toast.Title);
        Assert.Equal("shop/api", toast.Subtitle);
        Assert.Equal("ciPassed", toast.Event);
        Assert.Equal("https://gitlab.com/shop/api/-/jobs/7", toast.Url);
        Assert.Equal("ci", toast.Module);
    }

    [Fact]
    public void GitLabJobFailed_ToastsFailedWithViewLog()
    {
        var module = Make(out var announcer, out _, out _);
        var failed7 = Failed8.Replace("job=8", "job=7", StringComparison.Ordinal);

        module.Update(Input(RunnerProcs, logs: new[] { ("a.log", 1000L, Received7) }), 1000);
        module.Update(Input(RunnerProcs, logs: new[] { ("a.log", 2000L, Received7 + "\n" + failed7) }), 2000);

        var toast = Assert.Single(announcer.Toasts);
        Assert.Equal("octagon", toast.Glyph);
        Assert.Equal("#FF453A", toast.Tint);
        Assert.Equal("CI failed: unit tests", toast.Title);
        Assert.Equal("shop/api", toast.Subtitle);
        Assert.Equal("ciFailed", toast.Event);
        Assert.Equal("View log", toast.Action);
        Assert.Equal("https://gitlab.com/shop/api/-/jobs/7", toast.Url);
    }

    [Fact]
    public void GitLabJobGoneWithoutResult_NoToast()
    {
        var module = Make(out var announcer, out _, out _);

        module.Update(Input(RunnerProcs, logs: new[] { ("a.log", 1000L, Received7) }), 1000);
        module.Update(Input(RunnerProcs, logs: new[] { ("a.log", 2000L, "no result line here") }), 2000);

        Assert.Empty(announcer.Toasts);
    }

    [Fact]
    public void GitLabLogOlderThan5Min_NoRunningRows()
    {
        var module = Make(out _, out _, out _);

        // the log was last written 6 min ago: the runner died mid-job, so the job is not shown as running
        module.Update(Input(RunnerProcs, logs: new[] { ("a.log", 0L, Received7) }), 6 * 60_000L);

        Assert.Null(module.BuildState);
    }

    [Fact]
    public void GitLabLogUnchanged_NotReparsed()
    {
        var module = Make(out _, out _, out _);

        module.Update(Input(RunnerProcs, logs: new[] { ("a.log", 1000L, Received7) }), 1000);
        // same mtime, and a tail that no longer holds the line: the cached parse is kept
        module.Update(Input(RunnerProcs, logs: new[] { ("a.log", 1000L, "") }), 2000);

        Assert.Contains(module.BuildState!.Jobs, j => j.Id == "gl:7" && j.Detail == "unit tests");
    }

    [Fact]
    public void GitLabPairedRow_DetailIsNameAndChildDetail()
    {
        var module = Make(out _, out _, out _);
        var snapshot = new ProcSnapshot(new[] { new ProcInfo(1, 0, "gitlab-runner.exe", 1000, 0), new ProcInfo(50, 1, "git.exe", 5000, 0) });

        module.Update(Input(
            snapshot,
            logs: new[] { ("a.log", 1000L, Received7) },
            child: p => new Job($"p{p.Pid}", "git", "▶ git · 40% CPU", true)), 1000);

        var row = Assert.Single(module.BuildState!.Jobs);
        Assert.Equal("gl:7", row.Id);
        Assert.Equal("unit tests · ▶ git · 40% CPU", row.Detail);
        Assert.Equal("shop/api", row.Title);
        Assert.Equal("https://gitlab.com/shop/api/-/jobs/7", row.Url);
    }

    [Fact]
    public void NoRunningJobs_OneRowPerChild_TitledFromCwdElseGitLab()
    {
        var module = Make(out _, out _, out _);
        var snapshot = new ProcSnapshot(new[]
        {
            new ProcInfo(1, 0, "gitlab-runner.exe", 1000, 0),
            new ProcInfo(50, 1, "bash.exe", 5000, 0),
            new ProcInfo(51, 1, "bash.exe", 6000, 0),
        });
        var cwds = new Dictionary<int, string?> { [50] = @"C:\gl\builds\tok\1\shop\api" };

        module.Update(Input(snapshot, cwd: p => cwds.GetValueOrDefault(p.Pid)), 1000);

        var jobs = module.BuildState!.Jobs;
        Assert.Equal(new[] { "gl:50", "gl:51" }, jobs.Select(j => j.Id).ToArray());
        Assert.Equal("shop/api", jobs[0].Title);
        Assert.Equal("GitLab", jobs[1].Title);
        Assert.Equal("Waiting", jobs[1].Detail);
    }

    [Fact]
    public void FirstRun_SeedsOldFailuresAsDismissed()
    {
        var module = Make(out _, out var state, out _);

        module.Update(Input(RunnerProcs, logs: new[] { ("a.log", 1000L, Failed8) }), 1000);

        Assert.Empty(module.Alerts);
        Assert.Equal(new[] { "8" }, state.Array("dismissedJobs").Select(n => n!.GetValue<string>()).ToArray());
    }

    [Fact]
    public void LaterFailure_AlertsOnce()
    {
        var module = Make(out _, out var state, out _);
        state.Set("dismissedJobs", new JsonArray());

        module.Update(Input(RunnerProcs, logs: new[] { ("a.log", 1000L, Received7) }), 1000);
        module.Update(Input(RunnerProcs, logs: new[] { ("a.log", 2000L, Failed8) }), 2000);
        module.Update(Input(RunnerProcs, logs: new[] { ("a.log", 2000L, Failed8) }), 3000);
        module.Update(Input(RunnerProcs, logs: new[] { ("a.log", 4000L, Failed8 + "\n" + Failed8) }), 4000);

        var alert = Assert.Single(module.Alerts);
        Assert.Equal("8", alert.Id);
        Assert.Equal("job 8", alert.Name);
        Assert.Equal("shop/web", alert.Project);
        Assert.Equal("script_failure · exit 1", alert.Reason);
        Assert.Equal("https://gitlab.com/shop/web/-/jobs/8", alert.Url);
    }

    [Fact]
    public void GitLabAlerts_NewestFirst()
    {
        var module = Make(out _, out var state, out _);
        state.Set("dismissedJobs", new JsonArray());

        module.Update(Input(RunnerProcs, logs: new[] { ("a.log", 1000L, Failed8 + "\n" + Failed9) }), 1000);

        Assert.Equal(new[] { "9", "8" }, module.Alerts.Select(a => a.Id).ToArray());
    }

    [Fact]
    public void Alerts_CachedUntilAnAlertChanges()
    {
        var module = Make(out _, out _, out _);

        module.Update(Input(finished: new[] { Run(42, "failure") }), 1000);
        var first = module.Alerts;
        module.Update(Input(), 2000);
        Assert.Same(first, module.Alerts);

        module.Dismiss("gh-42");
        Assert.NotSame(first, module.Alerts);
        Assert.Empty(module.Alerts);
    }

    [Fact]
    public void Dismiss_KeepsLast1000()
    {
        var module = Make(out _, out var state, out _);
        var old = new JsonArray();
        for (var i = 0; i < 1000; i++)
        {
            old.Add($"old{i}");
        }
        state.Set("dismissedJobs", old);
        module.Update(Input(RunnerProcs, logs: new[] { ("a.log", 1000L, Failed8) }), 1000);
        Assert.Single(module.Alerts);

        module.Dismiss("8");

        Assert.Empty(module.Alerts);
        var kept = state.Array("dismissedJobs").Select(n => n!.GetValue<string>()).ToList();
        Assert.Equal(1000, kept.Count);
        Assert.Equal("old1", kept[0]);
        Assert.Equal("8", kept[^1]);
    }

    [Fact]
    public void GitHubFailure_AlertAndToast()
    {
        var module = Make(out var announcer, out _, out _);

        module.Update(Input(finished: new[] { Run(42, "failure", "fix build") }), 1000);

        var toast = Assert.Single(announcer.Toasts);
        Assert.Equal("octagon", toast.Glyph);
        Assert.Equal("CI failed: ci", toast.Title);
        Assert.Equal("shop/api", toast.Subtitle);
        Assert.Equal("ciFailed", toast.Event);
        Assert.Equal("View log", toast.Action);
        Assert.Equal("https://github.com/shop/api/actions/runs/42", toast.Url);

        var alert = Assert.Single(module.Alerts);
        Assert.Equal(new JobAlert("gh-42", "ci", "shop/api", "fix build", "https://github.com/shop/api/actions/runs/42"), alert);
    }

    [Theory]
    [InlineData("timed_out")]
    [InlineData("startup_failure")]
    public void GitHubTimedOutOrStartupFailure_ToastAndAlert(string conclusion)
    {
        var module = Make(out var announcer, out _, out _);

        module.Update(Input(finished: new[] { Run(42, conclusion) }), 1000);

        Assert.Equal("octagon", Assert.Single(announcer.Toasts).Glyph);
        Assert.Equal("gh-42", Assert.Single(module.Alerts).Id);
    }

    [Fact]
    public void GitHubFailure_AlreadyDismissed_StillToastsButGivesNoAlert()
    {
        var module = Make(out var announcer, out var state, out _);
        var dismissed = new JsonArray();
        dismissed.Add("gh-42");
        state.Set("dismissedJobs", dismissed);

        module.Update(Input(finished: new[] { Run(42, "failure") }), 1000);

        Assert.Single(announcer.Toasts);
        Assert.Empty(module.Alerts);
    }

    [Fact]
    public void GitHubAlerts_NewestFirst()
    {
        var module = Make(out _, out _, out _);

        module.Update(Input(finished: new[] { Run(42, "failure") }), 1000);
        module.Update(Input(finished: new[] { Run(43, "failure") }), 2000);

        Assert.Equal(new[] { "gh-43", "gh-42" }, module.Alerts.Select(a => a.Id).ToArray());
    }

    [Fact]
    public void GitHubSuccess_ToastOnly()
    {
        var module = Make(out var announcer, out _, out _);

        module.Update(Input(finished: new[] { Run(43, "success") }), 1000);

        var toast = Assert.Single(announcer.Toasts);
        Assert.Equal("seal", toast.Glyph);
        Assert.Equal("CI passed: ci", toast.Title);
        Assert.Equal("ciPassed", toast.Event);
        Assert.Empty(module.Alerts);
    }

    [Fact]
    public void WorkerGone_ToastsFromLog()
    {
        var logs = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Worker_42.log"] = "[10:00:00Z] Job result after all job steps finish: Succeeded\n\"jobDisplayName\": \"build app\"\n" +
                                @"C:\actions-runner\_work\shop-web\shop-web\src",
        };
        var module = Make(out var announcer, out _, out _, file => logs.GetValueOrDefault(file));

        module.Update(Input(workers: new[] { (42, (string?)null, (string?)"Worker_42.log") }), 1000);
        Assert.Empty(announcer.Toasts);

        module.Update(Input(), 2000);

        var toast = Assert.Single(announcer.Toasts);
        Assert.Equal("seal", toast.Glyph);
        Assert.Equal("CI passed: build app", toast.Title);
        Assert.Equal("shop-web", toast.Subtitle);
        Assert.Equal("ciPassed", toast.Event);
        Assert.Null(toast.Url);
    }

    [Fact]
    public void WorkerFailed_ToastsOctagon()
    {
        var module = Make(out var announcer, out _, out _,
            _ => "Job result after all job steps finish: Failed\n\"jobDisplayName\": \"lint\"");

        module.Update(Input(workers: new[] { (42, (string?)null, (string?)"Worker_42.log") }), 1000);
        module.Update(Input(), 2000);

        var toast = Assert.Single(announcer.Toasts);
        Assert.Equal("octagon", toast.Glyph);
        Assert.Equal("CI failed: lint", toast.Title);
    }

    [Fact]
    public void Worker_ReadsItsLog_OnSight_EveryTenthWhileUnnamed_AndOnceWhenGone()
    {
        var reads = 0;
        var module = Make(out _, out _, out _, _ =>
        {
            reads++;
            return "no job name yet";
        });
        var worker = new[] { (42, (string?)null, (string?)"Worker_42.log") };

        for (var i = 1; i <= 25; i++)
        {
            module.Update(Input(workers: worker), i * 1000L);
        }
        // first sight (update 1), then the 10th and the 20th update, while the job name is unknown
        Assert.Equal(3, reads);

        module.Update(Input(), 26_000);
        Assert.Equal(4, reads);   // gone: its result is read once
    }

    [Fact]
    public void Worker_NamedLog_ReadOnceWhileLive_AndOnceWhenGone()
    {
        var reads = 0;
        var module = Make(out _, out _, out _, _ =>
        {
            reads++;
            return "\"jobDisplayName\": \"build app\"";
        });
        var worker = new[] { (42, (string?)null, (string?)"Worker_42.log") };

        for (var i = 1; i <= 25; i++)
        {
            module.Update(Input(workers: worker), i * 1000L);
        }
        Assert.Equal(1, reads);   // the name is known: no more reads while it lives

        module.Update(Input(), 26_000);
        Assert.Equal(2, reads);
    }

    [Fact]
    public void DevJobDone_ToastsAfterDoneAfter()
    {
        var module = Make(out var announcer, out _, out _);
        var job = new Job("dev:10", "Gradle · app", "Build · 1:00", true, Tool: "Gradle");

        module.Update(Input(dev: new[] { job }, doneAfter: 5), 0);
        module.Update(Input(dev: new[] { job }, doneAfter: 5), 10_000);
        module.Update(Input(doneAfter: 5), 40_000);

        var toast = Assert.Single(announcer.Toasts);
        Assert.Equal("check", toast.Glyph);
        Assert.Equal("Gradle · app done", toast.Title);
        Assert.Equal("10 sec", toast.Subtitle);
        Assert.Equal("taskDone", toast.Event);
        Assert.Equal("#30D158", toast.Tint);
    }

    [Fact]
    public void DevJobShort_NoToast()
    {
        var module = Make(out var announcer, out _, out _);
        var job = new Job("dev:10", "Gradle · app", "Build · 0:02", true, Tool: "Gradle");

        module.Update(Input(dev: new[] { job }, doneAfter: 5), 0);
        module.Update(Input(dev: new[] { job }, doneAfter: 5), 2000);
        module.Update(Input(doneAfter: 5), 40_000);

        Assert.Empty(announcer.Toasts);
    }

    [Fact]
    public void BuildState_OrderAndNullWhenEmpty()
    {
        var module = Make(out _, out _, out _);
        Assert.Null(module.BuildState);

        var dev = new Job("dev:10", "Gradle · app", "Build · 1:00", true, Tool: "Gradle");
        var file = new Job("file:C:\\Users\\a\\Downloads\\x.zip", "Tải file · x.zip", "10%", true, Tool: "Tải file");
        var gh = new Job("gh:5", "shop/api · ci", "Waiting for a runner", true, Tool: "GitHub Actions");
        var worker = (42, (string?)null, (string?)"Worker_42.log");

        module.Update(Input(
            snapshot: RunnerProcs,
            logs: new[] { ("a.log", 1000L, Received7) },
            dev: new[] { dev },
            files: new[] { file },
            github: new[] { gh },
            workers: new[] { worker }), 1000);

        var state = module.BuildState!;
        Assert.Equal(WatchItems.BuildItem, state.Item);
        Assert.Equal(new[] { "dev:10", "file:C:\\Users\\a\\Downloads\\x.zip", "gl:7", "gh:5", "ghw:42" }, state.Jobs.Select(j => j.Id).ToArray());
    }

    [Fact]
    public void SeenTools_Reported()
    {
        var module = Make(out _, out _, out var seen);
        var dev = new Job("dev:10", "Gradle · app", "Build · 1:00", true, Tool: "Gradle");
        var gh = new Job("gh:5", "shop/api · ci", "Waiting", true, Tool: "GitHub Actions");

        module.Update(Input(dev: new[] { dev }, github: new[] { gh }), 1000);

        Assert.Equal(new[] { "Gradle", "GitHub Actions" }, seen.Last());
    }

    [Fact]
    public void SeenTools_CalledOnlyWhenTheSetChanges()
    {
        var module = Make(out _, out _, out var seen);
        var gradle = new Job("dev:10", "Gradle · app", "Build · 1:00", true, Tool: "Gradle");
        var gh = new Job("gh:5", "shop/api · ci", "Waiting", true, Tool: "GitHub Actions");

        module.Update(Input(dev: new[] { gradle }), 1000);
        module.Update(Input(dev: new[] { gradle }), 2000);
        Assert.Single(seen);

        module.Update(Input(dev: new[] { gradle }, github: new[] { gh }), 3000);
        Assert.Equal(2, seen.Count);
        Assert.Equal(new[] { "Gradle", "GitHub Actions" }, seen[1]);
    }

    [Fact]
    public void DismissedJobs_ReadFromStateOnce_NotPerScan()
    {
        var state = new State();
        state.Set("dismissedJobs", new JsonArray());
        var reads = 0;
        var module = new CiModule(new Recorder(), key => { reads++; return state.Get(key); }, state.Set, _ => { }, _ => string.Empty);

        module.Update(Input(RunnerProcs, logs: new[] { ("a.log", 1000L, Failed8) }), 1000);
        var afterFirst = reads;
        module.Update(Input(RunnerProcs, logs: new[] { ("a.log", 2000L, Failed8 + "\n" + Failed9) }), 2000);
        module.Update(Input(finished: new[] { Run(42, "failure") }), 3000);
        module.Update(Input(RunnerProcs, logs: new[] { ("a.log", 4000L, Failed9) }), 4000);
        Assert.Equal(afterFirst, reads);

        module.Dismiss("9");
        Assert.Equal(afterFirst, reads);
        Assert.Equal(new[] { "9" }, state.Array("dismissedJobs").Select(n => n!.GetValue<string>()).ToArray());
    }

    [Fact]
    public void Clear_ForgetsAlertsAndRows()
    {
        var module = Make(out _, out _, out _);
        module.Update(Input(finished: new[] { Run(42, "failure") }), 1000);
        Assert.Single(module.Alerts);

        module.Clear();

        Assert.Empty(module.Alerts);
        Assert.Null(module.BuildState);
    }
}
