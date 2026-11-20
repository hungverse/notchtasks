using NotchTasks.Services.Media;
using Xunit;

namespace NotchTasks.AppTests;

/// The bookkeeping under SmtcReader, with fake sessions and a fake manager: a Start overtaken by a Stop does nothing, a manager is
/// watched once, a session change after Stop subscribes nothing, and a session that closed and reopened is resubscribed.
public sealed class MediaSessionBookTests
{
    private sealed class FakeManager
    {
        public List<FakeSession> Sessions { get; } = new();

        public int Watches { get; set; }

        public int Unwatches { get; set; }
    }

    private sealed class FakeSession
    {
        public FakeSession(string id) => Id = id;

        public string Id { get; }

        public int Subscribes { get; set; }

        public int Unsubscribes { get; set; }
    }

    private static (MediaSessionBook<FakeManager, FakeSession> Book, FakeManager Manager) Make()
    {
        var book = new MediaSessionBook<FakeManager, FakeSession>(
            list: m => m.Sessions.ToList(),
            idOf: s => s.Id,
            subscribe: s =>
            {
                s.Subscribes++;
                return new ActionDisposable(() => s.Unsubscribes++);
            },
            watch: m =>
            {
                m.Watches++;
                return new ActionDisposable(() => m.Unwatches++);
            });
        return (book, new FakeManager());
    }

    [Fact]
    public void Sync_SubscribesListedSessions()
    {
        var (book, manager) = Make();
        var a = new FakeSession("a");
        manager.Sessions.Add(a);

        Assert.True(book.Attach(book.BeginStart(), manager));
        Assert.True(book.Sync(manager));

        Assert.Equal(1, book.Count);
        Assert.Equal(1, a.Subscribes);
        Assert.Same(a, book.Find("a"));
        Assert.Equal(1, manager.Watches);
    }

    [Fact]
    public void Stop_ThenLateSync_SubscribesNothing()
    {
        var (book, manager) = Make();
        var a = new FakeSession("a");
        manager.Sessions.Add(a);
        Assert.True(book.Attach(book.BeginStart(), manager));
        Assert.True(book.Sync(manager));

        book.Stop();
        manager.Sessions.Add(new FakeSession("b"));

        Assert.False(book.Sync(manager));
        Assert.Equal(0, book.Count);
        Assert.Null(book.Find("b"));
        Assert.Equal(1, a.Unsubscribes);
        Assert.Equal(1, manager.Unwatches);
        Assert.Null(book.Manager);
    }

    [Fact]
    public void StartOvertakenByStop_DoesNothingWhenItLands()
    {
        var (book, manager) = Make();
        var first = book.BeginStart();
        book.Stop();

        Assert.False(book.Attach(first, manager));
        Assert.Equal(0, manager.Watches);
        Assert.Null(book.Manager);
    }

    [Fact]
    public void StartStopStart_WhileFirstPending_WatchesOnce()
    {
        var (book, manager) = Make();
        var first = book.BeginStart();
        book.Stop();
        var second = book.BeginStart();

        Assert.False(book.Attach(first, manager));
        Assert.True(book.Attach(second, manager));
        Assert.False(book.Attach(second, manager));  // already attached: no second watch
        Assert.Equal(1, manager.Watches);
    }

    [Fact]
    public void SessionThatReopened_IsResubscribed()
    {
        var (book, manager) = Make();
        var old = new FakeSession("spotify.exe");
        manager.Sessions.Add(old);
        Assert.True(book.Attach(book.BeginStart(), manager));
        Assert.True(book.Sync(manager));

        var reopened = new FakeSession("spotify.exe");
        manager.Sessions[0] = reopened;
        Assert.True(book.Sync(manager));

        Assert.Equal(1, old.Unsubscribes);
        Assert.Equal(1, reopened.Subscribes);
        Assert.Same(reopened, book.Find("spotify.exe"));
        Assert.Equal(1, book.Count);
    }

    [Fact]
    public void SessionGone_IsUnsubscribed()
    {
        var (book, manager) = Make();
        var a = new FakeSession("a");
        manager.Sessions.Add(a);
        Assert.True(book.Attach(book.BeginStart(), manager));
        Assert.True(book.Sync(manager));

        manager.Sessions.Clear();
        Assert.True(book.Sync(manager));

        Assert.Equal(0, book.Count);
        Assert.Equal(1, a.Unsubscribes);
        Assert.Null(book.Find("a"));
    }

    [Fact]
    public void ForeignManager_IsIgnored()
    {
        var (book, manager) = Make();
        Assert.True(book.Attach(book.BeginStart(), manager));

        Assert.False(book.Sync(new FakeManager()));
    }
}
