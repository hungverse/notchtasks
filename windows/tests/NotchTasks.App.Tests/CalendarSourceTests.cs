using System.Windows.Threading;
using NotchTasks.Core.Calendars;
using NotchTasks.Core.Modules;
using NotchTasks.Core.Notch;
using NotchTasks.Services.Modules;
using Xunit;

namespace NotchTasks.AppTests;

/// The calendar source with fake stores: the store that opens and reads, the one that cannot open (no access, no store), and the
/// one that opens and then fails to read. No test touches the real appointment store.
public sealed class CalendarSourceTests
{
    /// A store the test controls: its records, how often it was read, whether it was disposed, and a change it can raise.
    private sealed class FakeStore : ICalendarStore
    {
        public List<CalendarRecord> Records { get; } = new();
        public int Reads { get; private set; }
        public bool Disposed { get; private set; }
        public Exception? ReadFailure { get; set; }

        public event Action? Changed;

        public Task<IReadOnlyList<CalendarRecord>> ReadAsync(DateTimeOffset from, TimeSpan length)
        {
            Reads++;
            if (ReadFailure is not null)
            {
                throw ReadFailure;
            }
            return Task.FromResult<IReadOnlyList<CalendarRecord>>(Records.ToList());
        }

        public void RaiseChanged() => Changed?.Invoke();

        public void Dispose() => Disposed = true;
    }

    /// An opener that hands out a store, returns null (the store cannot open), or throws. With hold, the open waits until Release,
    /// so a test can stop the source while the store is still opening.
    private sealed class FakeOpener : ICalendarOpener
    {
        private readonly ICalendarStore? _store;
        private readonly Exception? _failure;
        private readonly TaskCompletionSource<ICalendarStore?>? _gate;

        public FakeOpener(ICalendarStore? store, Exception? failure = null, bool hold = false)
        {
            _store = store;
            _failure = failure;
            _gate = hold ? new TaskCompletionSource<ICalendarStore?>() : null;
        }

        public void Release() => _gate?.SetResult(_store);

        public Task<ICalendarStore?> OpenAsync()
        {
            if (_failure is not null)
            {
                throw _failure;
            }
            return _gate?.Task ?? Task.FromResult(_store);
        }
    }

    private sealed class KeepAnnouncer : IAnnouncer
    {
        public void Announce(Toast t, bool notify = true)
        {
        }
    }

    private static CalendarRecord Standup(DateTimeOffset now) =>
        new("standup", "Standup", now.AddHours(1), now.AddHours(1).AddMinutes(30), false, "Work", "#0A84FF", null, null, null);

    private static DispatcherTimer Timer(CalendarSource source) => Fixture.Field<DispatcherTimer>(source, "_timer");

    [Fact]
    public void Open_ReadsTheEvents_AndRunsThePollWhileTheStoreIsOpen()
    {
        Sta.Run(() =>
        {
            var now = DateTimeOffset.Now;
            var store = new FakeStore();
            store.Records.Add(Standup(now));
            var module = new CalendarModule(new KeepAnnouncer());
            var published = 0;
            var source = new CalendarSource(module, new FakeOpener(store), () => published++);
            try
            {
                source.Start();
                Fixture.PumpUntil(() => module.Events.Count > 0);

                Assert.Equal(new[] { "Standup" }, module.Events.Select(e => e.Title));
                Assert.True(Timer(source).IsEnabled);
                Assert.Equal(CalendarSource.Poll, Timer(source).Interval);
                Assert.True(published > 0);
                Assert.False(module.Unavailable);
            }
            finally
            {
                source.Stop();
            }
        });
    }

    [Fact]
    public void StoreChange_ReadsAgain()
    {
        Sta.Run(() =>
        {
            var store = new FakeStore();
            var module = new CalendarModule(new KeepAnnouncer());
            var source = new CalendarSource(module, new FakeOpener(store), () => { });
            try
            {
                source.Start();
                Fixture.PumpUntil(() => store.Reads == 1);

                store.Records.Add(Standup(DateTimeOffset.Now));
                store.RaiseChanged();
                Fixture.PumpUntil(() => store.Reads == 2 && module.Events.Count == 1);

                Assert.Single(module.Events);
            }
            finally
            {
                source.Stop();
            }
        });
    }

    [Fact]
    public void StoreThatCannotOpen_ShowsTheHint_AndRunsNothing()
    {
        Sta.Run(() =>
        {
            var module = new CalendarModule(new KeepAnnouncer());
            var published = 0;
            var source = new CalendarSource(module, new FakeOpener(null), () => published++);
            try
            {
                source.Start();
                Fixture.PumpUntil(() => module.Unavailable);

                Assert.True(module.Unavailable);
                Assert.Empty(module.Events);
                Assert.False(Timer(source).IsEnabled);  // no poll: the store is not open
                Assert.True(published > 0);
            }
            finally
            {
                source.Stop();
            }
        });
    }

    [Fact]
    public void OpenThatThrows_IsTheSameAsACannotOpen()
    {
        Sta.Run(() =>
        {
            var module = new CalendarModule(new KeepAnnouncer());
            var source = new CalendarSource(module, new FakeOpener(null, new InvalidOperationException("no access")), () => { });
            try
            {
                source.Start();
                Fixture.PumpUntil(() => module.Unavailable);

                Assert.False(Timer(source).IsEnabled);
            }
            finally
            {
                source.Stop();
            }
        });
    }

    [Fact]
    public void StoreThatFailsToRead_ClosesItAndShowsTheHint()
    {
        Sta.Run(() =>
        {
            var store = new FakeStore { ReadFailure = new InvalidOperationException("store gone") };
            var module = new CalendarModule(new KeepAnnouncer());
            var source = new CalendarSource(module, new FakeOpener(store), () => { });
            try
            {
                source.Start();
                Fixture.PumpUntil(() => module.Unavailable);

                Assert.True(store.Disposed);
                Assert.False(Timer(source).IsEnabled);
            }
            finally
            {
                source.Stop();
            }
        });
    }

    [Fact]
    public void Stop_StopsThePoll_ClosesTheStore_AndClearsTheModule()
    {
        Sta.Run(() =>
        {
            var store = new FakeStore();
            store.Records.Add(Standup(DateTimeOffset.Now));
            var module = new CalendarModule(new KeepAnnouncer());
            var source = new CalendarSource(module, new FakeOpener(store), () => { });
            source.Start();
            Fixture.PumpUntil(() => module.Events.Count > 0);

            source.Stop();

            Assert.False(Timer(source).IsEnabled);
            Assert.True(store.Disposed);
            Assert.Empty(module.Events);
            Assert.False(module.Unavailable);
        });
    }

    [Fact]
    public void Stop_BeforeTheStoreOpens_DropsTheStoreWhenItArrives()
    {
        Sta.Run(() =>
        {
            var store = new FakeStore();
            var opener = new FakeOpener(store, hold: true);
            var module = new CalendarModule(new KeepAnnouncer());
            var source = new CalendarSource(module, opener, () => { });

            source.Start();
            source.Stop();
            opener.Release();
            Fixture.PumpUntil(() => store.Disposed);

            // The open that finished after the Stop is not wanted: the store is closed, and nothing is read or shown.
            Assert.Equal(0, store.Reads);
            Assert.False(Timer(source).IsEnabled);
            Assert.Empty(module.Events);
        });
    }
}
