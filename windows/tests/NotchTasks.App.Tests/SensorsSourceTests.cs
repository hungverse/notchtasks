using NotchTasks.Core.Modules;
using NotchTasks.Core.Notch;
using NotchTasks.Services.Modules;
using Xunit;

namespace NotchTasks.AppTests;

/// The sensors source serves the privacy and calls modules at once: it runs while either is started, and its watch is a fake
/// here, so no test waits on the real registry.
public sealed class SensorsSourceTests
{
    private sealed class FakeWatch : ISensorWatch
    {
        public int Stops { get; private set; }

        public void Stop() => Stops++;
    }

    private sealed class QuietAnnouncer : IAnnouncer
    {
        public void Announce(Toast t, bool notify = true)
        {
        }
    }

    private static readonly SensorState Quiet = new(Array.Empty<string>(), false, false);
    private static readonly SensorState WithCamera = new(Array.Empty<string>(), true, false);

    /// Sensor readers with the given reading and a watch made by watch. The mute is not used by these tests.
    private static SensorReaders Readers(Func<SensorState> read, Func<Action, ISensorWatch> watch) =>
        new(read, watch, _ => false, () => false);

    [Fact]
    public void Start_IsReferenceCounted_AcrossPrivacyAndCalls()
    {
        Sta.Run(() =>
        {
            var module = new SensorsModule(new QuietAnnouncer(), () => true, () => true);
            var watches = new List<FakeWatch>();
            var source = new SensorsSource(module, () => { }, new SharedClock(() => { }), Readers(() => Quiet, _ =>
            {
                var watch = new FakeWatch();
                watches.Add(watch);
                return watch;
            }));

            source.Start();  // privacy comes on
            Assert.Single(watches);
            source.Start();  // calls come on: the watch is already running
            Assert.Single(watches);

            source.Stop();  // privacy goes off: calls still needs it
            Assert.Equal(0, watches[0].Stops);
            source.Stop();  // calls goes off: the last one stops the watch
            Assert.Equal(1, watches[0].Stops);

            source.Stop();  // an extra stop does nothing
            Assert.Equal(1, watches[0].Stops);

            source.Start();  // the next start makes a new watch
            Assert.Equal(2, watches.Count);
        });
    }

    [Fact]
    public void ModuleHost_StartsTheSourceOnce_ForBothModules()
    {
        Sta.Run(() =>
        {
            var module = new SensorsModule(new QuietAnnouncer(), () => true, () => true);
            var watches = new List<FakeWatch>();
            var source = new SensorsSource(module, () => { }, new SharedClock(() => { }), Readers(() => Quiet, _ =>
            {
                var watch = new FakeWatch();
                watches.Add(watch);
                return watch;
            }));
            var host = new ModuleHost(
                new Dictionary<string, IModuleSource>(StringComparer.Ordinal) { ["privacy"] = source, ["calls"] = source },
                _ => { });

            host.Apply(new[] { "privacy", "calls" });
            Assert.Single(watches);
            host.Apply(new[] { "calls" });  // privacy turned off: the source keeps running
            Assert.Equal(0, watches[0].Stops);
            host.Apply(Array.Empty<string>());  // calls turned off: the watch stops
            Assert.Equal(1, watches[0].Stops);
            host.StopAll();
            Assert.Equal(1, watches[0].Stops);
        });
    }

    [Fact]
    public void UnchangedReading_DoesNotPublish_AChangedOneDoes()
    {
        Sta.Run(() =>
        {
            var module = new SensorsModule(new QuietAnnouncer(), () => true, () => true);
            var reading = Quiet;
            Action? captured = null;
            var published = 0;
            var source = new SensorsSource(module, () => published++, new SharedClock(() => { }),
                Readers(() => reading, onChange =>
                {
                    captured = onChange;
                    return new FakeWatch();
                }));
            try
            {
                source.Start();
                captured!();  // the baseline: the same as the quiet state the module starts from
                Fixture.Flush();
                Assert.Equal(0, published);

                reading = Quiet with { };  // the registry notified, but the same sensors
                captured!();
                Fixture.Flush();
                Assert.Equal(0, published);

                reading = WithCamera;  // a change is shown
                captured!();
                Fixture.Flush();
                Assert.Equal(1, published);

                captured!();  // the same again
                Fixture.Flush();
                Assert.Equal(1, published);
            }
            finally
            {
                source.Stop();
            }
        });
    }
}
