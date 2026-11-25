using System.Text.Json.Nodes;
using NotchTasks.Core.Asks;
using NotchTasks.Core.Sync;
using Xunit;

namespace NotchTasks.Core.Tests.Sync;

/// Port of electron/test/wire.test.js. The last test there writes the js-* files for the Swift check and is not ported.
public sealed class WireTests
{
    private static readonly byte[] Key = Box.FromB64Url(Crosscheck.Read("key.txt"));
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeMilliseconds(1794380000250); // the Swift fixtures' time
    private const double T0Swift = 816072800.25; // the same moment as Swift's reference date (since 2001)
    private const string UuidA = "6F9619FF-8B86-D011-B42D-00C04FC964FF";
    private const string UuidB = "904DCCFE-D16E-4571-AB4E-BEFC72ADF9C8";
    private const string UuidC = "0B5B8C2E-3D4F-4A67-9C10-8E1D2F3A4B5C";

    private static JsonNode OpenFix(string name) => Box.Open(Crosscheck.Read(name), Key);

    private static PhoneAnswer ReadFix(string name) => Wire.ReadAnswer(OpenFix(name));

    private static List<string> Keys(JsonObject o) => o.Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();

    private static JsonObject Item(JsonNode? array, int i) => array!.AsArray()[i]!.AsObject();

    private static JsonObject Field(JsonObject o, string key) => o[key]!.AsObject();

    private static List<(string Key, string Value)> Pairs(IReadOnlyDictionary<string, string>? d) =>
        d?.Select(p => (p.Key, p.Value)).ToList() ?? [];

    /// A phone's answer as the phone seals it, with the one field changed (null removes it).
    private static JsonObject Answer(string key, JsonNode? value)
    {
        var o = new JsonObject { ["ask"] = "a1", ["reply"] = new JsonObject { ["allow"] = new JsonObject() }, ["at"] = T0Swift };
        o[key] = value;
        return o;
    }

    private static JsonObject FullNotch() => new()
    {
        ["size"] = "compact",
        ["expandMode"] = "hover",
        ["accent"] = "custom",
        ["customAccent"] = "#00F5D4",
        ["background"] = "glow",
        ["gradientA"] = "#0F172A",
        ["gradientB"] = "#000000",
        ["toastStyle"] = "card",
        ["motion"] = "bouncy",
        ["modules"] = new JsonArray("agents", "ci", "media", "calendar", "timer", "shelf", "limits", "battery", "devices", "calls", "privacy", "levels", "network", "downloads", "health", "mirror"),
        ["disabled"] = new JsonArray("mirror", "shelf"),
        ["placements"] = new JsonObject { ["agents"] = "wings" },
        ["haptics"] = true,
        ["sounds"] = true,
        ["volume"] = 0.8,
        ["systemNotifications"] = true,
        ["doneAfter"] = 30,
        ["limitWarn"] = 80,
        ["lockScreen"] = true,
        ["lockShows"] = new JsonArray("lock"),
        ["badges"] = new JsonArray(
            new JsonObject { ["id"] = UuidA, ["symbol"] = "star.fill", ["sprite"] = "px-buddy" },
            new JsonObject { ["id"] = UuidB, ["symbol"] = "star.fill", ["sprite"] = "px-cat", ["right"] = true },
            new JsonObject
            {
                ["id"] = UuidC, ["symbol"] = "bolt.fill", ["text"] = "AI", ["color"] = "#FF375F", ["fg"] = "#FFFFFF", ["plain"] = false,
                ["shape"] = "rounded", ["border"] = true, ["right"] = true, ["frames"] = 4, ["fps"] = 12.5,
            }),
        ["lockOwnBadges"] = false,
        ["lockBadges"] = new JsonArray(),
        ["toolIcons"] = new JsonObject
        {
            ["Xcode"] = new JsonObject { ["id"] = UuidA, ["symbol"] = "star.fill", ["sprite"] = "px-gears", ["plain"] = true },
        },
        ["seenTools"] = new JsonArray("Xcode", "Gradle", "GitHub Actions"),
    };

    /// The full snapshot the Mac would send: every field, with an icon, a notch, a job, a crew.
    private static SyncSnapshot FullSnapshot() => new(
        Mac: "Hùng’s MacBook \"Pro\"",
        Runs: [new SyncRun("r1", "gemini", "api", "Running tests", T0, new JsonObject { ["id"] = UuidA, ["symbol"] = "star.fill", ["sprite"] = "px-gears", ["plain"] = true })],
        Builds: [new SyncRun("b1", null, "Xcode", "build", T0, null)],
        Asks:
        [
            new SyncAsk(new AgentAsk("a1", 4242, "NotchTasks", "Bash", "rm -rf build", [], T0, "claude"),
                new JsonObject { ["id"] = UuidB, ["symbol"] = "star.fill", ["sprite"] = "px-cat", ["plain"] = true }),
            new SyncAsk(new AgentAsk("a2", 1, "app", "AskUserQuestion", "Which DB?",
                [new AskQuestion("Which DB?", "DB", ["SQLite", "JSON"], false)], T0, "codex"), null),
        ],
        Limits: [new SyncLimit("Claude", "5h", 72, T0.AddHours(1))],
        Health: new SyncHealth(64, false, 1, 52, 3200, 45, 70),
        Events: [new SyncEvent("e1", "ciFailed", "CI failed", "deploy", T0)],
        Job: new SyncJob("Xcode", "build", T0, "running", null,
            new JsonObject { ["id"] = UuidC, ["symbol"] = "star.fill", ["sprite"] = "px-gears", ["plain"] = true }),
        Notch: FullNotch(),
        Crew: new SyncCrew(1, "api", "x", T0, "gemini", null, [new SyncMember("gemini", null)]),
        At: T0);

    [Fact]
    public void SwiftDate_SecondsSince2001_InAndOut()
    {
        Assert.Equal(0.0, SwiftDate.From(new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero)));
        Assert.Equal(1.25, SwiftDate.From(new DateTimeOffset(2001, 1, 1, 0, 0, 1, 250, TimeSpan.Zero)));
        Assert.Equal(T0Swift, SwiftDate.From(T0));
        Assert.Equal(new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero), SwiftDate.To(0));
        Assert.Equal(T0, SwiftDate.To(T0Swift));
        Assert.True(T0 == SwiftDate.To(OpenFix("snapshot.box")["at"]!.GetValue<double>()), "the Swift snapshot date");
        Assert.Throws<FormatException>(() => SwiftDate.To(double.NaN));
        Assert.Throws<FormatException>(() => SwiftDate.To(double.PositiveInfinity));
    }

    [Fact]
    public void SwiftSealedAnswers_OpenAndMeanWhatTheySay()
    {
        foreach (var (name, kind) in new[] { ("swift-allow.box", "allow"), ("swift-deny.box", "deny"), ("swift-pass.box", "pass") })
        {
            var a = ReadFix(name);
            Assert.Equal("a1", a.Ask);
            Assert.Equal(kind, a.Reply.Kind);
            Assert.Null(a.Reply.Answers);
            Assert.Equal(T0, a.At);
            Assert.Null(a.Phone);
        }

        var answers = ReadFix("swift-answers.box");
        Assert.Equal("a2", answers.Ask);
        Assert.Equal("answers", answers.Reply.Kind);
        Assert.Equal([("Which DB?", "SQLite, JSON")], Pairs(answers.Reply.Answers));
        Assert.Equal(T0, answers.At);
        Assert.Null(answers.Phone);

        var hello = ReadFix("swift-hello.box");
        Assert.Equal("", hello.Ask);
        Assert.Equal("uid-1", hello.Phone);
        Assert.Equal("pass", hello.Reply.Kind);

        var timer = ReadFix("swift-timer.box");
        Assert.Equal(25, timer.Timer);
        Assert.Null(timer.Notch);
        Assert.False(timer.Refresh);

        Assert.True(ReadFix("swift-refresh.box").Refresh);

        var n = ReadFix("swift-notch.box").Notch!;
        var badges = n["badges"]!.AsArray();
        Assert.Equal(3, badges.Count);
        Assert.Equal("px-buddy", badges[0]!["sprite"]!.GetValue<string>());
        Assert.Equal("AI", badges[2]!["text"]!.GetValue<string>());
        Assert.True(badges[2]!["border"]!.GetValue<bool>());
        Assert.True(badges[2]!["right"]!.GetValue<bool>());
        Assert.Equal("rounded", badges[2]!["shape"]!.GetValue<string>());
        Assert.True(UuidA == badges[2]!["id"]!.GetValue<string>(), "the id Swift wrote");
        Assert.Equal("px-gears", Field(Field(n, "toolIcons"), "Xcode")["sprite"]!.GetValue<string>());
        Assert.Equal("custom", n["accent"]!.GetValue<string>());
        Assert.Equal("#00F5D4", n["customAccent"]!.GetValue<string>());
        Assert.Equal(16, n["modules"]!.AsArray().Count);
    }

    [Fact]
    public void ReadAnswer_RefusesWhatAPhoneCouldNotHaveSent()
    {
        Assert.Equal("allow", Wire.ReadAnswer(Answer("timer", null)).Reply.Kind);
        Assert.Throws<FormatException>(() => Wire.ReadAnswer(Answer("ask", null))); // no ask
        Assert.Throws<FormatException>(() => Wire.ReadAnswer(Answer("reply", new JsonObject { ["boom"] = new JsonObject() }))); // unknown reply
        Assert.Throws<FormatException>(() => Wire.ReadAnswer(Answer("reply", new JsonObject
        {
            ["answers"] = new JsonObject { ["_0"] = new JsonObject { ["q"] = 1 } },
        }))); // answer not a string
        Assert.Throws<FormatException>(() => Wire.ReadAnswer(Answer("at", "x"))); // bad date
        Assert.Throws<FormatException>(() => Wire.ReadAnswer(Answer("timer", 1.5))); // timer not an integer
        Assert.Throws<FormatException>(() => Wire.ReadAnswer(Answer("refresh", "yes"))); // refresh not a boolean
        Assert.Throws<FormatException>(() => Wire.ReadAnswer(null)); // not an object
    }

    [Fact]
    public void EncodeReply_WritesSwiftEnumShapes_DecodeReplyReadsThemBack()
    {
        static string Text(AskReply r) => SyncJson.Write(Wire.EncodeReply(r));

        Assert.Equal("{\"allow\":{}}", Text(new AskReply("allow", null)));
        Assert.Equal("{\"deny\":{}}", Text(new AskReply("deny", null)));
        Assert.Equal("{\"pass\":{}}", Text(new AskReply("pass", null)));
        Assert.Equal("{\"answers\":{\"_0\":{\"q\":\"a\"}}}", Text(new AskReply("answers", new Dictionary<string, string> { ["q"] = "a" })));
        Assert.Equal("{\"answers\":{\"_0\":{}}}", Text(new AskReply("answers", new Dictionary<string, string>())));

        foreach (var r in new[]
        {
            new AskReply("allow", null),
            new AskReply("deny", null),
            new AskReply("pass", null),
            new AskReply("answers", new Dictionary<string, string> { ["Which DB?"] = "SQLite, JSON" }),
        })
        {
            var back = Wire.DecodeReply(JsonNode.Parse(Text(r)));
            Assert.Equal(r.Kind, back.Kind);
            Assert.Equal(Pairs(r.Answers), Pairs(back.Answers));
        }

        var decoded = Wire.DecodeReply(JsonNode.Parse("{\"answers\":{\"_0\":{\"Which DB?\":\"SQLite, JSON\"}}}"));
        Assert.Equal("answers", decoded.Kind);
        Assert.Equal([("Which DB?", "SQLite, JSON")], Pairs(decoded.Answers));

        foreach (var bad in new[]
        {
            "{}", "{\"allow\":null}", "{\"answers\":{}}", "{\"allow\":{},\"deny\":{}}", "[\"allow\"]", "\"allow\"", "null",
        })
        {
            Assert.Throws<FormatException>(() => Wire.DecodeReply(JsonNode.Parse(bad)));
        }
        Assert.Throws<FormatException>(() => Wire.EncodeReply(new AskReply("maybe", null)));
    }

    [Fact]
    public void ReferenceSnapshot_HasItsFields_AndNilKeysLeftOutAsSwiftLeavesThem()
    {
        var s = OpenFix("snapshot.box").AsObject();
        Assert.Equal("Hùng’s MacBook \"Pro\"", s["mac"]!.GetValue<string>());
        Assert.Equal("px-cat", Field(Item(s["asks"], 0), "icon")["sprite"]!.GetValue<string>());
        Assert.Empty(Item(s["asks"], 0)["questions"]!.AsArray());
        Assert.Equal("codex", Item(s["asks"], 1)["agent"]!.GetValue<string>());
        Assert.Equal("DB", Item(Item(s["asks"], 1)["questions"], 0)["header"]!.GetValue<string>());
        Assert.False(Item(s["asks"], 1).ContainsKey("icon"), "a nil icon is no key");
        Assert.False(Item(s["builds"], 0).ContainsKey("agent"), "a nil agent is no key");
        Assert.False(Field(s, "job").ContainsKey("ended"));
        Assert.Equal(64, Field(s, "health")["battery"]!.GetValue<int>());
        Assert.Equal(3, Field(s, "notch")["badges"]!.AsArray().Count);
        Assert.Single(Field(s, "crew")["members"]!.AsArray());
        Assert.Equal(T0Swift + 3600, Item(s["limits"], 0)["resets"]!.GetValue<double>());
    }

    [Fact]
    public void Snapshot_HasTheSameKeysAsTheSwiftSnapshotAtEveryLevelItCan()
    {
        var j = Wire.Snapshot(FullSnapshot());
        var sw = OpenFix("snapshot.box").AsObject();
        Assert.Equal(Keys(sw), Keys(j));
        Assert.Equal(Keys(Item(sw["asks"], 0)), Keys(Item(j["asks"], 0)));
        Assert.Equal(Keys(Item(sw["asks"], 1)), Keys(Item(j["asks"], 1)));
        Assert.Equal(Keys(Item(sw["limits"], 0)), Keys(Item(j["limits"], 0)));
        Assert.Equal(Keys(Field(sw, "health")), Keys(Field(j, "health")));
        Assert.Equal(Keys(Item(sw["events"], 0)), Keys(Item(j["events"], 0)));
        Assert.Equal(Keys(Field(sw, "job")), Keys(Field(j, "job")));
        Assert.Equal(Keys(Field(sw, "crew")), Keys(Field(j, "crew")));
        Assert.Equal(Keys(Item(Field(sw, "crew")["members"], 0)), Keys(Item(Field(j, "crew")["members"], 0)));
        Assert.Equal(Keys(Field(sw, "notch")), Keys(Field(j, "notch")));
        Assert.True(T0Swift == j["at"]!.GetValue<double>(), "dates are Swift seconds");
        Assert.Equal(T0Swift + 3600, Item(j["limits"], 0)["resets"]!.GetValue<double>());
    }

    [Fact]
    public void Snapshot_SealsAndOpensToItself()
    {
        var j = Wire.Snapshot(FullSnapshot());
        Assert.Equal(SyncJson.Write(j), SyncJson.Write(Box.Open(Box.Seal(j, Key), Key)));
    }

    [Fact]
    public void Snapshot_RefusesAnAgentThePhoneDoesNotKnow_AndAMissingNotchField()
    {
        var s = FullSnapshot() with { Runs = [new SyncRun("r1", "emacs", "api", "Running tests", T0, null)] };
        var e = Assert.Throws<FormatException>(() => Wire.Snapshot(s));
        Assert.Equal("unknown agent emacs", e.Message);

        var notch = FullNotch();
        notch.Remove("volume");
        var missing = Assert.Throws<FormatException>(() => Wire.Snapshot(FullSnapshot() with { Notch = notch }));
        Assert.Equal("notch: missing volume", missing.Message);
    }

    [Fact]
    public void Snapshot_DropsNilOptionals_NoIconJobEndedOrHealthWhenAbsent()
    {
        var j = Wire.Snapshot(new SyncSnapshot("M", [], [], [], [], null, [], null, null, null, T0));
        Assert.Equal(["asks", "at", "builds", "events", "limits", "mac", "runs"], Keys(j));
        Assert.Equal(T0Swift, j["at"]!.GetValue<double>());
    }

    [Fact]
    public void AskForPush_TrimsLikeAgentAskForPush_AndStillFitsAPush()
    {
        var longAsk = new SyncAsk(new AgentAsk(
            "a", 1, "s", "Bash", new string('x', 5000) + "é",
            [
                new AskQuestion(new string('Q', 250), "H", ["".PadRight(100, '1'), "2", "3", "4", "5", "6"], true),
                new AskQuestion("b", null, ["x"], false),
                new AskQuestion("c", null, ["y"], false),
            ],
            T0, "codex"), null);

        var p = Wire.AskForPush(longAsk).Ask;
        Assert.Equal(300, p.Summary.Length);
        Assert.Equal(2, p.Questions.Count);
        Assert.Equal(200, p.Questions[0].Question.Length);
        Assert.Equal(4, p.Questions[0].Options.Count);
        Assert.Equal(60, p.Questions[0].Options[0].Length);
        Assert.Equal("H", p.Questions[0].Header);
        Assert.True(p.Questions[0].MultiSelect);
        Assert.Equal("codex", p.Agent);
        Assert.True(Box.Seal(Wire.Ask(Wire.AskForPush(longAsk)), Key).Length < 3000, "the push box fits APNs");
    }

    [Fact]
    public void PrefixChars_CountsCharactersNotCodeUnits()
    {
        // "e" + combining acute is two code units but one character, as Intl.Segmenter counts it
        Assert.Equal(600, Wire.PrefixChars(string.Concat(Enumerable.Repeat("é", 301)), 300).Length);
        Assert.Equal("ab", Wire.PrefixChars("abc", 2));
        Assert.Equal("", Wire.PrefixChars("", 5));
    }

    [Fact]
    public void Badge_DefaultsAsTheMacSends()
    {
        var empty = Wire.Badge(new JsonObject());
        Assert.Matches("^[0-9A-F]{8}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{12}$", empty["id"]!.GetValue<string>());
        Assert.Equal("star.fill", empty["symbol"]!.GetValue<string>());
        Assert.Equal("circle", empty["shape"]!.GetValue<string>());
        Assert.False(empty["border"]!.GetValue<bool>());
        Assert.False(empty["right"]!.GetValue<bool>());
        Assert.False(empty.ContainsKey("image"));

        var upper = Wire.Badge(new JsonObject { ["id"] = "abc-def", ["sprite"] = "px-cat" });
        Assert.Equal("ABC-DEF", upper["id"]!.GetValue<string>());
        Assert.Equal("px-cat", upper["sprite"]!.GetValue<string>());
    }

    [Fact]
    public void Notch_KeepsExactlyTheWireKeys()
    {
        var n = Wire.Notch(FullNotch());
        Assert.Equal(Wire.NotchKeys.OrderBy(k => k, StringComparer.Ordinal).ToList(), Keys(n));
        Assert.Equal(3, n["badges"]!.AsArray().Count);
        Assert.Equal(25, Wire.NotchKeys.Length);
    }

    [Fact]
    public void AgentKinds_AreTheSixPhoneAgents()
    {
        Assert.Equal(["claude", "codex", "gemini", "antigravity", "cursor", "opencode"], Wire.AgentKinds);
    }
}
