using System.Windows.Threading;
using NotchTasks.Core.Modules;
using NotchTasks.Core.Notch;
using NotchTasks.Services.Modules;
using Xunit;

namespace NotchTasks.AppTests;

/// The sensors source takes readings from the watch's thread: the baseline is the watch's first callback, and a reading taken
/// before a Stop must not apply after a quick Start.
public sealed class SensorsSourceGenerationTests
{
    /// A watch that keeps the callback it was given, so a test can play the watch thread.
    private sealed class CapturingWatch : ISensorWatch
    {
        public Action? OnChange { get; init; }

        public void Stop()
        {
        }
    }

    private sealed class QuietAnnouncer : IAnnouncer
    {
        public void Announce(Toast t, bool notify = true)
        {
        }
    }

    private static readonly SensorState Quiet = new(Array.Empty<string>(), false, false);
    private static readonly SensorState WithCamera = new(Array.Empty<string>(), true, false);

    [Fact]
    public void WatchCallback_IsTheBaseline_AndAppliesOnTheUiThread()
    {
        Sta.Run(() =>
        {
            var module = new SensorsModule(new QuietAnnouncer(), () => true, () => true);
            var reading = Quiet;
            Action? captured = null;
            var source = new SensorsSource(module, () => { }, () => reading, onChange =>
            {
                captured = onChange;
                return new CapturingWatch();
            });

            source.Start();
            Assert.Equal(Quiet, module.Sensors);  // Start itself reads nothing: the watch thread takes the baseline
            reading = WithCamera;
            captured!();  // the watch thread's callback
            Flush();

            Assert.True(module.Sensors.Camera);
            source.Stop();
        });
    }

    [Fact]
    public void ReadingQueuedBeforeStop_DoesNotApplyAfterAQuickRestart()
    {
        Sta.Run(() =>
        {
            var module = new SensorsModule(new QuietAnnouncer(), () => true, () => true);
            var reading = Quiet;
            var callbacks = new List<Action>();
            var source = new SensorsSource(module, () => { }, () => reading, onChange =>
            {
                callbacks.Add(onChange);
                return new CapturingWatch();
            });

            source.Start();
            reading = WithCamera;
            callbacks[0]();   // a reading from the first run is posted to the UI thread...
            source.Stop();    // ...but the run ends before the UI thread takes it
            source.Start();   // and a new run starts at once
            Flush();

            Assert.False(module.Sensors.Camera);  // the stale reading was dropped

            callbacks[1]();   // the new run's own reading does apply
            Flush();

            Assert.True(module.Sensors.Camera);
            source.Stop();
        });
    }

    /// Runs the posted work queued before this call on the UI thread.
    private static void Flush() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
}
