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
        Rollout(root, Path.Combine("2026", "11", "14", "rollout-a.jsonl"), @"C:\Users\hung\Projects\notchtasks", "sa", new[] { started }, now);
        Rollout(root, Path.Combine("2026", "11", "14", "rollout-child.jsonl"), @"C:\Users\hung\Projects\other", "sc",
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

    [Fact]
    public void Rollout_50MB_ReadsTailOnly()
    {
        AsksFixtures.InitEnglish();
        var root = Path.Combine(Temp(), "sessions");
        var now = DateTimeOffset.UtcNow;
        var file = Path.Combine(root, "rollout-big.jsonl");
        Directory.CreateDirectory(root);
        var filler = new string('x', 900);
        var fillerLine = $"{{\"timestamp\":\"{Ts(now.AddSeconds(-20))}\",\"type\":\"response_item\",\"payload\":{{\"type\":\"message\",\"role\":\"assistant\",\"content\":\"{filler}\"}}}}";
        var done = new JsonObject
        {
            ["timestamp"] = Ts(now.AddSeconds(-5)),
            ["type"] = "event_msg",
            ["payload"] = new JsonObject { ["type"] = "task_complete", ["turn_id"] = "t" },
        }.ToJsonString();
        var head = new JsonObject
        {
            ["timestamp"] = Ts(now),
            ["type"] = "session_meta",
            ["payload"] = new JsonObject { ["id"] = "big", ["cwd"] = "/x/big", ["session_id"] = "big" },
        }.ToJsonString();
        using (var writer = new StreamWriter(file, false))
        {
            writer.Write(head);
            writer.Write('\n');
            for (var i = 0; i < 50_000; i++)
            {
                writer.Write(fillerLine);
                writer.Write('\n');
            }
            writer.Write(done);
            writer.Write('\n');
        }
        File.SetLastWriteTimeUtc(file, now.UtcDateTime);
        Assert.True(new FileInfo(file).Length > 50_000_000);

        var scanner = new CodexScanner(root);
        var watch = Stopwatch.StartNew();
        var jobs = scanner.Jobs(now);
        watch.Stop();

        var job = Assert.Single(jobs);
        Assert.False(job.Working);
        Assert.True(watch.ElapsedMilliseconds < 200, $"scan took {watch.ElapsedMilliseconds} ms");
    }
}
