using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using NotchTasks.Core;
using NotchTasks.Core.Asks;
using NotchTasks.Core.Sync;
using Xunit;

namespace NotchTasks.Core.Tests.Sync;

/// Port of the cloud tests of electron/test/sync.test.js (pairing, the snapshot write, Pro, the answers stream), with a manual clock
/// and Tick() in place of the timers. The LAN tests come with the LAN half.
[Collection("L10n")]
public sealed class SyncHubCloudTests
{
    private const string GroupId = "group123456789012";
    private const long T0 = 1_800_000_000_000;      // a fixed clock (ms)
    private const long ProUntil = 4_000_000_000_000; // a Pro that lasts past every test
    private static readonly string KeyText = Crosscheck.Read("key.txt");
    private static readonly byte[] Key = Box.FromB64Url(KeyText);
    private static readonly string ProJson = $"{{\"pro\":true,\"until\":{ProUntil}}}";

    /// The clock the tests move by hand.
    private sealed class Clock
    {
        public double Ms { get; set; } = T0;

        public DateTimeOffset Now => DateTimeOffset.FromUnixTimeMilliseconds((long)Ms);
    }

    /// A hub wired to the fakes, with everything it reports collected in lists.
    private sealed class Rig : IAsyncDisposable
    {
        private readonly ConcurrentQueue<string> _logs = new();
        private readonly ConcurrentQueue<TimeSpan> _delays = new();

        public Rig(bool group)
        {
            if (group)
            {
                Vault.Stored = new SyncGroup(GroupId, KeyText, "uid-mac", "ref-1");
            }
            var deps = new SyncDeps(
                Fb,
                Vault,
                () => new NullAdvertiser(),
                "Test PC",
                () => Clock.Now,
                () => Away,
                g => Notes.TryGetValue(g, out var at) ? at : 0,
                (g, at) => Notes[g] = at,
                message => _logs.Enqueue(message))
            {
                Delay = (t, _) =>
                {
                    _delays.Enqueue(t);
                    return Task.CompletedTask;
                },
            };
            Hub = new SyncHub(deps);
            Hub.StateChanged += s => States.Add(s);
            Hub.Answered += a => Answers.Add(a);
            Hub.ProNote += text => ProNotes.Add(text);
        }

        public FakeFirebase Fb { get; } = new();

        public FakeVault Vault { get; } = new();

        public Clock Clock { get; } = new();

        public bool Away { get; set; }

        public SyncHub Hub { get; }

        public ConcurrentBag<SyncState> States { get; } = [];

        public ConcurrentBag<PhoneAnswer> Answers { get; } = [];

        public ConcurrentBag<string> ProNotes { get; } = [];

        public Dictionary<string, double> Notes { get; } = [];

        public IReadOnlyList<TimeSpan> Delays => _delays.ToArray();

        /// The snapshot PUTs so far, each parsed.
        public List<JsonNode> Snaps() => Fb.Calls
            .Where(c => c.Method == "PUT" && c.Url.Contains("/snapshot.json", StringComparison.Ordinal))
            .Select(c => JsonNode.Parse(c.Body!)!)
            .ToList();

        public int SecureTokenCalls() => Fb.Calls.Count(c => c.Url.Contains("securetoken", StringComparison.Ordinal));

        public async ValueTask DisposeAsync() => await Hub.DisposeAsync();
    }

    private static SyncRun Run(string id) => new(id, "claude", "api", "Running", DateTimeOffset.FromUnixTimeMilliseconds(T0), null);

    private static SyncAsk Ask(string id = "a1") => new(
        new AgentAsk(id, 42, "s1", "Bash", "ls -la", [], DateTimeOffset.FromUnixTimeMilliseconds(T0), "claude"),
        null);

    private static SyncInput Input(IReadOnlyList<SyncRun>? runs = null, IReadOnlyList<SyncAsk>? asks = null) =>
        new(runs ?? [], [], asks ?? [], [], null, null);

    private static string Str(JsonNode node, string key) => node[key]!.GetValue<string>();

    private static string Fixture(string name) => Crosscheck.Read(name);

    private static string Event(string evt, string path, JsonObject data) =>
        $"event: {evt}\ndata: {SyncJson.Write(new JsonObject { ["path"] = path, ["data"] = data })}\n\n";

    private static async Task WaitFor(Func<bool> condition, int ms = 3000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (!condition())
        {
            if (DateTime.UtcNow > end)
            {
                throw new TimeoutException("timed out waiting");
            }
            await Task.Delay(5);
        }
    }

    private static bool Deleted(Rig rig, string path) =>
        rig.Fb.Calls.Any(c => c.Method == "DELETE" && c.Url.Contains(path, StringComparison.Ordinal));

    [Fact]
    public async Task Pair_SignsUpAnonymously_WritesTheMacMember_ReturnsACodeThatParses()
    {
        await using var rig = new Rig(group: false);
        var (code, url) = await rig.Hub.PairAsync();

        var p = Box.ParsePairing(code);
        Assert.NotNull(p);
        Assert.Equal("Test PC", p.N);
        Assert.Equal(p, Box.ParsePairing(url));
        Assert.Contains(rig.Fb.Calls, c => c.Url.Contains("accounts:signUp", StringComparison.Ordinal));

        var put = rig.Fb.Calls.Single(c => c.Method == "PUT" && c.Url.Contains($"/groups/{p.G}/members/uid-mac.json", StringComparison.Ordinal));
        var body = JsonNode.Parse(put.Body!)!;
        Assert.Equal("mac", Str(body, "role"));
        Assert.Equal("Test PC", Str(body, "name"));
        Assert.Equal("timestamp", body["at"]![".sv"]!.GetValue<string>());

        Assert.Equal("uid-mac", rig.Vault.Stored!.Uid);
        Assert.Equal(p.G, rig.Hub.Group!.G);
        Assert.Equal(code, (await rig.Hub.PairAsync()).Code);
        await rig.Hub.StopAsync();
    }

    [Fact]
    public async Task Update_WritesASealedSnapshot_NotTheSameStateAgain_AndWritesAtOnceWhenAsksChange()
    {
        await using var rig = new Rig(group: true);
        rig.Fb.EntitlementJson = ProJson;

        rig.Hub.Update(Input(runs: [Run("r1")]));
        await WaitFor(() => rig.Snaps().Count == 1);
        var first = rig.Snaps()[0];
        var opened = Box.Open(Str(first, "box"), Key);
        Assert.Equal("Test PC", Str(opened, "mac"));
        Assert.Equal("api", opened["runs"]![0]!["title"]!.GetValue<string>());
        Assert.Equal(0, first["asks"]!.GetValue<int>());
        await Task.Delay(30);

        rig.Hub.Update(Input(runs: [Run("r1")]));
        await Task.Delay(30);
        Assert.Single(rig.Snaps());

        rig.Hub.Update(Input(runs: [Run("r1")], asks: [Ask()]));
        await WaitFor(() => rig.Snaps().Count == 2);
        var second = rig.Snaps()[1];
        Assert.Equal(1, second["asks"]!.GetValue<int>());
        Assert.Equal(T0, second["newest"]!.GetValue<long>());
        Assert.Equal("ls -la", Str(Box.Open(Str(second, "push"), Key), "summary"));
        await rig.Hub.StopAsync();
    }

    [Fact]
    public async Task WithoutPro_TheSealedPartsStayEmpty_AndAsksStillGo()
    {
        await using var rig = new Rig(group: true);

        rig.Hub.Update(Input(runs: [Run("r1")], asks: [Ask()]));
        await WaitFor(() => rig.Snaps().Count == 1);
        var body = rig.Snaps()[0];
        Assert.Equal("", Str(body, "box"));
        Assert.Equal("", Str(body, "crew"));
        Assert.Equal(0, body["crewCount"]!.GetValue<int>());
        Assert.Equal(1, body["asks"]!.GetValue<int>());
        Assert.NotEqual("", Str(body, "push"));
        await rig.Hub.StopAsync();
    }

    [Fact]
    public async Task NonAskChanges_WaitForAMinute_WithPro()
    {
        await using var rig = new Rig(group: true);
        rig.Fb.EntitlementJson = ProJson;

        rig.Hub.Update(Input(runs: [Run("r1")]));
        await WaitFor(() => rig.Snaps().Count == 1);
        await Task.Delay(30);

        rig.Clock.Ms += 1000;
        rig.Hub.Update(Input(runs: [Run("r1"), Run("r2")])); // a second agent starts: the crew changes
        await Task.Delay(30);
        Assert.Single(rig.Snaps());

        rig.Clock.Ms += 60_000;
        rig.Hub.Update(Input(runs: [Run("r1"), Run("r2")]));
        await WaitFor(() => rig.Snaps().Count == 2);
        var crew = Box.Open(Str(rig.Snaps()[1], "box"), Key)["crew"]!;
        Assert.Equal(2, crew["count"]!.GetValue<int>());
        await rig.Hub.StopAsync();
    }

    [Fact]
    public async Task Record_WhenAwayWithPro_SealsTheEventIntoTheNextWrite()
    {
        await using var rig = new Rig(group: true);
        rig.Fb.EntitlementJson = ProJson;
        rig.Away = true;

        rig.Hub.Update(Input(runs: [Run("r1")]));
        await WaitFor(() => rig.Snaps().Count == 1);
        rig.Hub.Record("taskDone", "Done", "all clear", toPhone: true);
        rig.Clock.Ms += 61_000;
        rig.Hub.Update(Input(runs: [Run("r1"), Run("r2")]));
        await WaitFor(() => rig.Snaps().Count == 2);

        var body = rig.Snaps()[1];
        Assert.Equal("Done", Str(Box.Open(Str(body, "eventBox"), Key), "title"));
        Assert.True(body["eventAt"]!.GetValue<long>() > 0);
        await rig.Hub.StopAsync();
    }

    [Fact]
    public async Task AnswerFromTheEventStream_ReachesAnswered_AndItsPathIsDeleted()
    {
        await using var rig = new Rig(group: true);
        await rig.Hub.StartAsync();
        await WaitFor(() => rig.Fb.Events is not null);

        await rig.Fb.SendEventAsync(Event("put", "/ans1", new JsonObject { ["box"] = Fixture("swift-allow.box"), ["at"] = T0 }));
        await WaitFor(() => rig.Answers.Count == 1);
        var answer = rig.Answers.Single();
        Assert.Equal("a1", answer.Ask);
        Assert.Equal(ReplyKinds.Allow, answer.Reply.Kind);
        await WaitFor(() => Deleted(rig, $"/groups/{GroupId}/answers/ans1.json"));
        await rig.Hub.StopAsync();
    }

    [Fact]
    public async Task AnAnswerOlderThan590Seconds_IsIgnored_ButStillDeleted()
    {
        await using var rig = new Rig(group: true);
        await rig.Hub.StartAsync();
        await WaitFor(() => rig.Fb.Events is not null);

        await rig.Fb.SendEventAsync(Event("patch", "/old1", new JsonObject { ["box"] = Fixture("swift-allow.box"), ["at"] = T0 - 600_000 }));
        await WaitFor(() => Deleted(rig, "/answers/old1.json"));
        Assert.Empty(rig.Answers);
        await rig.Hub.StopAsync();
    }

    [Fact]
    public async Task Rest401_ClearsTheToken_AndTheNextWriteRenewsIt()
    {
        await using var rig = new Rig(group: true);
        rig.Fb.FailNext("PUT", "/snapshot.json", 401);

        rig.Hub.Update(Input(runs: [Run("r1")]));
        await WaitFor(() => rig.Hub.State.Problem is not null);
        Assert.Equal(1, rig.SecureTokenCalls());

        rig.Hub.Update(Input(runs: [Run("r1")], asks: [Ask()]));
        await WaitFor(() => rig.Hub.State.Problem is null);
        Assert.Equal(2, rig.SecureTokenCalls());
        await rig.Hub.StopAsync();
    }

    [Fact]
    public async Task StreamFails_BacksOff_FiveThenTenSeconds_AndSetsTheProblem()
    {
        await using var rig = new Rig(group: true);
        rig.Fb.FailStreamOpens(2);

        await rig.Hub.StartAsync();
        await WaitFor(() => rig.Delays.Count == 2);
        Assert.Equal(new[] { TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10) }, rig.Delays);
        Assert.Equal("Lost connection to the phone, retrying…", rig.Hub.State.Problem);
        await rig.Hub.StopAsync();
    }

    [Fact]
    public async Task WriteFails_ProblemIsSet_ThenClearedOnTheNextSuccess()
    {
        await using var rig = new Rig(group: true);
        rig.Fb.FailNext("PUT", "/snapshot.json", 500);

        rig.Hub.Update(Input(runs: [Run("r1")]));
        await WaitFor(() => rig.Hub.State.Problem?.StartsWith("Couldn't send to the phone:", StringComparison.Ordinal) == true);

        rig.Hub.Update(Input(runs: [Run("r1")], asks: [Ask()]));
        await WaitFor(() => rig.Hub.State.Problem is null);
        await rig.Hub.StopAsync();
    }

    [Fact]
    public async Task AnswerThatDoesNotOpen_IsDeleted_AndNothingIsRaised()
    {
        await using var rig = new Rig(group: true);
        await rig.Hub.StartAsync();
        await WaitFor(() => rig.Fb.Events is not null);

        await rig.Fb.SendEventAsync(Event("put", "/bad1", new JsonObject { ["box"] = "not a box!", ["at"] = T0 }));
        await WaitFor(() => Deleted(rig, "/answers/bad1.json"));
        Assert.Empty(rig.Answers);
        await rig.Hub.StopAsync();
    }

    [Fact]
    public async Task AnswerRefresh_ResendsEverything()
    {
        await using var rig = new Rig(group: true);
        rig.Hub.Update(Input(runs: [Run("r1")]));
        await WaitFor(() => rig.Snaps().Count == 1);
        await rig.Hub.StartAsync();
        await WaitFor(() => rig.Fb.Events is not null);

        await rig.Fb.SendEventAsync(Event("put", "/ref1", new JsonObject { ["box"] = Fixture("swift-refresh.box"), ["at"] = T0 }));
        await WaitFor(() => rig.Snaps().Count == 2);
        await WaitFor(() => Deleted(rig, "/answers/ref1.json"));
        Assert.Empty(rig.Answers);
        await rig.Hub.StopAsync();
    }

    [Fact]
    public async Task AnswerHello_IsNotRaised()
    {
        await using var rig = new Rig(group: true);
        await rig.Hub.StartAsync();
        await WaitFor(() => rig.Fb.Events is not null);

        await rig.Fb.SendEventAsync(Event("put", "/hello1", new JsonObject { ["box"] = Fixture("swift-hello.box"), ["at"] = T0 }));
        await WaitFor(() => Deleted(rig, "/answers/hello1.json"));
        Assert.Empty(rig.Answers);
        await rig.Hub.StopAsync();
    }

    [Fact]
    public async Task UnpairAll_DeletesThePhonesThenTheMac_AndClearsTheVault()
    {
        await using var rig = new Rig(group: true);
        rig.Fb.MembersJson = "{\"uid-phone\":{\"role\":\"phone\",\"name\":\"Pixel\"},\"uid-mac\":{\"role\":\"mac\",\"name\":\"Test PC\"}}";

        await rig.Hub.UnpairAllAsync();

        var deletes = rig.Fb.Calls.Where(c => c.Method == "DELETE").Select(c => c.Url).ToList();
        Assert.Equal(2, deletes.Count);
        Assert.Contains("/members/uid-phone.json", deletes[0], StringComparison.Ordinal);
        Assert.Contains("/members/uid-mac.json", deletes[1], StringComparison.Ordinal);
        Assert.Null(rig.Vault.Saves[^1]);
        Assert.False(rig.Hub.State.Paired);
    }

    [Fact]
    public async Task UnpairAll_DuringAWrite_DoesNotRevive_AndWritesNoMore()
    {
        await using var rig = new Rig(group: true);
        var hold = new TaskCompletionSource();
        rig.Fb.HoldSnapshot = hold;

        rig.Hub.Update(Input(runs: [Run("r1")]));
        await WaitFor(() => rig.Snaps().Count == 1);
        await rig.Hub.UnpairAllAsync();

        hold.SetResult();
        await Task.Delay(50);
        Assert.Null(rig.Hub.Group);

        rig.Hub.Tick();
        rig.Hub.Update(Input(runs: [Run("r1"), Run("r2")], asks: [Ask()]));
        await Task.Delay(30);
        Assert.Single(rig.Snaps());
    }

    [Fact]
    public async Task ProNote_NewerThanSeen_IsRaisedOnceAndStored()
    {
        await using var rig = new Rig(group: true);
        rig.Fb.ProNoteJson = "{\"why\":\"moved\",\"at\":1000}";

        await rig.Hub.LoadPhonesAsync();
        Assert.Equal(new[] { "The group’s Pro moved to another computer" }, rig.ProNotes);
        Assert.Equal(1000d, rig.Notes[GroupId]);
        Assert.Equal("moved", rig.Hub.State.ProNote!.Why);

        await rig.Hub.LoadPhonesAsync();
        Assert.Single(rig.ProNotes);
    }

    [Fact]
    public async Task Report_WithoutAGroup_SignsUpAndPostsTheFields()
    {
        await using var rig = new Rig(group: false);

        var id = await rig.Hub.SendReportAsync("Pairing", "x", "me@example.com", new ReportInfo("1.0", "2", "Windows 11", "en"));

        Assert.Equal("-NRep1", id);
        var post = rig.Fb.Calls.Single(c => c.Method == "POST" && c.Url.Contains("/reports.json", StringComparison.Ordinal));
        var body = JsonNode.Parse(post.Body!)!;
        Assert.Equal("uid-mac", Str(body, "uid"));
        Assert.Equal("mac", Str(body, "role"));
        Assert.Equal("problem", Str(body, "kind"));
        Assert.False(body["auto"]!.GetValue<bool>());
        Assert.Equal("Pairing: x", Str(body, "text"));
        Assert.Equal("PC", Str(body["app"]!, "model"));
        Assert.False(body["context"]!["paired"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Report_TextAndContact_AreCutTo2000And200()
    {
        await using var rig = new Rig(group: true);

        await rig.Hub.SendReportAsync("Topic", new string('x', 3000), new string('c', 300), new ReportInfo("1", "2", "3", "en"));

        var body = JsonNode.Parse(rig.Fb.Calls.Single(c => c.Method == "POST" && c.Url.Contains("/reports.json", StringComparison.Ordinal)).Body!)!;
        Assert.Equal(2000, Str(body, "text").Length);
        Assert.StartsWith("Topic: ", Str(body, "text"), StringComparison.Ordinal);
        Assert.Equal(200, Str(body, "contact").Length);
    }
}
