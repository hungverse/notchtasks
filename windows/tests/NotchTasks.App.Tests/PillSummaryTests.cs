using NotchTasks.Core.Agents;
using NotchTasks.Core.Media;
using NotchTasks.Core.Notch;
using NotchTasks.Pill;
using Xunit;

namespace NotchTasks.AppTests;

/// The open pill's header line, as the Mac builds it (NotchView.swift summary): "N running", "N waiting" from the idle jobs, and
/// "♪ app" while something plays, joined with " · ".
[Collection("L10n")]
public sealed class PillSummaryTests
{
    private static readonly WatchItem Item = new("claude", "Claude", WatchKind.Process);

    private static NotchSnapshot Snap(IReadOnlyList<ItemState> running, int jobCount, int askCount = 0, NowPlaying? media = null) =>
        new(Array.Empty<string>(), askCount, null, null, new SensorState(Array.Empty<string>(), false, false), null, null)
        {
            Running = running,
            JobCount = jobCount,
            Media = media,
        };

    private static ItemState Working(params string[] ids) =>
        new(Item, ids.Select(id => new Job(id, "job", "detail", Working: true)).ToList());

    private static NowPlaying Playing(string app, bool playing = true) => new(
        "source", app, null, "Song", "Artist", playing, 0, 200, 0, true, true, true, true, null);

    [Fact]
    public void NothingRunning_WhenNoJobsAndNothingPlays()
    {
        Assert.Equal("Nothing running", PillSummary.For(Snap(Array.Empty<ItemState>(), jobCount: 0)));
    }

    [Fact]
    public void Running_CountsTheWorkingJobs()
    {
        var running = new[] { Working("a"), Working("b", "c") };  // three working jobs across two agents

        Assert.Equal("3 running", PillSummary.For(Snap(running, jobCount: 3)));
    }

    [Fact]
    public void Waiting_IsTheIdleJobs_TotalMinusWorking()
    {
        // Four jobs: one working, three idle. Mac: "N waiting" is total minus working, not the pending approvals.
        var running = new[] { Working("a") };

        Assert.Equal("1 running · 3 waiting", PillSummary.For(Snap(running, jobCount: 4)));
    }

    [Fact]
    public void Waiting_DoesNotCountPendingAsks()
    {
        // The Mac's summary never counts asks: with no jobs there is nothing to wait on, however many asks are pending.
        Assert.Equal("Nothing running", PillSummary.For(Snap(Array.Empty<ItemState>(), jobCount: 0, askCount: 2)));
    }

    [Fact]
    public void Playing_ShowsTheAppName_WithANoteMark()
    {
        Assert.Equal("♪ Spotify", PillSummary.For(Snap(Array.Empty<ItemState>(), jobCount: 0, media: Playing("Spotify"))));
    }

    [Fact]
    public void Playing_IsLastAfterRunningAndWaiting()
    {
        var running = new[] { Working("a") };

        Assert.Equal("1 running · 2 waiting · ♪ Spotify", PillSummary.For(Snap(running, jobCount: 3, media: Playing("Spotify"))));
    }

    [Fact]
    public void Paused_MediaIsNotShown()
    {
        Assert.Equal("Nothing running", PillSummary.For(Snap(Array.Empty<ItemState>(), jobCount: 0, media: Playing("Spotify", playing: false))));
    }
}
