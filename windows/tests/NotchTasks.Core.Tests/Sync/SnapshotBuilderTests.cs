using System.Text.Json.Nodes;
using NotchTasks.Core;
using NotchTasks.Core.Asks;
using NotchTasks.Core.Sync;
using Xunit;

namespace NotchTasks.Core.Tests.Sync;

/// Port of the snapshot tests in electron/test/sync.test.js (crewSignal, bare, buildSnapshot, jobOf, record, isPro, proNoteText, healthOf).
[Collection("L10n")]
public sealed class SnapshotBuilderTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeMilliseconds(1794380000000);

    private static SyncRun Run(string id, DateTimeOffset since, string agent = "claude", string title = "Run", string detail = "work", JsonObject? icon = null) =>
        new(id, agent, title, detail, since, icon);

    private static SyncRun Build(string id, string title = "Build", string detail = "clean", JsonObject? icon = null) =>
        new(id, null, title, detail, T0, icon);

    private static SyncInput Input(
        IReadOnlyList<SyncRun>? runs = null,
        IReadOnlyList<SyncRun>? builds = null,
        IReadOnlyList<SyncLimitInput>? limits = null,
        SyncHealthInput? health = null,
        JsonObject? notch = null) =>
        new(runs ?? [], builds ?? [], [], limits ?? [], health, notch);

    private static JsonObject Entitlement(bool pro, double until) => new()
    {
        ["pro"] = pro,
        ["until"] = JsonValue.Create(until),
    };

    [Fact]
    public void CrewSignal_StartStopAtOnce_CountChangeEveryTenMinutes()
    {
        var idle = new CrewSig(0, 0, double.NegativeInfinity);
        var on = SnapshotBuilder.CrewSignal(1, 5, idle, 0);
        Assert.Equal(1, on.Count);

        Assert.Equal(1, SnapshotBuilder.CrewSignal(3, 5, on, 599_000).Count);
        Assert.Equal(3, SnapshotBuilder.CrewSignal(3, 5, on, 600_000).Count);
        Assert.Equal(0, SnapshotBuilder.CrewSignal(0, 0, on, 1000).Count);
    }

    [Fact]
    public void Bare_StripsClockAndEstimate()
    {
        Assert.Equal("clean · NotchTasks", SnapshotBuilder.Bare("clean · NotchTasks · 0:04 · ≈ 40s nữa"));
        Assert.Equal("Đang làm việc", SnapshotBuilder.Bare("Đang làm việc · 9 phút"));
    }

    [Fact]
    public void Build_KeepsFirstSeenSince_AndForgetsGoneRuns()
    {
        var builder = new SnapshotBuilder();
        var t0 = T0;
        var run = Run("R1", DateTimeOffset.MinValue);

        var first = builder.Build(Input(runs: [run]), "PC", t0);
        Assert.Equal(t0, first.Runs[0].Since);

        var still = builder.Build(Input(runs: [run]), "PC", t0.AddSeconds(5));
        Assert.Equal(t0, still.Runs[0].Since);

        builder.Build(Input(), "PC", t0.AddSeconds(6));

        var back = builder.Build(Input(runs: [run]), "PC", t0.AddSeconds(7));
        Assert.Equal(t0.AddSeconds(7), back.Runs[0].Since);
    }

    [Fact]
    public void Build_Job_RunningThenDoneForFiveMinutes()
    {
        var builder = new SnapshotBuilder();
        var t0 = T0;
        var t1 = t0.AddSeconds(10);

        var running = builder.Build(Input(builds: [Build("B1", "Deploy", "queued · 0:04")]), "PC", t0);
        Assert.NotNull(running.Job);
        Assert.Equal("running", running.Job!.State);
        Assert.Equal("queued", running.Job.Detail);

        var done = builder.Build(Input(), "PC", t1);
        Assert.Equal("done", done.Job!.State);
        Assert.Equal(t1, done.Job.Ended);

        Assert.Equal("done", builder.Build(Input(), "PC", t1.AddSeconds(299)).Job!.State);
        Assert.Null(builder.Build(Input(), "PC", t1.AddSeconds(300)).Job);
    }

    [Fact]
    public void Build_Job_FailedWhenCiFailedNearItsEnd()
    {
        var builder = new SnapshotBuilder();
        var t0 = T0;
        var t1 = t0.AddSeconds(10);

        builder.Build(Input(builds: [Build("B1", "Deploy")]), "PC", t0);
        builder.Build(Input(), "PC", t1);
        builder.Record("ciFailed", "Build failed", "", false, t1.AddSeconds(10));

        var job = builder.Build(Input(), "PC", t1.AddSeconds(10)).Job;
        Assert.NotNull(job);
        Assert.Equal("failed", job!.State);
    }

    [Fact]
    public void Build_Crew_OldestRunWithBareDetail_MembersAtMostFour()
    {
        var builder = new SnapshotBuilder();
        var t0 = T0;
        var agents = new[] { "claude", "codex", "gemini", "cursor", "opencode" };
        var runs = agents.Select((a, i) => Run($"R{i}", t0.AddSeconds(i), a, $"Title {i}", $"detail {i} · 0:0{i}")).ToList();

        var snapshot = builder.Build(Input(runs: runs), "PC", t0.AddSeconds(20));

        Assert.NotNull(snapshot.Crew);
        Assert.Equal(5, snapshot.Crew!.Count);
        Assert.Equal("Title 0", snapshot.Crew.Title);
        Assert.Equal("detail 0", snapshot.Crew.Detail);
        Assert.Equal("claude", snapshot.Crew.Agent);
        Assert.Equal(4, snapshot.Crew.Members.Count);
        Assert.Equal(["claude", "codex", "gemini", "cursor"], snapshot.Crew.Members.Select(m => m.Agent));
    }

    [Fact]
    public void Build_Limits_UsedTruncated_ResetsWholeMinute()
    {
        var builder = new SnapshotBuilder();
        var t0 = T0;
        var resets1 = new DateTimeOffset(2026, 11, 25, 10, 0, 31, TimeSpan.Zero);
        var resets2 = new DateTimeOffset(2026, 11, 25, 10, 0, 29, TimeSpan.Zero);

        var snapshot = builder.Build(Input(limits:
        [
            new SyncLimitInput("claude", "5h", 72.9, resets1),
            new SyncLimitInput("codex", "week", 10, resets2),
        ]), "PC", t0);

        Assert.Equal(72, snapshot.Limits[0].Used);
        Assert.Equal(new DateTimeOffset(2026, 11, 25, 10, 1, 0, TimeSpan.Zero), snapshot.Limits[0].Resets);
        Assert.Equal(new DateTimeOffset(2026, 11, 25, 10, 0, 0, TimeSpan.Zero), snapshot.Limits[1].Resets);
    }

    [Fact]
    public void HealthOf_RoundsCpuMemoryToFivePercentSteps()
    {
        var health = SnapshotBuilder.HealthOf(new SyncHealthInput(80, true, 51.6, 0.43, 0.71));

        Assert.NotNull(health);
        Assert.Equal(80, health!.Battery);
        Assert.True(health.Charging);
        Assert.Equal(0, health.Thermal);
        Assert.Equal(52, health.Temp);
        Assert.Null(health.Fan);
        Assert.Equal(45, health.Cpu);
        Assert.Equal(70, health.Memory);

        Assert.Null(SnapshotBuilder.HealthOf(null));
    }

    [Fact]
    public void Record_KeepsOnlyKnownKindsOrToPhone_TwentyWithin24h()
    {
        var builder = new SnapshotBuilder();
        var t0 = T0;

        Assert.Null(builder.Record("taskDone", "Done", "", false, t0));
        Assert.NotNull(builder.Record("ciPassed", "Passed", "", false, t0));
        Assert.NotNull(builder.Record("ciFailed", "Failed", "", false, t0));
        Assert.NotNull(builder.Record("limitWarn", "Limit", "", false, t0));
        Assert.NotNull(builder.Record("lowBattery", "Battery", "", false, t0));
        Assert.Equal(4, builder.Events.Count);

        var kept = builder.Record("taskDone", "Done", "for phone", true, t0);
        Assert.NotNull(kept);
        Assert.True(Guid.TryParse(kept!.Id, out var guid));
        Assert.Equal(guid.ToString().ToUpperInvariant(), kept.Id);

        var b = new SnapshotBuilder();
        for (var i = 0; i < 25; i++)
        {
            b.Record("taskDone", $"Task {i}", "", true, t0.AddSeconds(i));
        }
        Assert.Equal(20, b.Events.Count);
        Assert.Equal("Task 24", b.Events[^1].Title);
        Assert.Equal("Task 5", b.Events[0].Title);

        var c = new SnapshotBuilder();
        c.Record("taskDone", "Old", "", true, t0);
        c.Record("taskDone", "Recent", "", true, t0.AddSeconds(86401));
        Assert.Equal(["Recent"], c.Events.Select(e => e.Title));
    }

    [Fact]
    public void ProNoteText_Moved_Refunded_Expired_ElseNull()
    {
        L10n.Init(AppContext.BaseDirectory, "en");

        Assert.Equal("The group’s Pro moved to another computer", SnapshotBuilder.ProNoteText("moved"));
        Assert.Equal("The group’s Pro plan was refunded", SnapshotBuilder.ProNoteText("refunded"));
        Assert.Equal("The group’s Pro plan has expired", SnapshotBuilder.ProNoteText("expired"));
        Assert.Null(SnapshotBuilder.ProNoteText("other"));
    }

    [Fact]
    public void IsPro_TrueOnlyWhenProTrueAndUntilInFuture()
    {
        Assert.True(SnapshotBuilder.IsPro(Entitlement(true, 2000), 1000));
        Assert.False(SnapshotBuilder.IsPro(Entitlement(true, 1000), 1000));
        Assert.False(SnapshotBuilder.IsPro(Entitlement(false, 2000), 1000));
        Assert.False(SnapshotBuilder.IsPro(new JsonObject { ["pro"] = "true", ["until"] = 2000 }, 1000));
        Assert.False(SnapshotBuilder.IsPro(null, 1000));
    }

    [Fact]
    public void Members_OncePerAgentAndIconOldestFirst()
    {
        var runs = new[]
        {
            Run("R2", T0.AddSeconds(2), "codex"),
            Run("R1", T0.AddSeconds(1), "claude"),
            Run("R3", T0.AddSeconds(3), "codex"),
        };

        var members = SnapshotBuilder.Members(runs);

        Assert.Equal(["claude", "codex"], members.Select(m => m.Agent));
    }
}
