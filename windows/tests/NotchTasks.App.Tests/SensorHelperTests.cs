using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using NotchTasks.Core;
using NotchTasks.Core.Modules;
using NotchTasks.Core.Sensors;
using NotchTasks.Core.Settings;
using NotchTasks.Services;
using Xunit;

namespace NotchTasks.AppTests;

/// The temperature helper's app side, with fakes for the UAC launch, the pipe and the stop event. No test starts a process
/// elevated, opens a real pipe, or creates a scheduled task.
public sealed class SensorHelperTests
{
    private const string Sid = "S-1-5-21-1111111111-2222222222-3333333333-1001";
    private const string HelperExe = @"C:\app\NotchTasksSensors.exe";
    private const int AppPid = 4242;

    private sealed class FakeLauncher : IProcessLauncher
    {
        public int Calls;
        public string? LastArguments;
        public Exception? Throws;

        /// The process the launch gives back (null: no process, as in a test with no UAC).
        public Process? Started;

        /// When set, the launch (the UAC prompt) is pending until the test completes it.
        public TaskCompletionSource<Process?>? Pending;

        public Task<Process?> RunAsAsync(string fileName, string arguments)
        {
            Calls++;
            LastArguments = arguments;
            if (Throws is not null)
            {
                return Task.FromException<Process?>(Throws);
            }
            return Pending?.Task ?? Task.FromResult(Started);
        }
    }

    private sealed class FakePipe : ISensorPipe
    {
        public int Calls;
        public IReadOnlyList<ThermalReading>? Answer;

        public Task<IReadOnlyList<ThermalReading>?> QueryAsync(CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(Answer);
        }
    }

    private sealed class FakeStop : IHelperStop
    {
        public int Signals;

        public bool Signal()
        {
            Signals++;
            return true;
        }
    }

    /// The helper over fakes. marker says whether this user's sign-in marker is in place (the only file the helper looks at).
    private static (SensorHelper Helper, FakeLauncher Launcher, FakePipe Pipe, FakeStop Stop) NewHelper(SettingsStore settings, bool marker = false)
    {
        var launcher = new FakeLauncher();
        var pipe = new FakePipe();
        var stop = new FakeStop();
        var markerPath = SensorNames.MarkerPath(Sid);
        var helper = new SensorHelper(settings, launcher, pipe, stop, HelperExe, Sid, AppPid,
            path => marker && path == markerPath);
        return (helper, launcher, pipe, stop);
    }

    [Fact]
    public async Task TurnOff_WithMarker_RunsUninstall_EvenWhenTheSyncedFlagIsOff()
    {
        using var root = new TempRoot();
        File.WriteAllText(AppPaths.SettingsFile, "{\"sensorHelper\": true, \"sensorHelperAtSignIn\": false}");
        using var settings = SettingsStore.Load(AppPaths.SettingsFile);
        var (helper, launcher, _, stop) = NewHelper(settings, marker: true);

        // The launcher returns no process here (no UAC in tests): the uninstall is then not run to an end, and nothing changes.
        await helper.TurnOffAsync();

        Assert.Equal(1, launcher.Calls);
        Assert.Equal($"--uninstall-logon {Sid}", launcher.LastArguments);
        Assert.Equal(0, stop.Signals);
        Assert.True(settings.SensorHelper);
    }

    [Fact]
    public async Task TurnOff_WithoutMarker_NoUac_EvenIfTheSyncedSignInFlagIsOn()
    {
        using var root = new TempRoot();
        File.WriteAllText(AppPaths.SettingsFile, "{\"sensorHelper\": true, \"sensorHelperAtSignIn\": true}");
        using var settings = SettingsStore.Load(AppPaths.SettingsFile);
        var (helper, launcher, _, stop) = NewHelper(settings, marker: false);
        helper.Start();

        await helper.TurnOffAsync();

        Assert.Equal(0, launcher.Calls);
        Assert.Equal(1, stop.Signals);
        Assert.False(settings.SensorHelper);
        Assert.Equal(HelperState.Off, helper.State);
    }

    [Fact]
    public void Start_WithoutMarker_AndSyncedSignIn_IsOff_NeverLaunches()
    {
        using var root = new TempRoot();
        File.WriteAllText(AppPaths.SettingsFile, "{\"sensorHelper\": true, \"sensorHelperAtSignIn\": true}");
        using var settings = SettingsStore.Load(AppPaths.SettingsFile);
        var (helper, launcher, pipe, _) = NewHelper(settings, marker: false);

        helper.Start();

        Assert.Equal(HelperState.Off, helper.State);
        Assert.False(helper.HasSignInTask);
        Assert.Equal(0, launcher.Calls);
        Assert.Equal(0, pipe.Calls);
    }

    [Fact]
    public void HasSignInTask_FollowsTheLocalMarker_NotTheSettings()
    {
        using var root = new TempRoot();
        using var settings = SettingsStore.Load(AppPaths.SettingsFile);

        Assert.False(NewHelper(settings, marker: false).Helper.HasSignInTask);
        Assert.True(NewHelper(settings, marker: true).Helper.HasSignInTask);
    }

    [Fact]
    public async Task Helper_Off_ByDefault_NoProcessStarted()
    {
        using var root = new TempRoot();
        using var settings = SettingsStore.Load(AppPaths.SettingsFile);
        var (helper, launcher, pipe, stop) = NewHelper(settings);

        helper.Start();
        var readings = await helper.ReadAsync();

        Assert.Equal(HelperState.Off, helper.State);
        Assert.Empty(readings);
        Assert.Equal(0, launcher.Calls);
        Assert.Equal(0, pipe.Calls);
        Assert.Equal(0, stop.Signals);
    }

    [Fact]
    public async Task Helper_Declined_StaysDeclined_NoRetry()
    {
        using var root = new TempRoot();
        using var settings = SettingsStore.Load(AppPaths.SettingsFile);
        var (helper, launcher, pipe, _) = NewHelper(settings);
        launcher.Throws = new Win32Exception(1223);  // ERROR_CANCELLED: the UAC prompt was cancelled

        await helper.TurnOnAsync(atSignIn: true);

        Assert.Equal(HelperState.Declined, helper.State);
        Assert.False(settings.SensorHelper);
        Assert.False(settings.SensorHelperAtSignIn);
        Assert.Equal(1, launcher.Calls);

        // Nothing starts it again by itself: a start, a read and a second app start do not prompt.
        helper.Start();
        Assert.Empty(await helper.ReadAsync());
        Assert.Equal(1, launcher.Calls);
        Assert.Equal(0, pipe.Calls);
        Assert.Equal(HelperState.Off, helper.State);
    }

    [Fact]
    public void Start_WithSensorOn_AndSignInOff_NeverLaunches()
    {
        using var root = new TempRoot();
        File.WriteAllText(AppPaths.SettingsFile, "{\"sensorHelper\": true}");
        using var settings = SettingsStore.Load(AppPaths.SettingsFile);
        var (helper, launcher, pipe, _) = NewHelper(settings);

        helper.Start();

        Assert.Equal(HelperState.Paused, helper.State);
        Assert.Equal(0, launcher.Calls);
        Assert.Equal(0, pipe.Calls);
    }

    [Fact]
    public async Task TurnOn_WithUac_Accepted_AnswersAndShowsOn()
    {
        using var root = new TempRoot();
        using var settings = SettingsStore.Load(AppPaths.SettingsFile);
        var (helper, launcher, pipe, _) = NewHelper(settings);
        pipe.Answer = new[] { new ThermalReading("TZ00", 55) };

        await helper.TurnOnAsync(atSignIn: false);

        Assert.Equal(HelperState.On, helper.State);
        Assert.True(settings.SensorHelper);
        Assert.Equal(1, launcher.Calls);
        Assert.Equal($"--install-pawnio --serve --parent {AppPid} --user {Sid}", launcher.LastArguments);
    }

    [Fact]
    public async Task TurnOn_WithSignIn_AsksForTheInstall_Once()
    {
        using var root = new TempRoot();
        using var settings = SettingsStore.Load(AppPaths.SettingsFile);
        var (helper, launcher, pipe, _) = NewHelper(settings);
        pipe.Answer = new[] { new ThermalReading("TZ00", 55) };

        await helper.TurnOnAsync(atSignIn: true);

        Assert.Equal(HelperState.On, helper.State);
        Assert.True(settings.SensorHelperAtSignIn);
        Assert.Equal(1, launcher.Calls);
        Assert.Equal($"--install-pawnio --install-logon {Sid} --parent {AppPid}", launcher.LastArguments);
    }

    [Fact]
    public async Task TurnOn_NoZones_IsUnavailable_AndClearsTheSetting()
    {
        using var root = new TempRoot();
        using var settings = SettingsStore.Load(AppPaths.SettingsFile);
        var (helper, _, pipe, stop) = NewHelper(settings);
        pipe.Answer = Array.Empty<ThermalReading>();

        await helper.TurnOnAsync(atSignIn: false);

        Assert.Equal(HelperState.Unavailable, helper.State);
        Assert.False(settings.SensorHelper);
        Assert.Equal(1, stop.Signals);
    }

    [Fact]
    public void Probe_AtStart_NoZones_ShowsUnavailable_WithoutWritingFlagsOrSignallingTheHelper()
    {
        using var root = new TempRoot();
        File.WriteAllText(AppPaths.SettingsFile, "{\"sensorHelper\": true, \"sensorHelperAtSignIn\": true}");
        using var settings = SettingsStore.Load(AppPaths.SettingsFile);
        var (helper, launcher, pipe, stop) = NewHelper(settings, marker: true);
        pipe.Answer = Array.Empty<ThermalReading>();  // the helper answers, with no zones

        helper.Start();

        // Only a user's click may change the settings: the probe shows Unavailable and leaves the flags and the helper alone.
        Assert.Equal(HelperState.Unavailable, helper.State);
        Assert.True(settings.SensorHelper);
        Assert.True(settings.SensorHelperAtSignIn);
        Assert.Equal(0, stop.Signals);
        Assert.Equal(0, launcher.Calls);
    }

    [Fact]
    public async Task TurnOn_LaunchStillPending_DoesNotBlockTheCaller()
    {
        using var root = new TempRoot();
        using var settings = SettingsStore.Load(AppPaths.SettingsFile);
        var (helper, launcher, pipe, _) = NewHelper(settings);
        launcher.Pending = new TaskCompletionSource<Process?>();
        pipe.Answer = new[] { new ThermalReading("TZ00", 55) };

        // The UAC prompt is up: the call returns at once (the launch is awaited, not run on this thread), and the state is Starting.
        var turnOn = helper.TurnOnAsync(atSignIn: false);
        Assert.False(turnOn.IsCompleted);
        Assert.Equal(HelperState.Starting, helper.State);

        launcher.Pending.SetResult(null);  // the prompt is answered
        await turnOn;

        Assert.Equal(HelperState.On, helper.State);
    }

    [Fact]
    public async Task TurnOn_HelperExitsWithCode1_BeforeAnswering_IsFailed_LogsTheCode_AndClearsTheSettings()
    {
        using var root = new TempRoot();
        using var settings = SettingsStore.Load(AppPaths.SettingsFile);
        var (helper, launcher, pipe, _) = NewHelper(settings);
        // A helper process that ends at once with exit code 1 (an ordinary process: the launch's stand-in, no UAC)
        launcher.Started = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 1") { UseShellExecute = false, CreateNoWindow = true })!;
        pipe.Answer = null;

        await helper.TurnOnAsync(atSignIn: false);

        Assert.Equal(HelperState.Failed, helper.State);
        Assert.False(settings.SensorHelper);
        Assert.False(settings.SensorHelperAtSignIn);
        var log = File.ReadAllText(ErrorLog.FileOverride!);
        Assert.Contains("NotchTasks: the temperature helper exited with code 1 before answering", log);
    }

    [Fact]
    public async Task TurnOff_WithoutSignIn_StopsTheHelper_WithoutUac()
    {
        using var root = new TempRoot();
        File.WriteAllText(AppPaths.SettingsFile, "{\"sensorHelper\": true}");
        using var settings = SettingsStore.Load(AppPaths.SettingsFile);
        var (helper, launcher, _, stop) = NewHelper(settings);
        helper.Start();

        await helper.TurnOffAsync();

        Assert.Equal(HelperState.Off, helper.State);
        Assert.False(settings.SensorHelper);
        Assert.Equal(1, stop.Signals);
        Assert.Equal(0, launcher.Calls);
    }
}
