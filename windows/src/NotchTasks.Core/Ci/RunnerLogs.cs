using System.Text;
using System.Text.RegularExpressions;

namespace NotchTasks.Core.Ci;

/// One running GitLab job (Mac: RunnerJob in Sources/Runner.swift).
public sealed record RunnerJob(string Id, string Name, string Project, string? Url);

/// GitLab Runner and GitHub self-hosted runner logs. Port of electron/src/core/runnerlog.js (Mac: RunnerLog in
/// Sources/Runner.swift, GHSelfHosted in Sources/GitHub.swift, buildsProject in Sources/Processes.swift).
/// Pure parsing plus small file helpers. The job row itself (Electron workerJob) is built by the caller.
public static class RunnerLogs
{
    /// Default install folder of the GitHub Actions runner on Windows.
    public const string DefaultRunnerDir = @"C:\actions-runner";

    private const string DefaultServer = "https://gitlab.com";
    private const string FallbackGitLabDir = @"C:\GitLab-Runner";
    private const string JobResultMarker = "Job result after all job steps finish: ";
    private const string JobNameMarker = "\"jobDisplayName\": \"";

    private static readonly Regex Field = new(@"([A-Za-z0-9_-]+)=(""([^""]*)""|\S+)", RegexOptions.CultureInvariant);
    private static readonly Regex Ansi = new(@"\x1b\[[0-9;]*m", RegexOptions.CultureInvariant);
    private static readonly Regex WorkerRepo = new(@"[\\/]_work[\\/]([^\\/\s""]+)", RegexOptions.CultureInvariant);

    /// The last `bytes` of a file, decoded as UTF-8. "" on any error. Opened with the runner's sharing, since the runner
    /// keeps its log open while it writes.
    public static string ReadTail(string file, int bytes)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var len = (int)Math.Min(stream.Length, bytes);
            if (len <= 0)
            {
                return "";
            }
            var buffer = new byte[len];
            stream.Seek(-len, SeekOrigin.End);
            stream.ReadExactly(buffer);
            return Encoding.UTF8.GetString(buffer);
        }
        catch (Exception)
        {
            return "";
        }
    }

    /// GitLab Runner log lines: "received" = job started, "Job succeeded" / "Job failed" = end. A job still received is
    /// running. server: the GitLab base URL (config.toml on the Mac; the caller passes it).
    public static (IReadOnlyList<RunnerJob> Running, IReadOnlyList<JobAlert> Failed, IReadOnlySet<string> Succeeded) ParseGitLab(
        string text, string server = DefaultServer)
    {
        var running = new List<RunnerJob>();
        var failed = new List<JobAlert>();
        var succeeded = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in (text ?? "").Split('\n'))
        {
            var received = raw.Contains("received", StringComparison.Ordinal);
            var ok = raw.Contains("Job succeeded", StringComparison.Ordinal);
            var fail = raw.Contains("Job failed", StringComparison.Ordinal);
            if (!received && !ok && !fail)
            {
                continue;
            }
            var line = Ansi.Replace(raw, "");
            var f = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (Match m in Field.Matches(line))
            {
                f[m.Groups[1].Value] = m.Groups[3].Success ? m.Groups[3].Value : m.Groups[2].Value;
            }
            if (!f.TryGetValue("job", out var id) || id.Length == 0)
            {
                continue;
            }
            if (received)
            {
                var project = FirstNonEmpty(ProjectFromRepoUrl(Get(f, "repo_url")), Get(f, "project_full_path"));
                running.Add(new RunnerJob(id, Get(f, "job_name") ?? $"job {id}", project ?? "?", JobUrl(JobServer(f, server), project, id)));
                continue;
            }
            running.RemoveAll(r => r.Id == id);
            if (ok)
            {
                succeeded.Add(id);
            }
            if (fail)
            {
                var project = Get(f, "project_full_path");
                var exit = Get(f, "exit_code");
                var parts = new[] { Get(f, "failure_reason"), string.IsNullOrEmpty(exit) ? null : $"exit {exit}" };
                var reason = string.Join(" · ", parts.Where(s => !string.IsNullOrEmpty(s)));
                failed.Add(new JobAlert(id, Get(f, "job_name") ?? $"job {id}", project ?? "?", reason, JobUrl(JobServer(f, server), project, id)));
            }
        }
        return (running, failed, succeeded);
    }

    /// Worker log: the result of the last job ("Succeeded", "Failed", ...). Null when there is none yet.
    public static string? WorkerResult(string? log)
    {
        var text = log ?? "";
        var i = text.LastIndexOf(JobResultMarker, StringComparison.Ordinal);
        if (i < 0)
        {
            return null;
        }
        var start = i + JobResultMarker.Length;
        var end = start;
        while (end < text.Length && char.IsAsciiLetter(text[end]))
        {
            end++;
        }
        return end == start ? null : text[start..end];
    }

    /// Worker log: the last "jobDisplayName" (the running or finished job). Null when there is none.
    public static string? WorkerJobName(string? log)
    {
        var text = log ?? "";
        var i = text.LastIndexOf(JobNameMarker, StringComparison.Ordinal);
        if (i < 0)
        {
            return null;
        }
        var rest = text[(i + JobNameMarker.Length)..];
        var quote = rest.IndexOf('"');
        return quote < 0 ? rest : rest[..quote];
    }

    /// Repo folder name from the job's workspace path (...\_work\<repo>\<repo>). Windows has no cwd for the worker
    /// process here, so this reads the log instead.
    /// shortcut: the first _work path in the log is taken as the workspace; upgrade if a runner version writes it differently.
    public static string? RepoFromWorkerLog(string? log)
    {
        var m = WorkerRepo.Match(log ?? "");
        return m.Success ? m.Groups[1].Value : null;
    }

    /// ...\bin\Runner.Worker.exe -> ...\_diag (the runner's log folder). Keeps the separator the path uses.
    public static string DiagFromWorkerExe(string exe)
    {
        var sep = exe.Contains('\\') ? "\\" : "/";
        var parts = exe.Split(['\\', '/']);
        var kept = parts.Take(Math.Max(0, parts.Length - 2));
        return string.Join(sep, kept.Append("_diag"));
    }

    /// Newest Worker_* file in a _diag folder, or null.
    public static string? NewestWorkerLog(string diag)
    {
        try
        {
            string? best = null;
            var bestTime = DateTime.MinValue;
            foreach (var file in Directory.GetFiles(diag))
            {
                if (!Path.GetFileName(file).StartsWith("Worker_", StringComparison.Ordinal))
                {
                    continue;
                }
                var time = File.GetLastWriteTimeUtc(file);
                if (best is null || time > bestTime)
                {
                    best = file;
                    bestTime = time;
                }
            }
            return best;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// GitLab shell executor layout: "...\builds\<token>\<n>\<group>\<project>[\sub]" -> "group/project" (either separator).
    /// shortcut: nested groups lose their parent group; CI_PROJECT_PATH (when readable) is exact.
    public static string? BuildsProject(string path)
    {
        var parts = (path ?? "").Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        var i = Array.LastIndexOf(parts, "builds");
        if (i < 0 || parts.Length <= i + 4)
        {
            return null;
        }
        return $"{parts[i + 3]}/{parts[i + 4]}".Replace(".tmp", "", StringComparison.Ordinal);
    }

    /// The .log files of `<programData>\gitlab-runner` and the fallback folder, newest first, at most 3, each with its mtime in
    /// ms (taken from the same listing, no second stat). A file whose mtime cannot be read is skipped.
    public static IReadOnlyList<(string File, long MtimeMs)> GitLabLogs(string programData, string fallbackDir = FallbackGitLabDir)
    {
        var dirs = new List<string>();
        if (!string.IsNullOrEmpty(programData))
        {
            dirs.Add(Path.Combine(programData, "gitlab-runner"));
        }
        dirs.Add(fallbackDir);
        var files = new List<(string File, DateTime Written)>();
        foreach (var dir in dirs)
        {
            try
            {
                if (!Directory.Exists(dir))
                {
                    continue;
                }
                foreach (var f in new DirectoryInfo(dir).GetFiles("*.log"))
                {
                    if (!f.Extension.Equals(".log", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    try
                    {
                        files.Add((f.FullName, f.LastWriteTimeUtc));
                    }
                    catch (IOException)
                    {
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
        return files
            .OrderByDescending(f => f.Written)
            .Take(3)
            .Select(f => (f.File, new DateTimeOffset(f.Written).ToUnixTimeMilliseconds()))
            .ToList();
    }

    /// "https://gitlab.com/group/project.git" -> "group/project". Null when the URL does not parse.
    private static string? ProjectFromRepoUrl(string? url)
    {
        if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }
        var path = uri.AbsolutePath;
        if (path.StartsWith('/'))
        {
            path = path[1..];
        }
        return path.Replace(".git", "", StringComparison.Ordinal);
    }

    private static string? JobUrl(string server, string? project, string id) =>
        string.IsNullOrEmpty(project) ? null : $"{server}/{project}/-/jobs/{id}";

    /// The GitLab server of a job: scheme and host (and port) of its repo_url, so a self-managed server gets its own job
    /// links; the caller's server when the line has no usable repo_url.
    private static string JobServer(Dictionary<string, string> fields, string server)
    {
        var url = Get(fields, "repo_url");
        if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return server;
        }
        return uri.GetLeftPart(UriPartial.Authority);
    }

    private static string? Get(Dictionary<string, string> fields, string key) =>
        fields.TryGetValue(key, out var value) ? value : null;

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrEmpty(v));
}
