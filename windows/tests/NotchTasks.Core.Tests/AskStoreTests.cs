using System.Diagnostics;
using System.Text.Json.Nodes;
using NotchTasks.Core.Asks;
using Xunit;

namespace NotchTasks.Core.Tests;

// Ported from electron/test/asks.test.js: 'pending: drops asks...', 'watchAsks: reports new asks...',
// 'markAppRunning / appRunning...', plus the Windows pieces (pid checks, ask file shape, empty-path reads).
[Collection("L10n")]
public sealed class AskStoreTests : IDisposable
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
                // A file still open elsewhere; the temp cleaner takes it later.
            }
        }
    }

    private string Temp()
    {
        var dir = AsksFixtures.NewTempDir();
        _dirs.Add(dir);
        return dir;
    }

    /// Process checks from a fixed table (pid, program file name), so the tests do not depend on what else runs here.
    private static Func<int, string[], bool> Programs(params (int Pid, string Name)[] table) =>
        (pid, names) => table.Any(t => t.Pid == pid && names.Contains(t.Name, StringComparer.OrdinalIgnoreCase));

    /// A store whose only live process is this test run, as the NotchTasks hook.
    private static AskStore NewStore(string dir) =>
        new(dir, Programs((Environment.ProcessId, "NotchTasksHook.exe")));

    private static AgentAsk Ask(string id, int pid, DateTimeOffset at) =>
        new(id, pid, "notchtasks", "Bash", "rm -rf dist", new List<AskQuestion>(), at, "claude");

    /// The pid of a process that has already exited.
    private static int DeadPid()
    {
        using var p = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        p.WaitForExit();
        return p.Id;
    }

    private static async Task<bool> WaitFor(Func<bool> condition, int milliseconds)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < milliseconds)
        {
            if (condition())
            {
                return true;
            }
            await Task.Delay(25);
        }
        return condition();
    }

    [Fact]
    public void Pending_DropsDeadPidAndExpired_SortsOldestFirst()
    {
        var store = NewStore(Path.Combine(Temp(), "Asks"));
        var now = DateTimeOffset.UtcNow;
        var me = Environment.ProcessId;
        store.Write(Ask("newer", me, now.AddSeconds(-1)));
        store.Write(Ask("older", me, now.AddSeconds(-5)));
        store.Write(Ask("dead", DeadPid(), now));
        store.Write(Ask("old", me, now.AddSeconds(-591)));
        store.Write(Ask("answered", me, now));
        store.Reply("answered", new AskReply(ReplyKinds.Allow, null));

        var ids = store.Pending(now).Select(a => a.Id).ToList();

        Assert.Equal(new[] { "older", "newer" }, ids);
        Assert.False(File.Exists(store.AskPath("dead")));
        Assert.False(File.Exists(store.AskPath("old")));
        Assert.True(File.Exists(store.AskPath("answered")), "answered: left for the hook to pick up");
    }

    [Fact]
    public void Write_FileHoldsTheAsksJsonShape()
    {
        var store = NewStore(Path.Combine(Temp(), "Asks"));
        var at = new DateTimeOffset(2026, 11, 16, 9, 30, 15, 42, TimeSpan.Zero);
        var questions = new List<AskQuestion> { new("Which parts?", null, new List<string> { "Features", "Fixes" }, true) };
        store.Write(new AgentAsk("abc", 1234, "notchtasks", "AskUserQuestion", "Which parts?", questions, at, "claude"));

        var json = (JsonObject)JsonNode.Parse(File.ReadAllText(store.AskPath("abc")))!;

        Assert.Equal("abc", json["id"]!.GetValue<string>());
        Assert.Equal(1234, json["pid"]!.GetValue<int>());
        Assert.Equal("notchtasks", json["session"]!.GetValue<string>());
        Assert.Equal("2026-11-16T09:30:15.042Z", json["at"]!.GetValue<string>());
        Assert.Equal("claude", json["agent"]!.GetValue<string>());
        var q = (JsonObject)json["questions"]![0]!;
        Assert.Equal("Which parts?", q["question"]!.GetValue<string>());
        Assert.False(q.ContainsKey("header"), "a missing header is left out, as JSON.stringify does");
        Assert.True(q["multiSelect"]!.GetValue<bool>());
        Assert.Equal(new[] { "Features", "Fixes" }, q["options"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public void ReadReply_AcceptsOnlyTheFourKinds()
    {
        var store = NewStore(Path.Combine(Temp(), "Asks"));
        store.Reply("r", new AskReply(ReplyKinds.Answers, new Dictionary<string, string> { ["Proceed?"] = "Yes" }));

        var reply = store.ReadReply("r");

        Assert.Equal(ReplyKinds.Answers, reply!.Kind);
        Assert.Equal("Yes", reply.Answers!["Proceed?"]);
        File.WriteAllText(store.ReplyPath("bad"), "{\"kind\":\"maybe\"}");
        Assert.Null(store.ReadReply("bad"));
        Assert.Null(store.ReadReply("missing"));
    }

    [Fact]
    public void Remove_DeletesTheAskAndItsReply()
    {
        var store = NewStore(Path.Combine(Temp(), "Asks"));
        store.Write(Ask("gone", Environment.ProcessId, DateTimeOffset.UtcNow));
        store.Reply("gone", new AskReply(ReplyKinds.Deny, null));

        store.Remove("gone");

        Assert.False(File.Exists(store.AskPath("gone")));
        Assert.False(File.Exists(store.ReplyPath("gone")));
    }

    [Fact]
    public void MarkAppRunning_AndAppRunning_LiveOtherPidOnly()
    {
        var dir = Path.Combine(Temp(), "NotchTasks", "Asks");
        var store = NewStore(dir);
        Assert.False(store.AppRunning(Environment.ProcessId), "no pid file");

        using var child = Process.Start(new ProcessStartInfo("ping.exe", "-n 60 127.0.0.1")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        try
        {
            // The app is the NotchTasks.exe process: a live program with another name is not the app.
            var app = new AskStore(dir, Programs((child.Id, "NotchTasks.exe")));
            app.MarkAppRunning(child.Id);
            Assert.True(app.AppRunning(Environment.ProcessId));
            Assert.False(NewStore(dir).AppRunning(Environment.ProcessId), "a live ping.exe is not NotchTasks.exe");
            store.MarkAppRunning(Environment.ProcessId);
            Assert.False(app.AppRunning(Environment.ProcessId), "this process does not count as the app");
        }
        finally
        {
            child.Kill();
        }
    }

    [Fact]
    public void AppRunning_RealProcess_NeedsTheNotchTasksExeName()
    {
        var dir = Path.Combine(Temp(), "NotchTasks", "Asks");
        using var child = Process.Start(new ProcessStartInfo("ping.exe", "-n 60 127.0.0.1")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        try
        {
            var store = new AskStore(dir);
            store.MarkAppRunning(child.Id);

            Assert.False(store.AppRunning(Environment.ProcessId));
        }
        finally
        {
            child.Kill();
        }
    }

    [Fact]
    public void Pending_AskOwnedByAnotherProgram_IsDropped()
    {
        var store = new AskStore(Path.Combine(Temp(), "Asks"), Programs((101, "NotchTasksHook.exe"), (102, "NotchTasks.exe"), (103, "ping.exe")));
        var now = DateTimeOffset.UtcNow;
        store.Write(Ask("hook", 101, now));
        store.Write(Ask("oldApp", 102, now.AddSeconds(-1)));
        store.Write(Ask("reused", 103, now));

        var ids = store.Pending(now).Select(a => a.Id).ToList();

        Assert.Equal(new[] { "oldApp", "hook" }, ids);
        Assert.False(File.Exists(store.AskPath("reused")), "a reused pid no longer owns the ask");
    }

    [Fact]
    public void Pending_SweepsRepliesAndTempFilesOlderThanTheWait()
    {
        var store = NewStore(Path.Combine(Temp(), "Asks"));
        var now = DateTimeOffset.UtcNow;
        Directory.CreateDirectory(store.Dir);
        var oldReply = store.ReplyPath("orphan");
        var oldTmp = store.AskPath("torn") + ".tmp-0123456789abcdef";
        var freshReply = store.ReplyPath("fresh");
        File.WriteAllText(oldReply, "{\"kind\":\"allow\"}");
        File.WriteAllText(oldTmp, "{");
        File.WriteAllText(freshReply, "{\"kind\":\"allow\"}");
        File.SetLastWriteTimeUtc(oldReply, DateTime.UtcNow.AddSeconds(-600));
        File.SetLastWriteTimeUtc(oldTmp, DateTime.UtcNow.AddSeconds(-600));

        store.Pending(now);

        Assert.False(File.Exists(oldReply));
        Assert.False(File.Exists(oldTmp));
        Assert.True(File.Exists(freshReply), "a reply still inside the wait stays");
    }

    [Fact]
    public void Pending_CapsTextAndListsOfAParsedAsk()
    {
        var store = NewStore(Path.Combine(Temp(), "Asks"));
        var now = DateTimeOffset.UtcNow;
        var questions = Enumerable.Range(0, 12)
            .Select(i => new AskQuestion("Q" + i + new string('q', 2500), new string('h', 2500),
                Enumerable.Range(0, 25).Select(o => "option " + o + new string('o', 2500)).ToList(), false))
            .ToList();
        store.Write(new AgentAsk("big", Environment.ProcessId, new string('s', 2500), "Bash", new string('m', 2500),
            questions, now, new string('a', 2500)));

        var ask = Assert.Single(store.Pending(now));

        Assert.Equal(10, ask.Questions.Count);
        Assert.All(ask.Questions, q => Assert.Equal(20, q.Options.Count));
        Assert.All(ask.Questions, q => Assert.Equal(2000, q.Question.Length));
        Assert.All(ask.Questions, q => Assert.Equal(2000, q.Header!.Length));
        Assert.All(ask.Questions, q => Assert.All(q.Options, o => Assert.Equal(2000, o.Length)));
        Assert.Equal(2000, ask.Summary.Length);
        Assert.Equal(2000, ask.Session.Length);
        Assert.Equal(2000, ask.Agent.Length);
    }

    [Fact]
    public void ReadJsonOrNull_OfAnEmptyPathIsNull()
    {
        Assert.Null(AsksJson.ReadJsonOrNull(""));
        Assert.Null(AsksJson.ReadJsonOrNull("   "));
    }

    [Fact]
    public async Task AskWatcher_ReportsNewAsks_AndDropsOneOnceAnswered()
    {
        var store = NewStore(Path.Combine(Temp(), "Asks"));
        var seen = new List<IReadOnlyList<AgentAsk>>();
        List<IReadOnlyList<AgentAsk>> Snapshot()
        {
            lock (seen)
            {
                return seen.ToList();
            }
        }

        using var watcher = new AskWatcher(store, list =>
        {
            lock (seen)
            {
                seen.Add(list);
            }
        }, intervalMs: 100);
        store.Write(Ask("live", Environment.ProcessId, DateTimeOffset.UtcNow));

        Assert.True(await WaitFor(() => Snapshot().Any(l => l.Any(a => a.Id == "live")), 5000));
        store.Reply("live", new AskReply(ReplyKinds.Allow, null));
        Assert.True(await WaitFor(() => Snapshot()[^1].Count == 0, 5000), "the answered ask leaves the list");
    }

    [Fact]
    public async Task ThrowingOnChange_IsRetried()
    {
        var store = NewStore(Path.Combine(Temp(), "Asks"));
        var calls = 0;
        using var watcher = new AskWatcher(store, _ =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                throw new InvalidOperationException("the first report fails");
            }
        }, intervalMs: 50);

        // The first report threw, so the next scan reports the same (empty) list again, and nothing crashed.
        Assert.True(await WaitFor(() => Volatile.Read(ref calls) >= 2, 5000));
    }

    [Fact]
    public async Task AskWatcher_CallsBackOnlyWhenTheListChanges()
    {
        var store = NewStore(Path.Combine(Temp(), "Asks"));
        var calls = 0;
        using var watcher = new AskWatcher(store, _ => Interlocked.Increment(ref calls), intervalMs: 50);
        store.Write(Ask("one", Environment.ProcessId, DateTimeOffset.UtcNow));
        Assert.True(await WaitFor(() => Volatile.Read(ref calls) >= 2, 5000));
        var settled = Volatile.Read(ref calls);

        await Task.Delay(600); // several periodic scans see the same list

        Assert.Equal(settled, Volatile.Read(ref calls));
    }
}
