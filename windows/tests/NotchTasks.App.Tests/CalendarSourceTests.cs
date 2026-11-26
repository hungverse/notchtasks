using System.Windows.Threading;
using NotchTasks.Core.Calendars;
using NotchTasks.Core.Modules;
using NotchTasks.Core.Notch;
using NotchTasks.Services.Modules;
using Xunit;

namespace NotchTasks.AppTests;

/// The calendar source with fake stores: the store that opens and reads, the one that cannot open (no access, no store), the one whose
/// reads fail, and the restart while a read is in flight. No test touches the real appointment store.
public sealed class CalendarSourceTests
{
    /// A store the test controls: its records, how often it was read, whether it was disposed, the reads that fail next, and a gate
    /// that holds the next read until the test releases it.
    private sealed class FakeStore : ICalendarStore
    {
        public List<CalendarRecord> Records { get; } = new();
        public int Reads { get; private set; }
        public bool Disposed { get; private set; }
        public int FailNext { get; set; }
        public TaskCompletionSource<IReadOnlyList<CalendarRecord>>? Gate { get; set; }

        public event Action? Changed;

        public Task<IReadOnlyList<CalendarRecord>> ReadAsync(DateTimeOffset from, TimeSpan length)
        {
            Reads++;
            if (FailNext > 0)
            {
                FailNext--;
                throw new InvalidOperationException("the store is syncing");
            }
            if (Gate is not null)
            {
                return Gate.Task;
            }
            return Task.FromResult<IReadOnlyList<CalendarRecord>>(Records.ToList());
        }

        public void RaiseChanged() => Changed?.Invoke();

        public void Dispose() => Disposed = true;
    }

    /// An opener that hands out a store, returns null (the store cannot open), or throws. Each Open takes the next of its stores; the last
    /// one repeats.
    private sealed class FakeOpener : ICalendarOpener
    {
        private readonly Queue<ICalendarStore?> _stores;
        private readonly Exception? _failure;

        public FakeOpener(params ICalendarStore?[] stores)
        {
            _stores = new Queue<ICalendarStore?>(stores);
        }

        public FakeOpener(Exception failure)
        {
            _stores = new Queue<ICalendarStore?>();
            _failure = failure;
        }

        public Task<ICalendarStore?> OpenAsync()
        {
            if (_failure is not null)
            {
                throw _failure;
            }
            var next = _stores.Count > 1 ? _stores.Dequeue() : _stores.Peek();
            return Task.FromResult(next);
        }
    }

    /// An opener whose open waits until Release, so a test can stop the source while the store is still opening.
    private sealed class GatedOpener : ICalendarOpener
    {
        private readonly ICalendarStore _store;
        private readonly TaskCompletionSource<ICalendarStore?> _gate = new();

        public GatedOpener(ICalendarStore store)
        {
            _store = store;
        }

        public void Release() => _gate.SetResult(_store);

        public Task<ICalendarStore?> OpenAsync() => _gate.Task;
    }

    private sealed class RecordingAnnouncer : IAnnouncer
    {
        public List<Toast> Toasts { get; } = new();

        public void Announce(Toast t, bool notify = true) => Toasts.Add(t);
    }

    private static CalendarRecord Standup(DateTimeOffset now) =>
        new("standup", "Standup", now.AddHours(1), now.AddHours(1).AddMinutes(30), false, "Work", "#0A84FF", null, null, null);

    private static DispatcherTimer Timer(CalendarSource source) => Fixture.Field<DispatcherTimer>(source, "_timer");

    private static CalendarSource Source(CalendarModule module, ICalendarOpener opener, Action? publish = null) =>
        new(module, opener, publish ?? (() => { }), new SharedClock(() => { }));

    [Fact]
    public void Open_ReadsTheEvents_AndRunsThePollWhileTheStoreIsOpen()
    {
        Sta.Run(() =>
        {
            var now = DateTimeOffset.Now;
            var store = new FakeStore();
            store.Records.Add(Standup(now));
            var module = new CalendarModule(new RecordingAnnouncer());
            var published = 0;
            var source = Source(module, new FakeOpener(store), () => published++);
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
            var module = new CalendarModule(new RecordingAnnouncer());
            var source = Source(module, new FakeOpener(store));
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
            var module = new CalendarModule(new RecordingAnnouncer());
            var published = 0;
            var source = Source(module, new FakeOpener((ICalendarStore?)null), () => published++);
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
            var module = new CalendarModule(new RecordingAnnouncer());
            var source = Source(module, new FakeOpener(new InvalidOperationException("no access")));
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
    public void OneFailedRead_KeepsTheStoreAndThePoll_AndTheNextReadRecovers()
    {
        Sta.Run(() =>
        {
            var store = new FakeStore { FailNext = 1 };
            var module = new CalendarModule(new RecordingAnnouncer());
            var source = Source(module, new FakeOpener(store));
            try
            {
                source.Start();
                Fixture.PumpUntil(() => store.Reads == 1);

                // a transient failure: no hint, the store stays open, and the poll keeps running
                Assert.False(module.Unavailable);
                Assert.False(store.Disposed);
                Assert.True(Timer(source).IsEnabled);

                store.Records.Add(Standup(DateTimeOffset.Now));
                store.RaiseChanged();
                Fixture.PumpUntil(() => module.Events.Count == 1);
                Assert.False(module.Unavailable);
            }
            finally
            {
                source.Stop();
            }
        });
    }

    [Fact]
    public void ThreeFailedReadsInARow_CloseTheStore_AndShowTheHint()
    {
        Sta.Run(() =>
        {
            var store = new FakeStore { FailNext = CalendarSource.MaxFailures };
            var module = new CalendarModule(new RecordingAnnouncer());
            var source = Source(module, new FakeOpener(store));
            try
            {
                source.Start();
                Fixture.PumpUntil(() => store.Reads == 1);
                for (var i = 2; i <= CalendarSource.MaxFailures; i++)
                {
                    store.RaiseChanged();
                    Fixture.PumpUntil(() => store.Reads == i);
                }

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
    public void A_SuccessfulRead_ResetsTheFailureCount()
    {
        Sta.Run(() =>
        {
            var store = new FakeStore { FailNext = CalendarSource.MaxFailures - 1 };
            var module = new CalendarModule(new RecordingAnnouncer());
            var source = Source(module, new FakeOpener(store));
            try
            {
                source.Start();
                Fixture.PumpUntil(() => store.Reads == 1);
                for (var i = 2; i <= CalendarSource.MaxFailures - 1; i++)
                {
                    store.RaiseChanged();
                    Fixture.PumpUntil(() => store.Reads == i);
                }

                store.RaiseChanged();  // this one succeeds, and resets the count
                Fixture.PumpUntil(() => store.Reads == CalendarSource.MaxFailures);
                store.FailNext = CalendarSource.MaxFailures - 1;
                for (var i = 0; i < CalendarSource.MaxFailures - 1; i++)
                {
                    var reads = store.Reads;
                    store.RaiseChanged();
                    Fixture.PumpUntil(() => store.Reads == reads + 1);
                }

                // two failures after the reset are below the limit: still open, no hint
                Assert.False(module.Unavailable);
                Assert.False(store.Disposed);
            }
            finally
            {
                source.Stop();
            }
        });
    }

    [Fact]
    public void StopThenStart_DuringAnInFlightRead_ReadsTheNewStoreAtOnce_AndDropsTheStaleResult()
    {
        Sta.Run(() =>
        {
            var now = DateTimeOffset.Now;
            var old = new FakeStore { Gate = new TaskCompletionSource<IReadOnlyList<CalendarRecord>>() };
            var fresh = new FakeStore();
            fresh.Records.Add(new("fresh", "Fresh", now.AddHours(1), now.AddHours(2), false, "Work", "#0A84FF", null, null, null));
            var module = new CalendarModule(new RecordingAnnouncer());
            var source = Source(module, new FakeOpener(old, fresh));
            try
            {
                source.Start();
                Fixture.PumpUntil(() => old.Reads == 1);  // the old read is in flight, and held

                source.Stop();
                source.Start();

                // the new session reads at once, although the old read never finished
                Fixture.PumpUntil(() => module.Events.Count == 1);
                Assert.Equal(new[] { "Fresh" }, module.Events.Select(e => e.Title));

                // the stale result arrives now: it must not replace the new session's events
                old.Gate.SetResult(new List<CalendarRecord> { Standup(now) });
                Fixture.PumpUntil(() => false, 300);  // let the stale continuation run on the UI thread
                Assert.Equal(new[] { "Fresh" }, module.Events.Select(e => e.Title));
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
            var module = new CalendarModule(new RecordingAnnouncer());
            var source = Source(module, new FakeOpener(store));
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
            var opener = new GatedOpener(store);
            var module = new CalendarModule(new RecordingAnnouncer());
            var source = Source(module, opener);

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

    [Fact]
    public void Reminder_ComesFromTheClock_AtFiveMinutes_NotOnlyOnARead()
    {
        Sta.Run(() =>
        {
            var t0 = DateTimeOffset.Now;
            var store = new FakeStore();
            store.Records.Add(new("later", "Later", t0.AddSeconds(310), t0.AddSeconds(310 + 1800), false, "Work", "#0A84FF", null, null, null));
            var announce = new RecordingAnnouncer();
            var module = new CalendarModule(announce);
            var source = Source(module, new FakeOpener(store));
            try
            {
                source.Start();
                Fixture.PumpUntil(() => module.Events.Count == 1);
                var clock = (IClockUser)source;

                clock.ClockTick(t0.AddSeconds(9));  // 301 s to go: no meeting toast yet
                Assert.Empty(announce.Toasts);

                Assert.True(clock.ClockTick(t0.AddSeconds(10)));  // 300 s to go: the meeting toast, with no store read
                Assert.Single(announce.Toasts);
                Assert.Equal("meeting", announce.Toasts[0].Event);
                Assert.Equal(1, store.Reads);
            }
            finally
            {
                source.Stop();
            }
        });
    }

    [Fact]
    public void ClockTick_ReportsOnlyWhenTheSoonChipChanges()
    {
        Sta.Run(() =>
        {
            var t0 = DateTimeOffset.Now;
            var store = new FakeStore();
            store.Records.Add(new("soon", "Soon", t0.AddMinutes(20), t0.AddMinutes(50), false, "Work", "#0A84FF", null, null, null));
            var module = new CalendarModule(new RecordingAnnouncer());
            var source = Source(module, new FakeOpener(store));
            try
            {
                source.Start();
                Fixture.PumpUntil(() => module.Events.Count == 1);
                var clock = (IClockUser)source;

                Assert.False(clock.ClockTick(t0));  // 20 min out: not in the chip's 15 min window
                Assert.True(clock.ClockTick(t0.AddMinutes(6)));  // the chip appears
                Assert.True(clock.ClockTick(t0.AddMinutes(6).AddSeconds(20)));  // the minutes shown go from 15 to 14
                Assert.False(clock.ClockTick(t0.AddMinutes(6).AddSeconds(30)));  // same minute: nothing to publish
            }
            finally
            {
                source.Stop();
            }
        });
    }

    [Fact]
    public void NeedsClock_IsFalseWhenTheOnlyTimedEventIsDismissed()
    {
        Sta.Run(() =>
        {
            var store = new FakeStore();
            store.Records.Add(Standup(DateTimeOffset.Now));
            var module = new CalendarModule(new RecordingAnnouncer());
            var source = Source(module, new FakeOpener(store));
            var clock = (IClockUser)source;
            try
            {
                source.Start();
                Fixture.PumpUntil(() => module.Events.Count == 1);
                Assert.True(clock.NeedsClock);

                module.Dismiss(module.Events[0].Id);

                Assert.Empty(module.Visible);
                Assert.False(clock.NeedsClock);
            }
            finally
            {
                source.Stop();
            }
        });
    }

    [Fact]
    public void NeedsClock_OnlyWhileTheStoreIsOpenAndATimedEventIsListed()
    {
        Sta.Run(() =>
        {
            var store = new FakeStore();
            store.Records.Add(Standup(DateTimeOffset.Now));
            var module = new CalendarModule(new RecordingAnnouncer());
            var source = Source(module, new FakeOpener(store));
            var clock = (IClockUser)source;
            try
            {
                Assert.False(clock.NeedsClock);  // not started

                source.Start();
                Fixture.PumpUntil(() => module.Events.Count == 1);
                Assert.True(clock.NeedsClock);

                source.Stop();
                Assert.False(clock.NeedsClock);
            }
            finally
            {
                source.Stop();
            }
        });
    }
}
