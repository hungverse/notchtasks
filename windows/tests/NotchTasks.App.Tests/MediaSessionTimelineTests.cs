using NotchTasks.Services.Media;
using Xunit;

namespace NotchTasks.AppTests;

/// The timeline events are followed only while the pill is open: FollowTimeline subscribes them for every session (listed now or later),
/// takes them off when it closes, and never touches the properties and playback subscriptions.
public sealed class MediaSessionTimelineTests
{
    private sealed class FakeManager
    {
        public List<FakeSession> Sessions { get; } = new();
    }

    private sealed class FakeSession
    {
        public FakeSession(string id) => Id = id;

        public string Id { get; }

        public int Base { get; set; }

        public int Timeline { get; set; }

        public int TimelineOff { get; set; }
    }

    private static MediaSessionBook<FakeManager, FakeSession> Make() => new(
        list: m => m.Sessions.ToList(),
        idOf: s => s.Id,
        subscribe: s =>
        {
            s.Base++;
            return new ActionDisposable(() => s.Base--);
        },
        watch: _ => new ActionDisposable(() => { }),
        subscribeTimeline: s =>
        {
            s.Timeline++;
            return new ActionDisposable(() =>
            {
                s.Timeline--;
                s.TimelineOff++;
            });
        });

    [Fact]
    public void Closed_SessionsSubscribeOnlyTheBaseEvents()
    {
        var book = Make();
        var manager = new FakeManager();
        var a = new FakeSession("a");
        manager.Sessions.Add(a);
        Assert.True(book.Attach(book.BeginStart(), manager));
        Assert.True(book.Sync(manager));

        Assert.Equal(1, a.Base);
        Assert.Equal(0, a.Timeline);
        Assert.False(book.Following);
    }

    [Fact]
    public void Open_SubscribesTimelineForListedAndLaterSessions()
    {
        var book = Make();
        var manager = new FakeManager();
        var a = new FakeSession("a");
        manager.Sessions.Add(a);
        Assert.True(book.Attach(book.BeginStart(), manager));
        Assert.True(book.Sync(manager));

        book.FollowTimeline(true);
        var b = new FakeSession("b");
        manager.Sessions.Add(b);
        Assert.True(book.Sync(manager));

        Assert.Equal(1, a.Timeline);
        Assert.Equal(1, b.Timeline);
        Assert.Equal(1, a.Base);  // the base subscription is unchanged
        Assert.True(book.Following);
    }

    [Fact]
    public void Close_UnsubscribesTimelineOnly()
    {
        var book = Make();
        var manager = new FakeManager();
        var a = new FakeSession("a");
        manager.Sessions.Add(a);
        Assert.True(book.Attach(book.BeginStart(), manager));
        Assert.True(book.Sync(manager));
        book.FollowTimeline(true);

        book.FollowTimeline(false);

        Assert.Equal(0, a.Timeline);
        Assert.Equal(1, a.TimelineOff);
        Assert.Equal(1, a.Base);
    }

    [Fact]
    public void FollowTwice_SubscribesOnce()
    {
        var book = Make();
        var manager = new FakeManager();
        var a = new FakeSession("a");
        manager.Sessions.Add(a);
        Assert.True(book.Attach(book.BeginStart(), manager));
        Assert.True(book.Sync(manager));

        book.FollowTimeline(true);
        book.FollowTimeline(true);

        Assert.Equal(1, a.Timeline);
    }
}
