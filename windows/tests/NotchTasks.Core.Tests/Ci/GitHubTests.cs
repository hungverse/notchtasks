using System.Globalization;
using System.Text.Json;
using NotchTasks.Core;
using NotchTasks.Core.Ci;
using Xunit;

namespace NotchTasks.Core.Tests.Ci;

// Ported from electron/test/github.test.js (remote forms, config parsing, checkout, runs and steps JSON, the jobs row,
// the queued row, the pushed record), plus the Windows cases from the Phase 3c brief (null API answers, the one-hour
// stop, the settled stop, the token throttle, one poll at a time, worktrees). The API is a fake: no network, no gh.
// Checkouts are temp folders. All tests share L10n's static state, so the class is in the "L10n" collection.
[Collection("L10n")]
public sealed class GitHubTests : IDisposable
{
    private const string RunsRoute = "repos/acme/app/actions/runs?branch=main&per_page=10";
    private const string JobsRoute = "repos/acme/app/actions/runs/9001/jobs";

    private readonly string root = Path.Combine(Path.GetTempPath(), "nt-gh-" + Guid.NewGuid().ToString("N"));
    private readonly FakeGitHubApi api = new();
    private long clock;

    public GitHubTests()
    {
        Directory.CreateDirectory(root);
        L10n.Init(AppContext.BaseDirectory, "en");
    }

    public void Dispose()
    {
        Directory.Delete(root, recursive: true);
    }

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static JsonDocument Doc(string json) => JsonDocument.Parse(json);

    private static long Ms(string iso) =>
        DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture).ToUnixTimeMilliseconds();

    private GitHubWatcher NewWatcher() => new(api, () => clock);

    /// A checkout of acme/app on `head` in a fresh folder under the temp root.
    private string MakeRepo(string remote = "git@github.com:acme/app.git", string head = "ref: refs/heads/main\n")
    {
        var repo = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        File.WriteAllText(Path.Combine(repo, ".git", "config"), $"[remote \"origin\"]\n\turl = {remote}\n");
        File.WriteAllText(Path.Combine(repo, ".git", "HEAD"), head);
        return repo;
    }

    /// One workflow run as the API sends it, for acme/app.
    private static string RunJson(long id, string status, string conclusion, string started = "2026-11-10T03:20:05Z") =>
        "{\"workflow_runs\":[{\"id\":" + id + ",\"name\":\"CI\",\"display_title\":\"Fix login redirect\",\"status\":\"" + status +
        "\",\"conclusion\":" + conclusion + ",\"html_url\":\"https://github.com/acme/app/actions/runs/" + id +
        "\",\"run_started_at\":\"" + started + "\",\"repository\":{\"full_name\":\"acme/app\"}}]}";

    private const string NoRuns = "{\"workflow_runs\":[]}";

    [Fact]
    public void RepoFromRemote_SshHttpsSuffixAndJunk()
    {
        Assert.Equal("acme/app", GitHubParse.RepoFromRemote("git@github.com:acme/app.git"));
        Assert.Equal("acme/app", GitHubParse.RepoFromRemote("https://github.com/acme/app"));
        Assert.Equal("acme/app", GitHubParse.RepoFromRemote("https://github.com/acme/app/"));
        Assert.Null(GitHubParse.RepoFromRemote("https://gitlab.com/acme/app.git"));
        Assert.Null(GitHubParse.RepoFromRemote("https://github.com/acme"));
    }

    [Fact]
    public void ParseCheckout_OriginRepoAndBranch()
    {
        const string config = "[core]\r\n\tbare = false\r\n[remote \"upstream\"]\r\n\turl = https://github.com/other/x.git\r\n" +
                              "[remote \"origin\"]\r\n\turl = git@github.com:acme/app.git\r\n\tfetch = +refs/heads/*:refs/remotes/origin/*\r\n";

        Assert.Equal(("acme/app", "feature/login"), GitHubParse.ParseCheckout(config, "ref: refs/heads/feature/login\r\n"));
    }

    [Fact]
    public void ParseCheckout_DetachedHeadOrOtherHost_IsNull()
    {
        const string config = "[remote \"origin\"]\r\n\turl = git@github.com:acme/app.git\r\n";

        Assert.Null(GitHubParse.ParseCheckout(config, "a1b2c3d4e5f6\n"));
        Assert.Null(GitHubParse.ParseCheckout("[remote \"origin\"]\n\turl = https://example.com/a/b\n", "ref: refs/heads/main\n"));
    }

    [Fact]
    public void Checkout_FindsTheRepoFromASubfolder()
    {
        var repo = MakeRepo();
        var deep = Path.Combine(repo, "src", "deep");
        Directory.CreateDirectory(deep);

        Assert.Equal(("acme/app", "main"), GitHubParse.Checkout(deep));
    }

    [Fact]
    public void Checkout_Worktree_IsNull()
    {
        var app = MakeRepo();
        var wt = Path.Combine(root, "wt");
        Directory.CreateDirectory(wt);
        File.WriteAllText(Path.Combine(wt, ".git"), "gitdir: " + Path.Combine(app, ".git", "worktrees", "wt") + "\n");

        Assert.Null(GitHubParse.Checkout(wt));
    }

    [Fact]
    public void Runs_WorkflowRunsJson_ToRuns()
    {
        var runs = GitHubParse.Runs(Doc(Fixture("gh-runs.json")));

        Assert.Equal(2, runs.Count);
        var ci = runs[0];
        Assert.Equal(9001L, ci.Id);
        Assert.Equal("acme/app", ci.Repo);
        Assert.Equal("CI", ci.Workflow);
        Assert.Equal("Fix login redirect", ci.Title);
        Assert.Equal("in_progress", ci.Status);
        Assert.Null(ci.Conclusion);
        Assert.Equal("https://github.com/acme/app/actions/runs/9001", ci.Url);
        Assert.Equal(Ms("2026-11-10T03:20:05Z"), ci.StartedMs);
        Assert.Null(ci.Step);
        Assert.Null(ci.Progress);
        Assert.Equal("success", runs[1].Conclusion);
    }

    [Fact]
    public void Runs_NullOrEntryWithoutRepository_Empty()
    {
        Assert.Empty(GitHubParse.Runs(null));
        Assert.Empty(GitHubParse.Runs(Doc("{\"workflow_runs\":[{\"id\":\"x\"}]}")));
    }

    [Fact]
    public void Steps_RunJobsJson_CurrentStepAndProgress()
    {
        var (step, progress) = GitHubParse.Steps(Doc(Fixture("gh-run-jobs.json")));

        Assert.Equal("build (windows) › Build installer", step);
        Assert.Equal(5.0 / 7.0, progress!.Value, precision: 12);  // 5 completed of 7 steps over both jobs
    }

    [Fact]
    public void Steps_Null_IsNothing()
    {
        var none = GitHubParse.Steps(null);
        Assert.Null(none.Step);
        Assert.Null(none.Progress);
    }

    [Fact]
    public void Pushed_RecordsTheCheckoutOnly()
    {
        clock = Ms("2026-11-10T03:20:00Z");
        var watcher = NewWatcher();
        var notRepo = Path.Combine(root, "plain");
        Directory.CreateDirectory(notRepo);

        Assert.False(watcher.Pushed(notRepo));
        Assert.False(watcher.Watching);

        var repo = MakeRepo("git@github.com:acme/r.git", "ref: refs/heads/dev\n");
        Assert.True(watcher.Pushed(repo));
        Assert.True(watcher.Watching);
    }

    [Fact]
    public async Task Poll_InProgressRun_ShowsAJobsRow()
    {
        clock = Ms("2026-11-10T03:20:00Z");
        var watcher = NewWatcher();
        watcher.Pushed(MakeRepo());
        api.Routes[RunsRoute] = Fixture("gh-runs.json");
        api.Routes[JobsRoute] = Fixture("gh-run-jobs.json");
        clock = Ms("2026-11-10T03:21:05Z");

        await watcher.Poll(_ => { });

        var row = Assert.Single(watcher.Jobs);
        Assert.Equal("gh:9001", row.Id);
        Assert.Equal("acme/app · CI", row.Title);
        Assert.Equal("build (windows) › Build installer · 1:00", row.Detail);
        Assert.Equal(5.0 / 7.0, row.Progress!.Value, precision: 12);
        Assert.Equal("https://github.com/acme/app/actions/runs/9001", row.Url);
        Assert.Equal("GitHub Actions", row.Tool);
        Assert.Equal("server.rack", row.Symbol);
        Assert.True(row.Working);
        Assert.Contains(JobsRoute, api.Requested);
        Assert.Equal(RunsRoute, api.Requested[0]);
    }

    [Fact]
    public async Task Poll_QueuedRun_SaysItWaitsForARunner()
    {
        clock = Ms("2026-11-10T03:20:00Z");
        var watcher = NewWatcher();
        watcher.Pushed(MakeRepo());
        api.Routes[RunsRoute] = RunJson(9001, "queued", "null");

        await watcher.Poll(_ => { });

        Assert.Equal(L10n.L("Waiting for a runner"), Assert.Single(watcher.Jobs).Detail);
        Assert.Contains(JobsRoute, api.Requested);  // the steps route is read for every run not completed
    }

    [Fact]
    public async Task Poll_FinishedFiresOnlyOnTheTransition()
    {
        clock = Ms("2026-11-10T03:20:00Z");
        var watcher = NewWatcher();
        watcher.Pushed(MakeRepo());
        var finished = new List<IReadOnlyList<GhRun>>();
        api.Routes[RunsRoute] = RunJson(9001, "in_progress", "null");
        api.Routes[JobsRoute] = Fixture("gh-run-jobs.json");
        await watcher.Poll(f => finished.Add(f));
        Assert.Empty(finished);

        api.Routes[RunsRoute] = RunJson(9001, "completed", "\"success\"");
        await watcher.Poll(f => finished.Add(f));
        var done = Assert.Single(finished);
        var run = Assert.Single(done);
        Assert.Equal(9001L, run.Id);
        Assert.Equal("success", run.Conclusion);

        await watcher.Poll(f => finished.Add(f));
        Assert.Single(finished);  // already completed: not reported again
        Assert.Empty(watcher.Jobs);
    }

    [Fact]
    public async Task Poll_ApiReturnsNull_KeepsRunsAndWatches()
    {
        clock = Ms("2026-11-10T03:20:00Z");
        var watcher = NewWatcher();
        watcher.Pushed(MakeRepo());
        api.Routes[RunsRoute] = RunJson(9001, "in_progress", "null");
        api.Routes[JobsRoute] = Fixture("gh-run-jobs.json");
        await watcher.Poll(_ => { });

        api.Routes.Remove(RunsRoute);  // the API now answers null (no network, bad token, rate limit)
        var finished = new List<IReadOnlyList<GhRun>>();
        var failure = await Record.ExceptionAsync(() => watcher.Poll(f => finished.Add(f)));

        Assert.Null(failure);
        Assert.Empty(finished);
        Assert.True(watcher.Watching);
        Assert.Equal("gh:9001", Assert.Single(watcher.Jobs).Id);
    }

    [Fact]
    public async Task Poll_AfterOneHour_StopsWatching()
    {
        clock = Ms("2026-11-10T03:20:00Z");
        var watcher = NewWatcher();
        watcher.Pushed(MakeRepo());

        clock += 3600000;
        await watcher.Poll(_ => { });
        Assert.True(watcher.Watching, "exactly one hour is still watched");

        clock += 1;
        await watcher.Poll(_ => { });
        Assert.False(watcher.Watching);
    }

    [Fact]
    public async Task Poll_AllRunsCompleted_StopsWatchingAfterTwoMinutes()
    {
        clock = Ms("2026-11-10T03:20:00Z");
        var watcher = NewWatcher();
        watcher.Pushed(MakeRepo());
        api.Routes[RunsRoute] = RunJson(9001, "completed", "\"success\"");

        clock = Ms("2026-11-10T03:21:40Z");  // 100 s after the push
        await watcher.Poll(_ => { });
        Assert.True(watcher.Watching);

        clock = Ms("2026-11-10T03:22:01Z");  // 121 s after the push
        await watcher.Poll(_ => { });
        Assert.False(watcher.Watching);
    }

    [Fact]
    public async Task Poll_TokenAskedAtMostEvery10Min()
    {
        clock = Ms("2026-11-10T03:20:00Z");
        var watcher = NewWatcher();
        watcher.Pushed(MakeRepo());
        api.Routes[RunsRoute] = NoRuns;

        await watcher.Poll(_ => { });
        Assert.Equal(1, api.TokenCalls);

        clock += 300000;
        await watcher.Poll(_ => { });
        Assert.Equal(1, api.TokenCalls);

        clock += 300000;  // exactly 10 min since the last ask
        await watcher.Poll(_ => { });
        Assert.Equal(1, api.TokenCalls);

        clock += 1;
        await watcher.Poll(_ => { });
        Assert.Equal(2, api.TokenCalls);
        Assert.Equal("ghp_test", api.LastToken);
    }

    [Fact]
    public async Task Poll_WhileAPollIsRunning_ReturnsWithoutRequests()
    {
        clock = Ms("2026-11-10T03:20:00Z");
        var watcher = NewWatcher();
        watcher.Pushed(MakeRepo());
        api.Routes[RunsRoute] = NoRuns;
        api.Gate = new TaskCompletionSource();

        var first = watcher.Poll(_ => { });
        var requestsBefore = api.Requested.Count;
        await watcher.Poll(_ => { });

        Assert.Equal(requestsBefore, api.Requested.Count);
        api.Gate.SetResult();
        await first;
    }

    /// Fake of the GitHub seam. Routes maps a route to its JSON text; a route with no entry answers null (a failure).
    private sealed class FakeGitHubApi : IGitHubApi
    {
        public Dictionary<string, string> Routes { get; } = new(StringComparer.Ordinal);
        public List<string> Requested { get; } = new();
        public TaskCompletionSource? Gate { get; set; }
        public int TokenCalls { get; private set; }
        public string? LastToken { get; private set; }

        public async Task<JsonDocument?> Get(string route, string? token)
        {
            Requested.Add(route);
            LastToken = token;
            if (Gate is not null)
            {
                await Gate.Task;
            }
            return Routes.TryGetValue(route, out var json) ? JsonDocument.Parse(json) : null;
        }

        public Task<string?> Token()
        {
            TokenCalls++;
            return Task.FromResult<string?>("ghp_test");
        }
    }
}
