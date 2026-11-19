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

    [Fact]
    public void Start_IsReferenceCounted_AcrossPrivacyAndCalls()
    {
        Sta.Run(() =>
        {
            var module = new SensorsModule(new QuietAnnouncer(), () => true, () => true);
            var watches = new List<FakeWatch>();
            var source = new SensorsSource(module, () => { }, () => Quiet, _ =>
            {
                var watch = new FakeWatch();
                watches.Add(watch);
                return watch;
            });

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
            var source = new SensorsSource(module, () => { }, () => Quiet, _ =>
            {
                var watch = new FakeWatch();
                watches.Add(watch);
                return watch;
            });
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
}
