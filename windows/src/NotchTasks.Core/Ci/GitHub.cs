using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NotchTasks.Core.Agents;

namespace NotchTasks.Core.Ci;

/// The GitHub REST seam. Get answers the parsed JSON of api.github.com/<route>, or null on any failure (no throw: polls
/// must not crash). Token answers gh's token (`gh auth token`), or null when gh is missing or not logged in. The app's
/// implementation does the HTTPS call and the gh process (Phase 3c R3). Tests use a fake.
public interface IGitHubApi
{
    Task<JsonDocument?> Get(string route, string? token);

    Task<string?> Token();
}

/// One workflow run of a pushed branch. StartedMs is epoch milliseconds, 0 when unknown.
public sealed record GhRun(long Id, string Repo, string Workflow, string Title, string Status, string? Conclusion, string? Url, long StartedMs, string? Step, double? Progress);

/// Pure parsing of the GitHub data. Port of electron/src/core/github.js (Mac: GitHub in Sources/GitHub.swift, lines 1-164).
public static class GitHubParse
{
    private const string HeadPrefix = "ref: refs/heads/";

    private static readonly Regex HostPattern = new(@"github\.com[:/]", RegexOptions.CultureInvariant);

    /// "git@github.com:o/r.git" / "https://github.com/o/r" -> "o/r", else null.
    public static string? RepoFromRemote(string url)
    {
        var m = HostPattern.Match(url);
        if (!m.Success)
        {
            return null;
        }
        var path = url[(m.Index + m.Length)..];
        if (path.EndsWith(".git", StringComparison.Ordinal))
        {
            path = path[..^4];
        }
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? $"{parts[0]}/{parts[1]}" : null;
    }

    /// .git/config + .git/HEAD text -> repo of origin and the checked-out branch, else null (detached HEAD, no GitHub origin).
    public static (string Repo, string Branch)? ParseCheckout(string config, string head)
    {
        var inOrigin = false;
        string? remote = null;
        foreach (var raw in config.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                inOrigin = line == "[remote \"origin\"]";
            }
            else if (inOrigin && line.StartsWith("url", StringComparison.Ordinal))
            {
                var eq = line.IndexOf('=');
                if (eq >= 0)
                {
                    remote = line[(eq + 1)..].Trim();
                }
            }
        }
        var repo = remote is null ? null : RepoFromRemote(remote);
        var h = head.Trim();
        if (repo is null || !h.StartsWith(HeadPrefix, StringComparison.Ordinal))
        {
            return null;
        }
        return (repo, h[HeadPrefix.Length..]);
    }

    /// Repo (from origin) and current branch of the checkout containing dir. Walks up. A .git file (worktree) gives null.
    public static (string Repo, string Branch)? Checkout(string dir)
    {
        string cur;
        try
        {
            cur = Path.GetFullPath(dir);
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }

        while (true)
        {
            var git = Path.Combine(cur, ".git");
            if (Directory.Exists(git))
            {
                try
                {
                    return ParseCheckout(
                        File.ReadAllText(Path.Combine(git, "config")),
                        File.ReadAllText(Path.Combine(git, "HEAD")));
                }
                catch (IOException)
                {
                    return null;
                }
                catch (UnauthorizedAccessException)
                {
                    return null;
                }
            }
            if (File.Exists(git))
            {
                return null;  // a worktree or submodule: skipped
            }
            var up = Path.GetDirectoryName(cur);
            if (string.IsNullOrEmpty(up) || up == cur)
            {
                return null;
            }
            cur = up;
        }
    }

    /// workflow_runs JSON -> runs. An entry without a numeric id or a repository is dropped.
    public static IReadOnlyList<GhRun> Runs(JsonDocument? json)
    {
        var runs = new List<GhRun>();
        if (json is null || !IsObject(json.RootElement) ||
            !json.RootElement.TryGetProperty("workflow_runs", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return runs;
        }
        foreach (var r in list.EnumerateArray())
        {
            if (!IsObject(r) || !r.TryGetProperty("id", out var idEl) ||
                idEl.ValueKind != JsonValueKind.Number || !idEl.TryGetInt64(out var id))
            {
                continue;
            }
            var repo = r.TryGetProperty("repository", out var repoObj) && IsObject(repoObj) ? Text(repoObj, "full_name") : null;
            if (repo is null)
            {
                continue;
            }
            var stamp = Text(r, "run_started_at");
            if (string.IsNullOrEmpty(stamp))
            {
                stamp = Text(r, "created_at");
            }
            var name = Text(r, "name");
            var url = Text(r, "html_url");
            runs.Add(new GhRun(
                Id: id,
                Repo: repo,
                Workflow: string.IsNullOrEmpty(name) ? "workflow" : name,
                Title: Text(r, "display_title") ?? string.Empty,
                Status: Text(r, "status") ?? string.Empty,
                Conclusion: Text(r, "conclusion"),
                Url: string.IsNullOrEmpty(url) ? null : url,
                StartedMs: DateTimeOffset.TryParse(stamp ?? string.Empty, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
                    ? at.ToUnixTimeMilliseconds()
                    : 0,
                Step: null,
                Progress: null));
        }
        return runs;
    }

    /// jobs JSON -> current step ("job › step") and step progress (finished steps / all steps over every job).
    public static (string? Step, double? Progress) Steps(JsonDocument? json)
    {
        if (json is null || !IsObject(json.RootElement) ||
            !json.RootElement.TryGetProperty("jobs", out var jobs) || jobs.ValueKind != JsonValueKind.Array)
        {
            return (null, null);
        }

        string? jobName = null;
        foreach (var j in jobs.EnumerateArray())
        {
            if (Text(j, "status") == "in_progress")
            {
                jobName = Text(j, "name");
                break;
            }
        }

        var steps = new List<JsonElement>();
        foreach (var j in jobs.EnumerateArray())
        {
            if (IsObject(j) && j.TryGetProperty("steps", out var s) && s.ValueKind == JsonValueKind.Array)
            {
                steps.AddRange(s.EnumerateArray());
            }
        }
        if (steps.Count == 0)
        {
            return (jobName, null);
        }

        var done = steps.Count(s => Text(s, "status") == "completed");
        string? runningStep = null;
        foreach (var s in steps)
        {
            if (Text(s, "status") == "in_progress")
            {
                runningStep = Text(s, "name");
                break;
            }
        }
        var label = string.Join(" › ", new[] { jobName, runningStep }.Where(p => p is not null));
        return (label.Length == 0 ? null : label, (double)done / steps.Count);
    }

    private static bool IsObject(JsonElement e) => e.ValueKind == JsonValueKind.Object;

    /// The string property `name` of an object, or null (also for a non-object element, a missing or non-string value).
    private static string? Text(JsonElement obj, string name) =>
        IsObject(obj) && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// Watches each pushed branch for up to 1 h: runs, live step, pass/fail. Port of electron/src/core/github.js
/// GitHubWatcher (Mac: GitHubWatcher in Sources/GitHub.swift). The token comes from the api seam, asked at most every 10 min.
public sealed class GitHubWatcher
{
    private const long EarlyStartMs = 90000;      // runs that started this long before the push are not ours
    private const long SettledAfterMs = 120000;   // a repo whose runs all finished is unwatched after this
    private const long WatchLimitMs = 3600000;    // and always after an hour
    private const long TokenMaxAgeMs = 600000;

    private static readonly string[] WaitingStatuses = { "queued", "waiting", "pending", "requested" };

    private readonly IGitHubApi api;
    private readonly Func<long> nowMs;
    private readonly Dictionary<string, (string Branch, long Since)> watched = new(StringComparer.Ordinal);
    private Dictionary<long, GhRun> runs = new();
    private bool busy;
    private string? token;
    private long? tokenAt;

    public GitHubWatcher(IGitHubApi api, Func<long> nowMs)
    {
        this.api = api;
        this.nowMs = nowMs;
    }

    /// True while at least one repo is watched.
    public bool Watching => watched.Count > 0;

    /// Records the checkout containing dir as pushed now. False when dir is not a GitHub checkout on a branch.
    public bool Pushed(string dir)
    {
        var c = GitHubParse.Checkout(dir);
        if (c is null)
        {
            return false;
        }
        watched[c.Value.Repo] = (c.Value.Branch, nowMs());
        return true;
    }

    /// One poll at a time. finished gets the runs that completed since the last poll.
    public async Task Poll(Action<IReadOnlyList<GhRun>> finished)
    {
        if (busy || watched.Count == 0)
        {
            return;
        }
        busy = true;
        try
        {
            var targets = watched.ToList();
            var previous = runs;
            var tok = await TokenAsync();
            var next = new Dictionary<long, GhRun>();

            foreach (var (repo, w) in targets)
            {
                var route = $"repos/{repo}/actions/runs?branch={EncodeUriComponent(w.Branch)}&per_page=10";
                using var listing = await GetOrNull(route, tok);
                if (listing is null)
                {
                    // A failed request keeps this repo's runs as they were: a network error is not a finished run.
                    foreach (var r in previous.Values.Where(r => string.Equals(r.Repo, repo, StringComparison.OrdinalIgnoreCase)))
                    {
                        next[r.Id] = r;
                    }
                    continue;
                }

                foreach (var r in GitHubParse.Runs(listing))
                {
                    if (r.StartedMs < w.Since - EarlyStartMs)
                    {
                        continue;
                    }
                    var run = r;
                    if (r.Status != "completed")
                    {
                        using var jobs = await GetOrNull($"repos/{repo}/actions/runs/{r.Id}/jobs", tok);
                        var (step, progress) = GitHubParse.Steps(jobs);
                        run = r with { Step = step, Progress = progress };
                    }
                    next[run.Id] = run;
                }
            }

            var justFinished = next.Values
                .Where(r => r.Status == "completed" && previous.TryGetValue(r.Id, out var before) && before.Status != "completed")
                .ToList();
            runs = next;

            var now = nowMs();
            // stop watching a repo once its runs are all done (after 2 min), or after an hour
            foreach (var (repo, w) in targets)
            {
                var mine = next.Values.Where(r => string.Equals(r.Repo, repo, StringComparison.OrdinalIgnoreCase)).ToList();
                var settled = mine.Count > 0 && mine.All(r => r.Status == "completed") && now - w.Since > SettledAfterMs;
                if ((settled || now - w.Since > WatchLimitMs) && watched.TryGetValue(repo, out var current) && current.Equals(w))
                {
                    watched.Remove(repo);
                }
            }

            if (justFinished.Count > 0)
            {
                finished(justFinished);
            }
        }
        finally
        {
            busy = false;
        }
    }

    /// The rows of the runs not completed yet, oldest first. Waiting runs say so; running ones show step and time.
    public IReadOnlyList<Job> Jobs
    {
        get
        {
            var now = nowMs();
            return runs.Values
                .Where(r => r.Status != "completed")
                .OrderBy(r => r.Id)
                .Select(r =>
                {
                    var waiting = WaitingStatuses.Contains(r.Status);
                    var detail = waiting
                        ? L10n.L("Waiting for a runner")
                        : $"{(string.IsNullOrEmpty(r.Step) ? L10n.L("running") : r.Step)} · {DevRules.Mmss((now - r.StartedMs) / 1000.0)}";
                    return new Job(
                        Id: $"gh:{r.Id}",
                        Title: $"{r.Repo} · {r.Workflow}",
                        Detail: detail,
                        Working: true,
                        Url: r.Url,
                        Progress: r.Progress,
                        Symbol: "server.rack",
                        Tool: "GitHub Actions");
                })
                .ToList();
        }
    }

    /// gh's token, asked at most every 10 min (the first ask is always made).
    private async Task<string?> TokenAsync()
    {
        var now = nowMs();
        if (tokenAt is null || now - tokenAt.Value > TokenMaxAgeMs)
        {
            token = await TryToken();
            tokenAt = nowMs();
        }
        return token;
    }

    private async Task<string?> TryToken()
    {
        try
        {
            return await api.Token();
        }
        catch (Exception)
        {
            return null;  // the seam must not crash a poll: no token means anonymous requests
        }
    }

    private async Task<JsonDocument?> GetOrNull(string route, string? tok)
    {
        try
        {
            return await api.Get(route, tok);
        }
        catch (Exception)
        {
            return null;  // the seam must not crash a poll: a failed request is a null answer
        }
    }

    /// encodeURIComponent: every byte except letters, digits and -_.!~*'() is percent-encoded (upper case hex).
    private static string EncodeUriComponent(string s)
    {
        const string unreserved = "-_.!~*'()";
        var sb = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(s))
        {
            var c = (char)b;
            if (b < 128 && (char.IsAsciiLetterOrDigit(c) || unreserved.Contains(c)))
            {
                sb.Append(c);
            }
            else
            {
                sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }
        return sb.ToString();
    }
}
