using NotchTasks.Core.Ci;
using Xunit;

namespace NotchTasks.Core.Tests.Ci;

// Ported from electron/test/runner.test.js (the GitLab parse, worker log, runner folder and newest-log cases), plus the
// Windows cases from the Phase 3c brief. Fixtures are copied from electron/test/fixtures/builds. Runner folders are never
// read: the newest-log and GitLab-log cases use temp folders.
public sealed class RunnerLogsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "nt-runner-" + Guid.NewGuid().ToString("N"));

    public RunnerLogsTests()
    {
        Directory.CreateDirectory(root);
    }

    public void Dispose()
    {
        Directory.Delete(root, recursive: true);
    }

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Fact]
    public void ParseGitLab_RunningSucceededAndFailed_WithTheirPages()
    {
        var (running, failed, succeeded) = RunnerLogs.ParseGitLab(Fixture("gitlab-runner.log"), "https://gitlab.com");

        // job 101 received then succeeded: not running; job 102 received then failed: not running
        Assert.Empty(running);
        Assert.Equal(new[] { "101" }, succeeded.ToArray());
        Assert.Single(failed);
        var f = failed[0];
        Assert.Equal("102", f.Id);
        Assert.Equal("job 102", f.Name);                                   // job_name is not in the failed line: falls back
        Assert.Equal("script_failure · exit 1", f.Reason);
        Assert.Equal("shop/web", f.Project);
        Assert.Equal("https://gitlab.com/shop/web/-/jobs/102", f.Url);
    }

    [Fact]
    public void ParseGitLab_JobStillReceived_IsRunning_AndRepoUrlGivesTheProjectPath()
    {
        const string text = "Feb 10 10:00:01 received job=7 job_name=\"unit tests\" repo_url=\"https://gitlab.com/shop/api.git\"\n";
        var (running, failed, _) = RunnerLogs.ParseGitLab(text, "https://gl.example.com");

        var job = Assert.Single(running);
        Assert.Equal(new RunnerJob("7", "unit tests", "shop/api", "https://gitlab.com/shop/api/-/jobs/7"), job);
        Assert.Empty(failed);
    }

    [Fact]
    public void ParseGitLab_SelfManagedRepoUrl_JobLinkUsesItsServer()
    {
        const string text = "Feb 10 10:00:01 received job=7 job_name=\"deploy\" repo_url=\"https://git.company.local/group/proj.git\"\n";
        var (running, _, _) = RunnerLogs.ParseGitLab(text, "https://gitlab.com");

        Assert.Equal("https://git.company.local/group/proj/-/jobs/7", Assert.Single(running).Url);
    }

    [Fact]
    public void ParseGitLab_SelfManagedRepoUrlWithPort_FailedJobLinkKeepsThePort()
    {
        const string text = "Feb 10 10:00:01 Job failed job=8 job_name=\"lint\" project_full_path=\"group/proj\" " +
                            "repo_url=\"https://git.company.local:8443/group/proj.git\"\n";
        var (_, failed, _) = RunnerLogs.ParseGitLab(text, "https://gitlab.com");

        Assert.Equal("https://git.company.local:8443/group/proj/-/jobs/8", Assert.Single(failed).Url);
    }

    [Fact]
    public void ParseGitLab_AnsiColourCodes_DoNotBreakFields()
    {
        const string text = "\u001b[0;m\u001b[0;mJob failed job=9 job_name=\"\u001b[31mtest\u001b[0m\" project_full_path=\"a/b\" exit_code=2\n";
        var (_, failed, _) = RunnerLogs.ParseGitLab(text);

        Assert.Equal("exit 2", failed[0].Reason);
        Assert.Equal("a/b", failed[0].Project);
    }

    [Fact]
    public void ParseGitLab_HalfLine_Ignored()
    {
        // The last line cut before "Job failed": it must parse like the fixture without that line.
        var full = Fixture("gitlab-runner.log").TrimEnd('\n');
        var lastNewline = full.LastIndexOf('\n');
        var withoutLast = full[..(lastNewline + 1)];
        var cut = full.IndexOf("Job failed", lastNewline, StringComparison.Ordinal);
        var half = full[..(cut + "Job fai".Length)];
        Assert.DoesNotContain("Job failed", half, StringComparison.Ordinal);

        var expected = RunnerLogs.ParseGitLab(withoutLast, "https://gitlab.com");
        var actual = RunnerLogs.ParseGitLab(half, "https://gitlab.com");

        Assert.Equal(expected.Running, actual.Running);
        Assert.Equal(expected.Failed, actual.Failed);
        Assert.True(expected.Succeeded.SetEquals(actual.Succeeded));
    }

    [Fact]
    public void WorkerLog_JobNameAndResultOfTheLastJob()
    {
        var log = Fixture("runner-worker.log");

        Assert.Equal("build (windows)", RunnerLogs.WorkerJobName(log));
        Assert.Equal("Succeeded", RunnerLogs.WorkerResult(log));
        Assert.Null(RunnerLogs.WorkerResult("[t] Starting Worker\n"));     // still running: no result line
        Assert.Equal("app", RunnerLogs.RepoFromWorkerLog(log));
    }

    [Fact]
    public void RunnerFolder_DiagIsNextToBin_WindowsSeparatorsKept()
    {
        Assert.Equal(@"C:\actions-runner\_diag", RunnerLogs.DiagFromWorkerExe(@"C:\actions-runner\bin\Runner.Worker.exe"));
        Assert.Equal("/home/u/actions-runner/_diag", RunnerLogs.DiagFromWorkerExe("/home/u/actions-runner/bin/Runner.Worker"));
        Assert.Equal(@"C:\actions-runner", RunnerLogs.DefaultRunnerDir);
    }

    [Fact]
    public void NewestWorkerLog_PicksNewestWorkerFile()
    {
        var diag = Path.Combine(root, "_diag");
        Directory.CreateDirectory(diag);
        var runner = Path.Combine(diag, "Runner_1.log");
        var worker1 = Path.Combine(diag, "Worker_1.log");
        var worker2 = Path.Combine(diag, "Worker_2.log");
        File.WriteAllText(runner, "runner");
        File.WriteAllText(worker1, "old job");
        File.WriteAllText(worker2, "new job");
        var now = DateTime.UtcNow;
        File.SetLastWriteTimeUtc(runner, now.AddHours(1));       // newest file overall, but not a Worker_ log
        File.SetLastWriteTimeUtc(worker1, now.AddMinutes(-10));
        File.SetLastWriteTimeUtc(worker2, now.AddMinutes(-1));

        Assert.Equal(worker2, RunnerLogs.NewestWorkerLog(diag));
        Assert.Null(RunnerLogs.NewestWorkerLog(Path.Combine(root, "missing")));
    }

    [Fact]
    public void BuildsProject_ShellExecutorPath()
    {
        Assert.Equal("group/project", RunnerLogs.BuildsProject(@"C:\GitLab-Runner\builds\abc123\0\group\project\sub"));
        Assert.Equal("group/project", RunnerLogs.BuildsProject("/srv/gitlab-runner/builds/abc123/0/group/project"));
        Assert.Null(RunnerLogs.BuildsProject(@"C:\GitLab-Runner\builds\abc123\0\group"));
        Assert.Null(RunnerLogs.BuildsProject(@"C:\Users\Hung\Desktop"));
    }

    [Fact]
    public void ReadTail_ReturnsTheLastBytes_AndEmptyOnError()
    {
        var file = Path.Combine(root, "tail.log");
        File.WriteAllText(file, "abcdefghij");

        Assert.Equal("ghij", RunnerLogs.ReadTail(file, 4));
        Assert.Equal("abcdefghij", RunnerLogs.ReadTail(file, 100));
        Assert.Equal("", RunnerLogs.ReadTail(Path.Combine(root, "missing.log"), 4));
    }

    [Fact]
    public void GitLabLogs_NewestFirst_AtMostThree_LogFilesOnly()
    {
        var programData = Path.Combine(root, "ProgramData");
        var fallback = Path.Combine(root, "GitLab-Runner");
        var dir = Path.Combine(programData, "gitlab-runner");
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(fallback);
        var now = DateTime.UtcNow;
        var names = new[] { "a.log", "b.log", "c.log", "d.log" };
        for (var i = 0; i < names.Length; i++)
        {
            var file = Path.Combine(i % 2 == 0 ? dir : fallback, names[i]);
            File.WriteAllText(file, "x");
            File.SetLastWriteTimeUtc(file, now.AddMinutes(-i));
        }
        File.WriteAllText(Path.Combine(dir, "e.txt"), "not a log");

        var logs = RunnerLogs.GitLabLogs(programData, fallback);

        Assert.Equal(new[] { "a.log", "b.log", "c.log" }, logs.Select(Path.GetFileName).ToArray());
    }
}
