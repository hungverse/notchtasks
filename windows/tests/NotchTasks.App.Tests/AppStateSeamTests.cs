using System.IO;
using System.Text.Json.Nodes;
using NotchTasks.Core;
using NotchTasks.Core.Asks;
using NotchTasks.Core.Notch;
using NotchTasks.Core.Settings;
using NotchTasks.Services;
using NotchTasks.Services.Modules;
using Xunit;

namespace NotchTasks.AppTests;

/// The seams of the app state: the OS readers it is built on, the calls module turned off during a call, and the disposal.
public sealed class AppStateSeamTests
{
    [Fact]
    public void InertOptions_ReachNoRealReader()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var settings = SettingsStore.Load(AppPaths.SettingsFile);

            var options = Fixture.Inert(settings);

            // The Downloads folder is under the temp root (and missing, so nothing is watched); every other reader is a fake.
            Assert.StartsWith(AppPaths.Root, options.DownloadsFolder);
            Assert.False(Directory.Exists(options.DownloadsFolder));
            Assert.NotNull(options.Network);
            Assert.NotNull(options.Power);
            Assert.NotNull(options.Health);
            Assert.NotNull(options.Sensors);
            Assert.NotNull(options.Helper);
            Assert.Equal(HelperState.Off, options.Helper!.State);  // a fake helper that is never started
        });
    }

    [Fact]
    public void CallsTurnedOffDuringACall_ForgetsTheCallAtOnce_WithNoEndedToast_AndPrivacyKeepsTheSensors()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var settings = SettingsStore.Load(AppPaths.SettingsFile);
            var reading = new SensorState(new[] { "zoom.exe" }, false, false);
            Action? captured = null;
            var options = Fixture.Inert(settings) with
            {
                Sensors = new SensorReaders(
                    () => reading,
                    onChange =>
                    {
                        captured = onChange;
                        return new Fixture.NoopWatch();
                    },
                    _ => false,
                    () => false),
            };
            settings.Set("modules", JsonNode.Parse("[\"calls\",\"privacy\"]"));  // both on: one source serves them
            using var state = new AppState(settings, new AskStore(AppPaths.AsksDir), options);
            Assert.Contains("calls", AppState.ActiveModules(settings));

            captured!();  // the baseline reading: a mic app is recording, so a call starts
            Fixture.Flush();
            Assert.NotNull(state.Snapshot.Call);

            settings.Set("disabled", JsonNode.Parse("[\"calls\"]"));  // the calls module is turned off during the call
            Fixture.Flush();

            // The call is gone at once; the sensors stay, so the sensor chip shows again.
            Assert.Null(state.Sensors.Call);
            Assert.Null(state.Snapshot.Call);
            Assert.Equal(new[] { "zoom.exe" }, state.Snapshot.Sensors.MicApps);

            // A call ends 8 s after its app leaves; with calls off it never announces "Ended".
            state.Sensors.Tick(DateTimeOffset.Now.AddSeconds(30));
            Assert.NotEqual("callEnd", state.Toasts.Current?.Event);
            Assert.NotEqual("Ended", state.Toasts.Current?.Title);
        });
    }

    [Fact]
    public void Dispose_UnsubscribesTheThermalSourceFromTheHelper()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var settings = SettingsStore.Load(AppPaths.SettingsFile);
            var state = new AppState(settings, new AskStore(AppPaths.AsksDir), Fixture.Inert(settings));
            var helper = state.Helper;
            var before = Fixture.Field<Action?>(helper, "StateChanged");
            Assert.NotNull(before);
            Assert.Contains(before!.GetInvocationList(), d => d.Target is ThermalSource);

            state.Dispose();

            var after = Fixture.Field<Action?>(helper, "StateChanged");
            Assert.True(after is null || after.GetInvocationList().All(d => d.Target is not ThermalSource));
        });
    }
}
