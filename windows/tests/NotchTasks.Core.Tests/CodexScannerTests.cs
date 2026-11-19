using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using NotchTasks.Core.Agents;
using Xunit;

namespace NotchTasks.Core.Tests;

// Ported from electron/test/providers.test.js: codex status, codex limits, codex question (both forms), the three
// CodexScanner tests, and lastComponent. The added cases (half-written line, 50 MB tail) are from Task 4's brief.
// Every rollout is written to a temp folder; nothing touches ~/.codex.
[Collection("L10n")]
public sealed class CodexScannerTests : IDisposable
{
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

    private static string Ts(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// Writes a rollout: a session_meta head line, then the given lines. The mtime is set, so no test relies on the clock's resolution.
    private static string Rollout(string root, string name, string cwd, string sessionId, IEnumerable<string> lines, DateTimeOffset mtime)
    {
        var file = Path.Combine(root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var head = new JsonObject
        {
            ["timestamp"] = Ts(mtime),
            ["type"] = "session_meta",
            ["payload"] = new JsonObject { ["id"] = sessionId, ["cwd"] = cwd, ["session_id"] = sessionId },
        }.ToJsonString();
        File.WriteAllText(file, string.Join("\n", new[] { head }.Concat(lines)) + "\n");
        File.SetLastWriteTimeUtc(file, mtime.UtcDateTime);
        return file;
    }

    // ---- Status, limits, questions (pure parsers)

    [Fact]
    public void CodexStatus_LastTaskEventDecides()
    {
        const string started = """{"timestamp":"2026-11-05T03:07:53.935Z","type":"event_msg","payload":{"type":"task_started","turn_id":"t1"}}""";
        const string done = """{"timestamp":"2026-11-05T03:09:10.100Z","type":"event_msg","payload":{"type":"task_complete","turn_id":"t1"}}""";

        var s1 = CodexRollout.ParseStatus($"{started}\n{{\"type\":\"response_item\"}}\n");
        Assert.NotNull(s1);
        Assert.True(s1!.Working);
        Assert.Equal(DateTimeOffset.Parse("2026-11-05T03:07:53.935Z", CultureInfo.InvariantCulture), s1.At);

        var s2 = CodexRollout.ParseStatus($"{started}\n{done}\n");
        Assert.NotNull(s2);
        Assert.False(s2!.Working);

        Assert.Null(CodexRollout.ParseStatus("{\"type\":\"response_item\"}\n"));
    }

    [Fact]
    public void CodexLimits_PrimaryAndSecondary_PassedResetCountsAsUnused()
    {
        var now = new DateTimeOffset(2026, 11, 14, 10, 0, 0, TimeSpan.Zero);
        var sec = now.ToUnixTimeSeconds();
        var line = new JsonObject
        {
            ["timestamp"] = Ts(now),
            ["type"] = "event_msg",
            ["payload"] = new JsonObject
            {
                ["type"] = "token_count",
                ["rate_limits"] = new JsonObject
                {
                    ["primary"] = new JsonObject { ["used_percent"] = 42.5, ["window_minutes"] = 300, ["resets_at"] = sec + 3600 },
                    ["secondary"] = new JsonObject { ["used_percent"] = 12, ["window_minutes"] = 10080, ["resets_at"] = sec - 100 },
                },
            },
        }.ToJsonString();

        var r = CodexRollout.ParseLimits($"{{\"type\":\"event_msg\"}}\n{line}\n", now);

        Assert.NotNull(r);
        Assert.Equal(now, r!.At);
        Assert.Equal(new[] { ("5h", 42.5), ("7d", 0.0) }, r.Windows.Select(w => (w.Label, w.Used)));
        Assert.Null(CodexRollout.ParseLimits("{\"type\":\"x\"}\n", now));
    }

    [Fact]
    public void CodexQuestion_ReadUntilYouWriteBack()
    {
        var args = "{\"questions\":[{\"title\":\"Phần nào?\",\"options\":[\"JSON\",\"Cách duyệt\"]}]}";
        var q = new JsonObject
        {
            ["timestamp"] = "2026-11-05T03:07:53.935Z",
            ["type"] = "response_item",
            ["payload"] = new JsonObject
            {
                ["type"] = "function_call",
                ["name"] = "request_user_input_async",
                ["arguments"] = args,
                ["call_id"] = "call_1",
            },
        }.ToJsonString();

        var open = CodexRollout.ParseQuestion(q, "t1", null, DateTimeOffset.UtcNow);

        Assert.NotNull(open);
        Assert.Equal(new[] { "JSON", "Cách duyệt" }, open!.Questions[0].Options);
        Assert.Equal("Phần nào?", open.Questions[0].Question);
        Assert.Equal("call_1", open.CallId);

        var user = """{"timestamp":"2026-11-05T03:09:00.000Z","type":"response_item","payload":{"type":"message","id":"m","role":"user","content":[]}}""";
        Assert.Null(CodexRollout.ParseQuestion(user, "t1", open, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void CodexQuestion_TerminalForm_OptionsAreLabels()
    {
        var args = new JsonObject
        {
            ["questions"] = new JsonArray(new JsonObject
            {
                ["question"] = "Color?",
                ["header"] = "Pick",
                ["multiSelect"] = true,
                ["options"] = new JsonArray(new JsonObject { ["label"] = "Red" }, new JsonObject { ["label"] = "Blue" }),
            }),
        }.ToJsonString();
        var line = new JsonObject
        {
            ["type"] = "response_item",
            ["payload"] = new JsonObject
            {
                ["type"] = "function_call",
                ["name"] = "request_user_input",
                ["arguments"] = args,
                ["call_id"] = "c2",
            },
        }.ToJsonString();

        var q = CodexRollout.ParseQuestion(line, "t2", null, DateTimeOffset.UtcNow);

        Assert.NotNull(q);
        var only = Assert.Single(q!.Questions);
        Assert.Equal("Color?", only.Question);
        Assert.Equal("Pick", only.Header);
        Assert.Equal(new[] { "Red", "Blue" }, only.Options);
        Assert.True(only.MultiSelect);
    }

    [Fact]
    public void LastComponent_BothSeparators_TrailingIgnored()
    {
        Assert.Equal("app", Providers.LastComponent(@"C:\Users\hung\Projects\app\"));
        Assert.Equal("app", Providers.LastComponent("/Users/dev/app"));
        Assert.Equal(string.Empty, Providers.LastComponent(string.Empty));
    }

    // ---- CodexScanner

    [Fact]
    public void CodexScanner_RunningTaskIsJob_ChildThreadsAreNot()
    {
        AsksFixtures.InitEnglish();
        var root = Path.Combine(Temp(), "sessions");
        var now = DateTimeOffset.UtcNow;
        var started = new JsonObject
        {
            ["timestamp"] = Ts(now.AddSeconds(-30)),
            ["type"] = "event_msg",
            ["payload"] = new JsonObject { ["type"] = "task_started", ["turn_id"] = "t" },
        }.ToJsonString();
        // today's date folder: the scan does not enter a day folder that is older than the job window
        Rollout(root, Path.Combine(Day(now), "rollout-a.jsonl"), @"C:\Users\hung\Projects\notchtasks", "sa", new[] { started }, now);
        Rollout(root, Path.Combine(Day(now), "rollout-child.jsonl"), @"C:\Users\hung\Projects\other", "sc",
            new[] { """{"parent_thread_id":"sa"}""", started }, now);

        var scanner = new CodexScanner(root);
        var jobs = scanner.Jobs(now);

        var job = Assert.Single(jobs);
        Assert.Equal("notchtasks", job.Title);
        Assert.True(job.Working);
        Assert.Equal("rollout-a.jsonl", job.Id);
        Assert.Equal("Running · 30 sec", job.Detail);
    }

    [Fact]
    public void CodexScanner_FinishedTasksShow10Min_IdleRolloutsLeave20Min()
    {
        AsksFixtures.InitEnglish();
        var root = Path.Combine(Temp(), "sessions");
        var now = DateTimeOffset.UtcNow;
        var done = new JsonObject
        {
            ["timestamp"] = Ts(now.AddSeconds(-45)),
            ["type"] = "event_msg",
            ["payload"] = new JsonObject { ["type"] = "task_complete", ["turn_id"] = "t" },
        }.ToJsonString();
        Rollout(root, "rollout-done.jsonl", "/x/app", "sd", new[] { done }, now);

        var scanner = new CodexScanner(root);
        var jobs = scanner.Jobs(now);

        var job = Assert.Single(jobs);
        Assert.False(job.Working);
        Assert.Equal("Done · 45 sec ago", job.Detail);
        Assert.Empty(scanner.Jobs(now.AddMinutes(25)));
    }

    [Fact]
    public void CodexScanner_NewestLimitsAcrossRollouts_AndOpenQuestion()
    {
        AsksFixtures.InitEnglish();
        var root = Path.Combine(Temp(), "sessions");
        var now = DateTimeOffset.UtcNow;
        var sec = now.ToUnixTimeSeconds();
        string Limits(double pct, DateTimeOffset at) => new JsonObject
        {
            ["timestamp"] = Ts(at),
            ["type"] = "event_msg",
            ["payload"] = new JsonObject
            {
                ["rate_limits"] = new JsonObject
                {
                    ["primary"] = new JsonObject { ["used_percent"] = pct, ["window_minutes"] = 300, ["resets_at"] = sec + 3600 },
                },
            },
        }.ToJsonString();
        Rollout(root, "old.jsonl", "/a", "o", new[] { Limits(10, now.AddHours(-1)) }, now);
        Rollout(root, "new.jsonl", "/b", "n", new[] { Limits(55, now.AddSeconds(-60)) }, now);
        var q = new JsonObject
        {
            ["timestamp"] = Ts(now),
            ["type"] = "response_item",
            ["payload"] = new JsonObject
            {
                ["type"] = "function_call",
                ["name"] = "request_user_input",
                ["arguments"] = new JsonObject { ["questions"] = new JsonArray(new JsonObject { ["title"] = "Go?", ["options"] = new JsonArray(JsonValue.Create("Yes")) }) }.ToJsonString(),
                ["call_id"] = "q",
            },
        }.ToJsonString();
        Rollout(root, "q.jsonl", "/q/thread", "qq", new[] { q }, now);

        var scanner = new CodexScanner(root);
        var limits = scanner.Limits(now);

        Assert.NotNull(limits);
        Assert.Equal("Codex", limits!.Name);
        Assert.Equal("codex", limits.Kind);
        Assert.Equal(55, limits.Windows[0].Used);

        scanner.Jobs(now);
        var open = Assert.Single(scanner.Questions(now));
        Assert.Equal("Go?", open.Questions[0].Question);
        Assert.Equal("thread", open.Session);
        Assert.Equal("request_user_input", open.Tool);
        Assert.Equal("codex", open.Agent);
    }

    [Fact]
    public void Rollout_HalfWrittenLastLine_IsSkipped()
    {
        AsksFixtures.InitEnglish();
        var root = Path.Combine(Temp(), "sessions");
        var now = DateTimeOffset.UtcNow;
        var file = Path.Combine(root, "rollout-half.jsonl");
        var started = new JsonObject
        {
            ["timestamp"] = Ts(now.AddSeconds(-30)),
            ["type"] = "event_msg",
            ["payload"] = new JsonObject { ["type"] = "task_started", ["turn_id"] = "t" },
        }.ToJsonString();
        Rollout(root, "rollout-half.jsonl", "/x/half", "h", new[] { started }, now);
        // a write in progress: the task_complete line has no newline yet
        var partial = $"{{\"timestamp\":\"{Ts(now.AddSeconds(-5))}\",\"type\":\"event_msg\",\"payload\":{{\"type\":\"task_complete\",\"turn_id\":\"t\"";
        File.AppendAllText(file, partial);
        File.SetLastWriteTimeUtc(file, now.UtcDateTime);

        var scanner = new CodexScanner(root);
        var first = Assert.Single(scanner.Jobs(now));
        Assert.True(first.Working, "the half-written complete line is not read");

        // the write finishes: the line is read now, from where the last full line ended
        File.AppendAllText(file, "}}\n");
        File.SetLastWriteTimeUtc(file, now.AddSeconds(1).UtcDateTime);
        var second = Assert.Single(scanner.Jobs(now.AddSeconds(1)));
        Assert.False(second.Working);
    }

    private static string StartedLine(DateTimeOffset at) => new JsonObject
    {
        ["timestamp"] = Ts(at),
        ["type"] = "event_msg",
        ["payload"] = new JsonObject { ["type"] = "task_started", ["turn_id"] = "t" },
    }.ToJsonString();

    private static string DoneLine(DateTimeOffset at) => new JsonObject
    {
        ["timestamp"] = Ts(at),
        ["type"] = "event_msg",
        ["payload"] = new JsonObject { ["type"] = "task_complete", ["turn_id"] = "t" },
    }.ToJsonString();

    /// A line of about 1 KB that no parser matches (an assistant message).
    private static string FillerLine(DateTimeOffset at) =>
        $"{{\"timestamp\":\"{Ts(at)}\",\"type\":\"response_item\",\"payload\":{{\"type\":\"message\",\"role\":\"assistant\",\"content\":\"{new string('x', 900)}\"}}}}";

    private static string QuestionLine(DateTimeOffset at, string callId, string title) => new JsonObject
    {
        ["timestamp"] = Ts(at),
        ["type"] = "response_item",
        ["payload"] = new JsonObject
        {
            ["type"] = "function_call",
            ["name"] = "request_user_input",
            ["arguments"] = new JsonObject { ["questions"] = new JsonArray(new JsonObject { ["title"] = title, ["options"] = new JsonArray(JsonValue.Create("Yes")) }) }.ToJsonString(),
            ["call_id"] = callId,
        },
    }.ToJsonString();

    [Fact]
    public void Rollout_FirstMB_OfFiveMB_StatusRight_ReadsBounded()
    {
        AsksFixtures.InitEnglish();
        var root = Path.Combine(Temp(), "sessions");
        var now = DateTimeOffset.UtcNow;
        var lines = new List<string> { StartedLine(now.AddSeconds(-30)) };
        lines.AddRange(Enumerable.Range(0, 5200).Select(_ => FillerLine(now.AddSeconds(-20))));
        var file = Rollout(root, "rollout-big.jsonl", "/x/big", "big", lines, now);
        var size = new FileInfo(file).Length;
        Assert.True(size > 5L * 1024 * 1024, $"the file is {size} bytes");

        var reads = new List<int>();
        var scanner = new CodexScanner(root, reads.Add);
        var job = Assert.Single(scanner.Jobs(now));

        Assert.True(job.Working, "the task_started in the first MB is still the last status");
        Assert.Equal("Running · 30 sec", job.Detail);
        Assert.All(reads, n => Assert.True(n <= CodexScanner.ChunkBytes, $"a read of {n} bytes"));
        Assert.Equal(size, reads.Sum(n => (long)n));
    }

    [Fact]
    public void Rollout_MoreThan1MBAppendedBetweenScans_TaskCompleteInsideIsCaught()
    {
        AsksFixtures.InitEnglish();
        var root = Path.Combine(Temp(), "sessions");
        var now = DateTimeOffset.UtcNow;
        var file = Rollout(root, "rollout-grow.jsonl", "/x/grow", "g", new[] { StartedLine(now.AddSeconds(-120)) }, now.AddSeconds(-100));

        var scanner = new CodexScanner(root);
        Assert.True(Assert.Single(scanner.Jobs(now.AddSeconds(-100))).Working);

        // one turn writes 1.5 MB, and its task_complete comes last
        var more = Enumerable.Range(0, 1500).Select(_ => FillerLine(now.AddSeconds(-10))).ToList();
        more.Add(DoneLine(now.AddSeconds(-5)));
        File.AppendAllText(file, string.Join("\n", more) + "\n");
        File.SetLastWriteTimeUtc(file, now.UtcDateTime);

        var done = Assert.Single(scanner.Jobs(now));
        Assert.False(done.Working);
        Assert.Equal("Done · 5 sec ago", done.Detail);
    }

    [Fact]
    public void Rollout_Empty_NoJobsNoLimitsNoQuestions()
    {
        AsksFixtures.InitEnglish();
        var root = Path.Combine(Temp(), "sessions");
        var now = DateTimeOffset.UtcNow;
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "rollout-empty.jsonl");
        File.WriteAllText(file, string.Empty);
        File.SetLastWriteTimeUtc(file, now.UtcDateTime);

        var scanner = new CodexScanner(root);

        Assert.Empty(scanner.Jobs(now));
        Assert.Null(scanner.Limits(now));
        Assert.Empty(scanner.Questions(now));
    }

    [Fact]
    public void Rollout_BadJsonLine_IsSkipped_ScanGoesOn()
    {
        AsksFixtures.InitEnglish();
        var root = Path.Combine(Temp(), "sessions");
        var now = DateTimeOffset.UtcNow;
        Rollout(root, "rollout-bad.jsonl", "/x/bad", "b",
            new[] { StartedLine(now.AddSeconds(-30)), "this is not json {", "{\"payload\":{\"rate_limits\":{", DoneLine(now.AddSeconds(-5)) },
            now);

        var scanner = new CodexScanner(root);

        var job = Assert.Single(scanner.Jobs(now));
        Assert.False(job.Working);
        Assert.Null(CodexRollout.ParseLimits("{\"payload\":{\"rate_limits\":{\n", now));
        Assert.Null(scanner.Limits(now));
    }

    [Fact]
    public void Rollout_ReplacedAndShorter_OffsetResetsToStart()
    {
        AsksFixtures.InitEnglish();
        var root = Path.Combine(Temp(), "sessions");
        var now = DateTimeOffset.UtcNow;
        var file = Rollout(root, "rollout-r.jsonl", "/x/r", "r",
            new[] { StartedLine(now.AddSeconds(-30)), DoneLine(now.AddSeconds(-5)) }, now.AddSeconds(-1));
        var oldSize = new FileInfo(file).Length;

        var scanner = new CodexScanner(root);
        Assert.False(Assert.Single(scanner.Jobs(now)).Working);

        // the file is replaced by a shorter one: its saved offset is past the end, so it is read from the start
        Rollout(root, "rollout-r.jsonl", "/x/r", "r", new[] { StartedLine(now.AddSeconds(-2)) }, now.AddSeconds(1));
        Assert.True(new FileInfo(file).Length < oldSize);

        var job = Assert.Single(scanner.Jobs(now.AddSeconds(1)));
        Assert.True(job.Working);
    }

    [Fact]
    public void CodexAsk_IdToolAndTitle_AsTheMacMakesThem()
    {
        AsksFixtures.InitEnglish();
        var root = Path.Combine(Temp(), "sessions");
        var now = DateTimeOffset.UtcNow;
        Rollout(root, "q.jsonl", "/q/thread", "qq", new[] { QuestionLine(now, "q", "Go?") }, now);
        Rollout(root, "g.jsonl", @"C:\Users\hung\g-p-8f3a21", "gg", new[] { QuestionLine(now, "q2", new string('w', 700)) }, now);

        var scanner = new CodexScanner(root);
        scanner.Jobs(now);
        var asks = scanner.Questions(now);

        Assert.Equal(2, asks.Count);
        var plain = Assert.Single(asks, a => a.Id == "codex|qq|q");
        Assert.Equal("thread", plain.Session);
        Assert.Equal("request_user_input", plain.Tool);
        Assert.Equal("Go?", plain.Summary);
        Assert.Equal("codex", plain.Agent);
        Assert.Equal(0, plain.Pid);
        // a g-p- project folder is the desktop app's own: shown as "Codex", and the full question is kept
        var desktop = Assert.Single(asks, a => a.Id == "codex|gg|q2");
        Assert.Equal("Codex", desktop.Session);
        Assert.Equal(700, desktop.Summary.Length);
    }

    private static string Day(DateTimeOffset t) =>
        Path.Combine(t.UtcDateTime.ToString("yyyy", CultureInfo.InvariantCulture), t.UtcDateTime.ToString("MM", CultureInfo.InvariantCulture),
            t.UtcDateTime.ToString("dd", CultureInfo.InvariantCulture));

    [Fact]
    public void CodexScanner_Jobs_DoesNotEnterDateFoldersBeforeTheWindow()
    {
        AsksFixtures.InitEnglish();
        var root = Path.Combine(Temp(), "sessions");
        var now = DateTimeOffset.UtcNow;
        var old = now.AddDays(-3);
        var started = new JsonObject
        {
            ["timestamp"] = Ts(now.AddSeconds(-30)),
            ["type"] = "event_msg",
            ["payload"] = new JsonObject { ["type"] = "task_started", ["turn_id"] = "t" },
        }.ToJsonString();
        Rollout(root, Path.Combine(Day(now), "rollout-now.jsonl"), @"C:\a\now", "s1", new[] { started }, now);
        Rollout(root, Path.Combine(Day(old), "rollout-old.jsonl"), @"C:\a\old", "s2", new[] { started }, old);

        var entered = new List<string>();
        var scanner = new CodexScanner(root, onFolder: entered.Add);
        var job = Assert.Single(scanner.Jobs(now));

        Assert.Equal("rollout-now.jsonl", job.Id);
        Assert.Contains(entered, d => d.EndsWith(Day(now), StringComparison.Ordinal));
        Assert.DoesNotContain(entered, d => d.EndsWith(Day(old), StringComparison.Ordinal));
    }

    [Fact]
    public void CodexScanner_Limits_DoesNotEnterDateFoldersBeforeSevenDays_AndReadsNothingOnAnUnchangedTree()
    {
        AsksFixtures.InitEnglish();
        var root = Path.Combine(Temp(), "sessions");
        var now = DateTimeOffset.UtcNow;
        var sec = now.ToUnixTimeSeconds();
        string Limits(double pct, DateTimeOffset at) => new JsonObject
        {
            ["timestamp"] = Ts(at),
            ["type"] = "event_msg",
            ["payload"] = new JsonObject
            {
                ["rate_limits"] = new JsonObject
                {
                    ["primary"] = new JsonObject { ["used_percent"] = pct, ["window_minutes"] = 300, ["resets_at"] = sec + 3600 },
                },
            },
        }.ToJsonString();
        var ancient = now.AddDays(-10);
        Rollout(root, Path.Combine(Day(now), "new.jsonl"), "/b", "n", new[] { Limits(55, now.AddSeconds(-60)) }, now);
        Rollout(root, Path.Combine(Day(ancient), "ancient.jsonl"), "/c", "c", new[] { Limits(90, ancient) }, ancient);

        var entered = new List<string>();
        var reads = new List<int>();
        var scanner = new CodexScanner(root, reads.Add, entered.Add);
        Assert.Equal(55, scanner.Limits(now)!.Windows[0].Used);
        Assert.DoesNotContain(entered, d => d.EndsWith(Day(ancient), StringComparison.Ordinal));

        // an unchanged tree is not read again
        Assert.True(reads.Sum() > 0);
        reads.Clear();
        Assert.Equal(55, scanner.Limits(now)!.Windows[0].Used);
        Assert.Empty(reads);

        // a changed file is read again
        Rollout(root, Path.Combine(Day(now), "new.jsonl"), "/b", "n", new[] { Limits(70, now.AddSeconds(-10)) }, now.AddSeconds(1));
        Assert.Equal(70, scanner.Limits(now.AddSeconds(2))!.Windows[0].Used);
        Assert.NotEmpty(reads);
    }

    [Fact]
    public void CodexScanner_Limits_CachedReading_ResetStillAppliesAtTheTimeAsked()
    {
        AsksFixtures.InitEnglish();
        var root = Path.Combine(Temp(), "sessions");
        var now = DateTimeOffset.UtcNow;
        var line = new JsonObject
        {
            ["timestamp"] = Ts(now.AddSeconds(-5)),
            ["type"] = "event_msg",
            ["payload"] = new JsonObject
            {
                ["rate_limits"] = new JsonObject
                {
                    ["primary"] = new JsonObject
                    {
                        ["used_percent"] = 60.0,
                        ["window_minutes"] = 300,
                        ["resets_at"] = now.ToUnixTimeSeconds() + 3600,
                    },
                },
            },
        }.ToJsonString();
        Rollout(root, Path.Combine(Day(now), "r.jsonl"), "/r", "r", new[] { line }, now);

        var scanner = new CodexScanner(root);
        Assert.Equal(60, scanner.Limits(now)!.Windows[0].Used);
        // the reset has passed by then: nothing is used, although the file is cached
        Assert.Equal(0, scanner.Limits(now.AddHours(2))!.Windows[0].Used);
    }
}
