using System.Globalization;
using System.Text;
using NotchTasks.Core.Agents;
using NotchTasks.Core.Limits;
using Xunit;

namespace NotchTasks.Core.Tests;

// Ported from electron/test/providers.test.js: the Antigravity quota tests, fetchAntigravityLimits, the mid-turn and project
// tests, AntigravityScanner, and readAntigravityConversation. Mac reference: mac/Sources/AntigravityScanner.swift and
// Providers.swift. Every folder is a temp folder; the language server, the process list and sqlite3 are fakes.
[Collection("L10n")]
public sealed class AntigravityTests : IDisposable
{
    private const string Antigravity =
        "{\"userStatus\":{\"cascadeModelConfigData\":{\"clientModelConfigs\":["
        + "{\"label\":\"Gemini 3.8 Flash (High)\",\"quotaInfo\":{\"remainingFraction\":0.25,\"resetTime\":\"2026-11-11T07:21:06Z\"}},"
        + "{\"label\":\"Gemini 3.8 Flash (Low)\",\"quotaInfo\":{\"remainingFraction\":0.25,\"resetTime\":\"2026-11-11T07:21:06Z\"}},"
        + "{\"label\":\"Claude Sonnet 4.6\",\"quotaInfo\":{\"remainingFraction\":1,\"resetTime\":\"2026-11-14T12:19:38Z\"}},"
        + "{\"label\":\"GPT-OSS 120B\",\"quotaInfo\":{\"remainingFraction\":1,\"resetTime\":\"2026-11-14T12:19:38Z\"}},"
        + "{\"label\":\"Gemini 4 Argon\",\"quotaInfo\":{}}]}}}";

    private readonly List<string> _dirs = new();

    public void Dispose()
    {
        foreach (var dir in _dirs)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // Still open elsewhere; the temp cleaner takes it later.
            }
        }
    }

    private string Temp()
    {
        var dir = AsksFixtures.NewTempDir();
        _dirs.Add(dir);
        return dir;
    }

    private static DateTimeOffset Utc(string s) => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    /// A language server stand-in: records the request and answers with a fixed body.
    private sealed class FakeHttpPost : IHttpPost
    {
        public string? Url { get; private set; }
        public IReadOnlyDictionary<string, string>? Headers { get; private set; }
        public string? Body { get; private set; }
        public TimeSpan? Timeout { get; private set; }
        public int Calls { get; private set; }
        public string? Reply { get; init; }

        public Task<string?> PostAsync(string url, IReadOnlyDictionary<string, string> headers, string body, TimeSpan timeout)
        {
            Calls++;
            Url = url;
            Headers = headers;
            Body = body;
            Timeout = timeout;
            return Task.FromResult(Reply);
        }
    }

    /// sqlite3 stand-in: answers the two queries a conversation needs. Null means the query failed.
    private sealed class FakeSqlite : ISqliteReader
    {
        public int Type { get; set; } = 15;
        public int Status { get; set; } = 8;
        public string Project { get; set; } = "NotchTasks";
        public bool Failing { get; set; }
        public string? Empty { get; set; }

        public string? Query(string dbFile, string sql)
        {
            if (Failing)
            {
                return null;
            }
            if (sql.Contains("from steps", StringComparison.Ordinal))
            {
                return Empty ?? $"[{{\"step_type\":{Type},\"status\":{Status}}}]";
            }
            if (sql.Contains("trajectory_metadata_blob", StringComparison.Ordinal))
            {
                return $"[{{\"h\":\"{Convert.ToHexString(Blob(Project))}\"}}]";
            }
            return null;
        }

        /// A metadata blob carrying a workspace uri, as the conversation writes it.
        private static byte[] Blob(string project)
        {
            var uri = "file:///Users/x/" + project;
            return new byte[] { 0x0a, (byte)uri.Length }.Concat(Encoding.UTF8.GetBytes(uri)).Concat(new byte[] { 0x7a }).ToArray();
        }
    }

    // ---- Quotas

    [Fact]
    public void AntigravityQuotas_ModelsSharingAResetAndUse_AreOneWindow_FullestFirst()
    {
        var now = Utc("2026-11-11T06:53:00Z");
        var p = AntigravityLimits.Parse(Antigravity, now);
        Assert.NotNull(p);
        Assert.Equal("Antigravity", p!.Name);
        Assert.Equal("process", p.Kind);
        Assert.Equal("com.google.antigravity", p.BundleID);
        Assert.Equal(new[] { "Gemini", "Claude/GPT-OSS" }, p.Windows.Select(w => w.Label).ToArray());
        Assert.Equal(75, p.Windows[0].Used);
        Assert.Equal(0, p.Windows[1].Used);
        Assert.Equal(Utc("2026-11-11T07:21:06Z"), p.Windows[0].Resets);
    }

    [Fact]
    public void AntigravityQuota_AlreadyReset_CountsAsUnused_NoQuotas_IsNull()
    {
        var later = Utc("2026-11-12T00:00:00Z");
        Assert.Equal(0, AntigravityLimits.Parse(Antigravity, later)!.Windows[0].Used);
        Assert.Null(AntigravityLimits.Parse("{\"userStatus\":{}}", later));
        Assert.Null(AntigravityLimits.Parse("not json", later));
    }

    [Fact]
    public async Task FetchAntigravityLimits_AsksTheLiveLanguageServer_NamedInTheDaemonFolder()
    {
        var home = Temp();
        var daemon = Path.Combine(home, ".gemini", "antigravity", "daemon");
        Directory.CreateDirectory(daemon);
        File.WriteAllText(Path.Combine(daemon, "hub.json"),
            "{\"pid\":4321,\"httpPort\":4455,\"csrfToken\":\"tok-123\"}");
        var http = new FakeHttpPost { Reply = Antigravity };
        var limits = new AntigravityLimits(pid => pid == 4321);

        var p = await limits.FetchAsync(home, http, Utc("2026-11-11T06:53:00Z"), CancellationToken.None);

        Assert.NotNull(p);
        Assert.Equal(new[] { "Gemini", "Claude/GPT-OSS" }, p!.Windows.Select(w => w.Label).ToArray());
        Assert.Equal("http://127.0.0.1:4455/exa.language_server_pb.LanguageServerService/GetUserStatus", http.Url);
        Assert.Equal("tok-123", http.Headers!["X-Codeium-Csrf-Token"]);
        Assert.Equal("1", http.Headers["Connect-Protocol-Version"]);
        Assert.Equal("application/json", http.Headers["Content-Type"]);
        Assert.Equal("{\"metadata\":{\"ideName\":\"antigravity\",\"extensionName\":\"antigravity\",\"locale\":\"en\"}}", http.Body);
        Assert.Equal(TimeSpan.FromSeconds(5), http.Timeout);
    }

    [Fact]
    public async Task FetchAntigravityLimits_DeadDaemon_IsSkipped_NoDaemonFolder_IsNull()
    {
        var home = Temp();
        var daemon = Path.Combine(home, ".gemini", "antigravity", "daemon");
        Directory.CreateDirectory(daemon);
        File.WriteAllText(Path.Combine(daemon, "hub.json"),
            "{\"pid\":4194301,\"httpPort\":1,\"csrfToken\":\"x\"}");
        var http = new FakeHttpPost { Reply = Antigravity };
        var limits = new AntigravityLimits(_ => false);
        var now = Utc("2026-11-11T06:53:00Z");

        Assert.Null(await limits.FetchAsync(home, http, now, CancellationToken.None));
        Assert.Equal(0, http.Calls);
        Assert.Null(await limits.FetchAsync(Temp(), http, now, CancellationToken.None));
        Assert.Equal(0, http.Calls);
    }

    // ---- Conversations

    [Fact]
    public void AntigravityMidTurn_RunningSteps_AndUnansweredToolStep_ButNotAFinishedReply()
    {
        Assert.True(AntigravityConversation.MidTurn(15, 8));
        Assert.True(AntigravityConversation.MidTurn(132, 3));
        Assert.True(AntigravityConversation.MidTurn(21, 2));
        Assert.False(AntigravityConversation.MidTurn(15, 3));
        Assert.False(AntigravityConversation.MidTurn(21, 6));
        Assert.False(AntigravityConversation.MidTurn(21, 7));
    }

    [Fact]
    public void AntigravityProject_WorkspaceFolder_FromMetadataBlob_VarintLengths()
    {
        var shortBlob = new byte[] { 0x0a, 26 }
            .Concat(Encoding.UTF8.GetBytes("file:///Users/x/NotchTasks"))
            .Concat(new byte[] { 0x7a }).ToArray();
        Assert.Equal("NotchTasks", AntigravityConversation.Project(shortBlob));

        var path132 = "file:///" + new string('a', 121) + "/NT";
        var longBlob = new byte[] { 0x0a, 0x84, 0x01 }
            .Concat(Encoding.UTF8.GetBytes(path132))
            .Concat(new byte[] { 0x7a }).ToArray();
        Assert.Equal("NT", AntigravityConversation.Project(longBlob));

        Assert.Null(AntigravityConversation.Project(Encoding.UTF8.GetBytes("no uri here")));
    }

    [Fact]
    public void AntigravityScanner_RunningShows_HeldBetweenSteps_CrashLeftMidStepClearsAfter10Min()
    {
        var root = Temp();
        var db = Path.Combine(root, "conv-1.db");
        File.WriteAllText(db, "x");
        var t0 = new DateTimeOffset(2026, 11, 11, 6, 53, 0, TimeSpan.Zero);
        void Touch(DateTimeOffset t) => File.SetLastWriteTimeUtc(db, t.UtcDateTime);
        Touch(t0);
        var sql = new FakeSqlite { Type = 15, Status = 8, Project = "NotchTasks" };
        var scanner = new AntigravityScanner(root, sql);

        var jobs = scanner.Jobs(t0);
        Assert.Single(jobs);
        Assert.True(jobs[0].Working);
        Assert.Equal("NotchTasks", jobs[0].Title);

        // the conversation writes its next step while the agent is between steps: held, not gone
        sql.Status = 3;
        Touch(t0.AddSeconds(1));
        Assert.Single(scanner.Jobs(t0.AddSeconds(1)));
        Assert.Empty(scanner.Jobs(t0.AddSeconds(60)));

        // a step left "generating" by a crash: nothing written for 10 minutes
        sql.Status = 8;
        Touch(t0);
        Assert.Empty(new AntigravityScanner(root, sql).Jobs(t0.AddMinutes(11)));
    }

    [Fact]
    public void AntigravityScanner_IdleConversation_OpenedNow_IsNotAJob()
    {
        var root = Temp();
        var db = Path.Combine(root, "old.db");
        File.WriteAllText(db, "x");
        var t0 = new DateTimeOffset(2026, 11, 11, 6, 53, 0, TimeSpan.Zero);
        File.SetLastWriteTimeUtc(db, t0.UtcDateTime);
        var sql = new FakeSqlite { Type = 15, Status = 3 };

        Assert.Empty(new AntigravityScanner(root, sql).Jobs(t0));
    }

    [Fact]
    public void ReadAntigravityConversation_NullWhenTheFileIsNotAConversation()
    {
        var file = Path.Combine(Temp(), "junk.db");
        File.WriteAllText(file, "not a database");

        Assert.Null(AntigravityConversation.Read(file, new FakeSqlite { Failing = true }));
        Assert.Null(AntigravityConversation.Read(file, new FakeSqlite { Empty = string.Empty }));
    }

    [Fact]
    public void ReadAntigravityConversation_ReadsActivityAndTitle()
    {
        var file = Path.Combine(Temp(), "conv.db");
        File.WriteAllText(file, "x");
        var state = AntigravityConversation.Read(file, new FakeSqlite { Type = 21, Status = 2, Project = "Demo" });

        Assert.NotNull(state);
        Assert.True(state!.Active);
        Assert.Equal("Demo", state.Title);
    }
}
