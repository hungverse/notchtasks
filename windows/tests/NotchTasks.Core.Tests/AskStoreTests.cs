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
        var store = new AskStore(Path.Combine(Temp(), "Asks"));
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
        var store = new AskStore(Path.Combine(Temp(), "Asks"));
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
        var store = new AskStore(Path.Combine(Temp(), "Asks"));
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
        var store = new AskStore(Path.Combine(Temp(), "Asks"));
        store.Write(Ask("gone", Environment.ProcessId, DateTimeOffset.UtcNow));
        store.Reply("gone", new AskReply(ReplyKinds.Deny, null));

        store.Remove("gone");

        Assert.False(File.Exists(store.AskPath("gone")));
        Assert.False(File.Exists(store.ReplyPath("gone")));
    }

    [Fact]
    public void MarkAppRunning_AndAppRunning_LiveOtherPidOnly()
    {
        var store = new AskStore(Path.Combine(Temp(), "NotchTasks", "Asks"));
        Assert.False(store.AppRunning(Environment.ProcessId), "no pid file");

        using var child = Process.Start(new ProcessStartInfo("ping.exe", "-n 60 127.0.0.1")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        try
        {
            store.MarkAppRunning(child.Id);
            Assert.True(store.AppRunning(Environment.ProcessId));
            store.MarkAppRunning(Environment.ProcessId);
            Assert.False(store.AppRunning(Environment.ProcessId), "this process does not count as the app");
        }
        finally
        {
            child.Kill();
        }
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
        var store = new AskStore(Path.Combine(Temp(), "Asks"));
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
    public async Task AskWatcher_CallsBackOnlyWhenTheListChanges()
    {
        var store = new AskStore(Path.Combine(Temp(), "Asks"));
        var calls = 0;
        using var watcher = new AskWatcher(store, _ => Interlocked.Increment(ref calls), intervalMs: 50);
        store.Write(Ask("one", Environment.ProcessId, DateTimeOffset.UtcNow));
        Assert.True(await WaitFor(() => Volatile.Read(ref calls) >= 2, 5000));
        var settled = Volatile.Read(ref calls);

        await Task.Delay(600); // several periodic scans see the same list

        Assert.Equal(settled, Volatile.Read(ref calls));
    }
}
