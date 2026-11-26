using System.Text.Json;
using NotchTasks.Core.Agents;

namespace NotchTasks.Core.Limits;

/// Antigravity's quota, asked from its own language server: the daemon folder names its port and token. Models that share a
/// quota reset together, so each quota is one window named by its model families (Electron: providers.js
/// parseAntigravityLimits and fetchAntigravityLimits; Mac: Providers.swift AntigravityLimits).
public sealed class AntigravityLimits
{
    private const string GetUserStatusPath = "/exa.language_server_pb.LanguageServerService/GetUserStatus";
    private const string RequestBody = "{\"metadata\":{\"ideName\":\"antigravity\",\"extensionName\":\"antigravity\",\"locale\":\"en\"}}";
    private const double SameResetMs = 120_000;
    private const double SameUsedPercent = 0.5;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    private readonly Func<int, bool> _pidAlive;

    private sealed record Quota(DateTimeOffset Reset, double Used, List<string> Families);

    private sealed record Hub(int Pid, int Port, string Token);

    /// pidAlive says whether a process id is running (the app checks the process table).
    public AntigravityLimits(Func<int, bool> pidAlive)
    {
        _pidAlive = pidAlive;
    }

    /// The daemon folder under a home folder: <home>\.gemini\antigravity\daemon.
    public static string DaemonDir(string home) => Path.Combine(home, ".gemini", "antigravity", "daemon");

    /// Parses a GetUserStatus reply. Null when there are no quotas, or the text is not JSON. The fullest quota comes first, and
    /// a quota whose reset has passed counts as unused.
    public static ProviderLimits? Parse(string json, DateTimeOffset now)
    {
        using var doc = JsonFields.TryParse(json);
        if (doc is null)
        {
            return null;
        }
        var configs = JsonFields.Field(JsonFields.Field(JsonFields.Field(doc.RootElement, "userStatus"), "cascadeModelConfigData"), "clientModelConfigs");
        var quotas = new List<Quota>();
        if (configs.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in configs.EnumerateArray())
            {
                var q = JsonFields.Field(c, "quotaInfo");
                if (c.ValueKind != JsonValueKind.Object || q.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                var reset = Providers.ParseIso(JsonFields.Str(JsonFields.Field(q, "resetTime")));
                var family = JsonFields.Str(JsonFields.Field(c, "label"))?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (reset is null || string.IsNullOrEmpty(family))
                {
                    continue;
                }
                // a fraction left out of the JSON is a zero: nothing remains
                var used = (1 - (JsonFields.Num(JsonFields.Field(q, "remainingFraction")) ?? 0)) * 100;
                var i = quotas.FindIndex(x =>
                    Math.Abs((x.Reset - reset.Value).TotalMilliseconds) < SameResetMs && Math.Abs(x.Used - used) < SameUsedPercent);
                if (i >= 0)
                {
                    if (!quotas[i].Families.Contains(family))
                    {
                        quotas[i].Families.Add(family);
                    }
                }
                else
                {
                    quotas.Add(new Quota(reset.Value, used, new List<string> { family }));
                }
            }
        }
        // the fullest first: it is the one shown where there is room for one
        var windows = quotas
            .OrderByDescending(x => x.Used)
            .Select(x => new LimitWindow(string.Join("/", x.Families), x.Reset > now ? x.Used : 0, x.Reset))
            .ToList();
        return windows.Count == 0
            ? null
            : new ProviderLimits("process", "Antigravity", "com.google.antigravity", windows, now);
    }

    /// Asks the language server of a running Antigravity: the hubs in the daemon folder, newest first, whose process is alive.
    /// Null when none answers with quotas (Antigravity is then not running, and its limits leave the pill).
    public async Task<ProviderLimits?> FetchAsync(string home, IHttpPost http, DateTimeOffset now, CancellationToken ct)
    {
        foreach (var file in DaemonFiles(DaemonDir(home)))
        {
            ct.ThrowIfCancellationRequested();
            var hub = ReadHub(file);
            if (hub is null || !_pidAlive(hub.Pid))
            {
                continue;
            }
            var url = $"http://127.0.0.1:{hub.Port}{GetUserStatusPath}";
            var headers = new Dictionary<string, string>
            {
                ["Content-Type"] = "application/json",
                ["Connect-Protocol-Version"] = "1",
                ["X-Codeium-Csrf-Token"] = hub.Token,
            };
            try
            {
                var reply = await http.PostAsync(url, headers, RequestBody, RequestTimeout);
                var found = reply is null ? null : Parse(reply, now);
                if (found is not null)
                {
                    return found;
                }
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // this daemon did not answer: try the next
            }
        }
        return null;
    }

    /// The .json files in the daemon folder, newest first. A folder that cannot be listed (or is missing) has none.
    private static List<string> DaemonFiles(string daemon)
    {
        try
        {
            return Directory.EnumerateFiles(daemon)
                .Where(f => f.EndsWith(".json", StringComparison.Ordinal))
                .Select(f => (Full: f, Mtime: WriteTime(f)))
                .OrderByDescending(x => x.Mtime)
                .Select(x => x.Full)
                .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new List<string>();
        }
    }

    private static DateTimeOffset WriteTime(string file)
    {
        try
        {
            return new DateTimeOffset(new FileInfo(file).LastWriteTimeUtc);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return DateTimeOffset.MinValue;
        }
    }

    /// The port, token and pid of a hub file. Null when it is unreadable or any of the three is missing or of the wrong type.
    private static Hub? ReadHub(string file)
    {
        string text;
        try
        {
            text = Providers.ReadAllTextShared(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        using var doc = JsonFields.TryParse(text);
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        var pid = JsonFields.Int(JsonFields.Field(doc.RootElement, "pid"));
        var port = JsonFields.Int(JsonFields.Field(doc.RootElement, "httpPort"));
        var token = JsonFields.Str(JsonFields.Field(doc.RootElement, "csrfToken"));
        if (pid is null || port is null || token is null)
        {
            return null;
        }
        return new Hub(pid.Value, port.Value, token);
    }
}
