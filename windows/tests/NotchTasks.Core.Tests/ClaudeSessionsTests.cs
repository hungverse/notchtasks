using NotchTasks.Core;
using NotchTasks.Core.Agents;
using Xunit;

namespace NotchTasks.Core.Tests;

// Ported from electron/test/providers.test.js: claudeSessions and claudeDir, plus the job rules of main.js claudeJobs.
// Every test uses a temp folder and a fake process table; nothing touches ~/.claude or the real process list.
[Collection("L10n")]
public sealed class ClaudeSessionsTests : IDisposable
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

    [Fact]
    public void ClaudeDir_ConfigDirWins_ElseHomeDotClaude()
    {
        Assert.Equal(Path.Combine("/h", ".claude"), ClaudeSessions.Dir("/h", null));
        Assert.Equal(Path.Combine("/h", ".claude"), ClaudeSessions.Dir("/h", ""));
        Assert.Equal("/cfg", ClaudeSessions.Dir("/h", "/cfg"));
    }

    [Fact]
    public void ClaudeSessions_RegistryRead_SkipsEntriesWithoutPid()
    {
        var dir = Temp();
        Directory.CreateDirectory(Path.Combine(dir, "sessions"));
        File.WriteAllText(Path.Combine(dir, "sessions", "4321.json"),
            """{"pid":4321,"sessionId":"abc","cwd":"C:\\x\\app","name":"app work","status":"busy","statusUpdatedAt":1}""");
        File.WriteAllText(Path.Combine(dir, "sessions", "bad.json"), """{"sessionId":"nope"}""");

        var list = ClaudeSessions.Read(dir);

        var only = Assert.Single(list);
        Assert.Equal(4321, only.Pid);
        Assert.Equal("app work", only.Name);
        Assert.Equal("busy", only.Status);
        Assert.Equal(1L, only.StatusUpdatedAt);
    }

    [Fact]
    public void ClaudeSessions_RegistryFileHeldOpenByItsWriter_IsRead()
    {
        var dir = Temp();
        Directory.CreateDirectory(Path.Combine(dir, "sessions"));
        // Claude keeps the session file open for writing while it works
        using var writer = new FileStream(Path.Combine(dir, "sessions", "77.json"), FileMode.Create, FileAccess.Write, FileShare.Read);
        writer.Write(System.Text.Encoding.UTF8.GetBytes("""{"pid":77,"cwd":"C:\\x\\app","status":"busy","statusUpdatedAt":1}"""));
        writer.Flush();

        var only = Assert.Single(ClaudeSessions.Read(dir));

        Assert.Equal(77, only.Pid);
        Assert.Equal("busy", only.Status);
    }

    [Fact]
    public void Sessions_BadJson_Skipped()
    {
        var dir = Temp();
        Directory.CreateDirectory(Path.Combine(dir, "sessions"));
        File.WriteAllText(Path.Combine(dir, "sessions", "broken.json"), "{\"pid\":77,\"name\":\"\"");
        File.WriteAllText(Path.Combine(dir, "sessions", "good.json"), """{"pid":88,"status":"idle"}""");

        var list = ClaudeSessions.Read(dir);

        var only = Assert.Single(list);
        Assert.Equal(88, only.Pid);
    }

    [Fact]
    public void ClaudeJobs_KeepOnlyLiveClaudeProcesses_TitleAndDetail()
    {
        L10nInit();
        const long now = 1_000_000_000;
        var sessions = new List<ClaudeSession>
        {
            new(4321, "app work", @"C:\x\app", "busy", now - 30_000),
            new(5555, null, @"C:\x\other", "idle", now - 5_000),
            new(6666, null, null, "idle", null),
            new(8888, "node work", @"C:\x\node", "busy", now),
            new(7777, "gone", null, "busy", now),
        };
        var procs = new ProcSnapshot(new[]
        {
            new ProcInfo(4321, 1, "claude.exe", 0, 0),
            new ProcInfo(5555, 1, @"C:\Program Files\Claude\CLAUDE.EXE", 0, 0),
            new ProcInfo(6666, 1, "claude", 0, 0),
            new ProcInfo(8888, 1, "node.exe", 0, 0),
        });

        var jobs = ClaudeSessions.Jobs(sessions, procs, now);

        Assert.Equal(3, jobs.Count);
        Assert.Equal(new Job("4321", "app work", "Working · 30 sec", true), jobs[0]);
        Assert.Equal(new Job("5555", "other", "Waiting", false), jobs[1]);
        Assert.Equal(new Job("6666", "Claude", "Waiting", false), jobs[2]);
    }

    private static void L10nInit() => AsksFixtures.InitEnglish();
}
