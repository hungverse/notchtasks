using System.Windows.Media;
using NotchTasks.Core;
using NotchTasks.Core.Asks;
using NotchTasks.Core.Media;
using NotchTasks.Core.Settings;
using NotchTasks.Services;
using NotchTasks.Services.Media;
using NotchTasks.Services.Modules;
using Xunit;

namespace NotchTasks.AppTests;

/// A media reader the tests drive: the sessions it lists, a read it can hold open, the commands and seeks it was sent, the
/// artwork it gives, and the Changed subscribers (so a test sees the source unsubscribe).
internal sealed class FakeMediaReader : IMediaReader
{
    private Action? _changed;

    public List<MediaSessionFact> Sessions { get; set; } = new();

    /// Reads taken from here, in order, while any is queued: a read that is held until the test sets its result.
    public Queue<TaskCompletionSource<IReadOnlyList<MediaSessionFact>>> Holds { get; } = new();

    public int Starts { get; private set; }

    public int Stops { get; private set; }

    public int Reads { get; private set; }

    public int Subscribers { get; private set; }

    /// Every FollowTimeline call, in order (true when the pill opened).
    public List<bool> FollowCalls { get; } = new();

    /// When true, every Read raises Changed before it returns, as a busy session does (its events land during each read).
    public bool RaiseDuringRead { get; set; }

    public List<(string Source, MediaCommand Command)> Sent { get; } = new();

    public List<(string Source, double Seconds)> Seeks { get; } = new();

    public List<string> ArtReads { get; } = new();

    public byte[]? Art { get; set; }

    public event Action Changed
    {
        add
        {
            _changed += value;
            Subscribers++;
        }
        remove
        {
            _changed -= value;
            Subscribers--;
        }
    }

    /// Raises Changed, as SMTC does when a session changes.
    public void Raise() => _changed?.Invoke();

    public Task Start()
    {
        Starts++;
        return Task.CompletedTask;
    }

    public void Stop() => Stops++;

    public void FollowTimeline(bool follow) => FollowCalls.Add(follow);

    public Task<IReadOnlyList<MediaSessionFact>> Read()
    {
        Reads++;
        if (RaiseDuringRead)
        {
            Raise();
            Thread.Sleep(300);  // long enough for the 150 ms coalescing timer to request again before this read lands
        }
        if (Holds.Count > 0)
        {
            return Holds.Dequeue().Task;
        }
        return Task.FromResult<IReadOnlyList<MediaSessionFact>>(Sessions.ToList());
    }

    public Task Send(string source, MediaCommand command)
    {
        Sent.Add((source, command));
        return Task.CompletedTask;
    }

    public Task Seek(string source, double seconds)
    {
        Seeks.Add((source, seconds));
        return Task.CompletedTask;
    }

    public Task<byte[]?> Artwork(string source)
    {
        ArtReads.Add(source);
        return Task.FromResult(Art);
    }
}

/// The media source (module "media"): a burst of SMTC events makes one read, a read that a newer request overtakes still applies (only a
/// stop or restart drops a read), the timeline follows the open pill,
/// stopping clears the module and unsubscribes, artwork is fetched once per key, and a toggle flips the card before SMTC answers.
public sealed class MediaSourceTests
{
    /// A 1 x 1 PNG, the artwork bytes the tests give.
    private const string OnePixelPng =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=";

    private static MediaSessionFact Session(string source, MediaStatus status, string? art = "key") => new(
        source, "Song", "Artist", status, 30, 200, 0, true, true, true, true, art, IsCurrent: true);

    /// A source over the reader, with a module and a publish log of the shown source at each publish.
    private static (MediaSource Source, MediaModule Module, List<string?> Published) Make(FakeMediaReader reader,
        RecentCache<ImageSource>? art = null)
    {
        var module = new MediaModule();
        var published = new List<string?>();
        var source = new MediaSource(module, reader, () => published.Add(module.Current?.Source),
            art ?? new RecentCache<ImageSource>(8), () => 1000);
        return (source, module, published);
    }

    private static AppState NewState(FakeMediaReader reader)
    {
        var settings = SettingsStore.Load(AppPaths.SettingsFile);
        return new AppState(settings, new AskStore(AppPaths.AsksDir), Fixture.Inert(settings) with { Media = reader });
    }

    [Fact]
    public void Burst_CoalescedToOneRead()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var reader = new FakeMediaReader { Sessions = { Session("spotify.exe", MediaStatus.Playing) } };
            var (source, module, _) = Make(reader);

            source.Start();
            Fixture.PumpUntil(() => reader.Reads >= 1);
            var before = reader.Reads;

            for (var i = 0; i < 10; i++)
            {
                reader.Raise();
            }
            Fixture.PumpUntil(() => reader.Reads > before);
            Fixture.PumpUntil(() => false, 400);  // a later burst would show here as another read

            Assert.Equal(before + 1, reader.Reads);
            Assert.Equal("spotify.exe", module.Current?.Source);
            source.Stop();
        });
    }

    [Fact]
    public void OvertakenRead_AppliedThenNewerReadFollows()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var reader = new FakeMediaReader();
            var held = new TaskCompletionSource<IReadOnlyList<MediaSessionFact>>();
            reader.Holds.Enqueue(held);
            var (source, module, published) = Make(reader);

            source.Start();
            Fixture.PumpUntil(() => reader.Reads == 1);

            // A newer change arrives while the first read is still in flight; its read then gives the new session.
            reader.Sessions = new List<MediaSessionFact> { Session("chrome.exe", MediaStatus.Playing) };
            reader.Raise();
            Fixture.PumpUntil(() => false, 300);

            held.SetResult(new List<MediaSessionFact> { Session("spotify.exe", MediaStatus.Playing) });
            Fixture.PumpUntil(() => module.Current?.Source == "chrome.exe");

            // The held read is not stale (the generation did not change), so it applies; the newer request's read follows it.
            Assert.Equal("chrome.exe", module.Current?.Source);
            Assert.Equal(new[] { "spotify.exe", "chrome.exe" }, published);
            source.Stop();
        });
    }

    [Fact]
    public void Stop_ClearsAndUnsubscribes()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var reader = new FakeMediaReader { Sessions = { Session("spotify.exe", MediaStatus.Playing) } };
            var (source, module, _) = Make(reader);

            source.Start();
            Fixture.PumpUntil(() => module.Current is not null);
            Assert.Equal(1, reader.Subscribers);

            source.Stop();

            Assert.Null(module.Current);
            Assert.Equal(1, reader.Stops);
            Assert.Equal(0, reader.Subscribers);
            var reads = reader.Reads;
            reader.Raise();
            Fixture.PumpUntil(() => false, 300);
            Assert.Equal(reads, reader.Reads);
        });
    }

    [Fact]
    public void Artwork_FetchedOncePerKey()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var reader = new FakeMediaReader { Art = Convert.FromBase64String(OnePixelPng) };
            reader.Sessions.Add(Session("spotify.exe", MediaStatus.Playing, art: "spotify.exe|Song|Artist"));
            var art = new RecentCache<ImageSource>(8);
            var (source, _, _) = Make(reader, art);

            source.Start();
            Fixture.PumpUntil(() => art.Get("spotify.exe|Song|Artist") is not null);
            reader.Raise();
            Fixture.PumpUntil(() => reader.Reads >= 3);
            Fixture.PumpUntil(() => false, 200);

            Assert.Single(reader.ArtReads);
            var image = art.Get("spotify.exe|Song|Artist");
            Assert.NotNull(image);
            Assert.True(image!.IsFrozen);
            source.Stop();
        });
    }

    [Fact]
    public void Artwork_FailedKey_NotFetchedAgain()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var reader = new FakeMediaReader { Art = null };  // no thumbnail, or too big
            reader.Sessions.Add(Session("spotify.exe", MediaStatus.Playing, art: "spotify.exe|Song|Artist"));
            var (source, _, _) = Make(reader);

            source.Start();
            Fixture.PumpUntil(() => reader.ArtReads.Count >= 1);
            reader.Raise();
            Fixture.PumpUntil(() => reader.Reads >= 3);
            Fixture.PumpUntil(() => false, 200);

            Assert.Single(reader.ArtReads);
            source.Stop();
        });
    }

    [Fact]
    public void RecentCache_KeepsTheLastEight()
    {
        var cache = new RecentCache<string>(8);
        for (var i = 0; i < 9; i++)
        {
            cache.Put("k" + i, "v" + i);
        }

        Assert.Null(cache.Get("k0"));
        Assert.Equal("v1", cache.Get("k1"));
        Assert.Equal("v8", cache.Get("k8"));
    }

    [Fact]
    public void Toggle_OptimisticThenSend()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var reader = new FakeMediaReader { Sessions = { Session("spotify.exe", MediaStatus.Playing) } };
            var state = NewState(reader);
            Fixture.PumpUntil(() => state.Media.Current is not null);
            Assert.True(state.Media.Current!.Playing);

            state.MediaControl(MediaCommand.Toggle);

            Assert.False(state.Media.Current!.Playing);  // the card flips at once, before SMTC answers
            Fixture.PumpUntil(() => reader.Sent.Count == 1);
            Assert.Equal(("spotify.exe", MediaCommand.Toggle), reader.Sent[0]);
            state.Dispose();
        });
    }

    [Fact]
    public void BusySession_EventsDuringEveryRead_StillApply()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var reader = new FakeMediaReader { RaiseDuringRead = true, Sessions = { Session("spotify.exe", MediaStatus.Playing) } };
            var (source, module, _) = Make(reader);

            source.Start();
            Fixture.PumpUntil(() => module.Current is not null);

            // Every read is overtaken by an event, yet the result of each one is applied (it is not dropped as stale).
            Assert.Equal("spotify.exe", module.Current?.Source);
            source.Stop();
        });
    }

    [Fact]
    public void FollowTimeline_ClosedThenOpen_FollowsAndReadsOnce()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var reader = new FakeMediaReader { Sessions = { Session("spotify.exe", MediaStatus.Playing) } };
            var (source, module, published) = Make(reader);

            source.Start();
            Fixture.PumpUntil(() => module.Current is not null);

            source.FollowTimeline(false);
            Assert.Equal(new[] { false }, reader.FollowCalls);
            var reads = reader.Reads;
            var publishes = published.Count;
            Fixture.PumpUntil(() => false, 200);
            Assert.Equal(reads, reader.Reads);  // closed: no read of its own

            source.FollowTimeline(true);
            Assert.Equal(new[] { false, true }, reader.FollowCalls);
            Fixture.PumpUntil(() => reader.Reads > reads);
            Fixture.PumpUntil(() => false, 300);
            Assert.Equal(reads + 1, reader.Reads);  // opening reads once, so the seek bar rebases
            Assert.True(published.Count > publishes);  // and that read publishes even though nothing changed
            source.Stop();
        });
    }

    [Fact]
    public void PillOpened_DrivesTheTimelineFollow()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var reader = new FakeMediaReader { Sessions = { Session("spotify.exe", MediaStatus.Playing) } };
            var state = NewState(reader);
            Fixture.PumpUntil(() => state.Media.Current is not null);

            state.PillOpened(true);
            state.PillOpened(false);

            Assert.Equal(new[] { true, false }, reader.FollowCalls.Skip(reader.FollowCalls.Count - 2));
            state.Dispose();
        });
    }
}
