using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using NotchTasks.Core;
using NotchTasks.Core.Modules;
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

        public Process? RunAs(string fileName, string arguments)
        {
            Calls++;
            LastArguments = arguments;
            if (Throws is not null)
            {
                throw Throws;
            }
            return null;
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

    private static (SensorHelper Helper, FakeLauncher Launcher, FakePipe Pipe, FakeStop Stop) NewHelper(SettingsStore settings)
    {
        var launcher = new FakeLauncher();
        var pipe = new FakePipe();
        var stop = new FakeStop();
        var helper = new SensorHelper(settings, launcher, pipe, stop, HelperExe, Sid, AppPid);
        return (helper, launcher, pipe, stop);
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
        Assert.Equal($"--serve --parent {AppPid} --user {Sid}", launcher.LastArguments);
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
        Assert.Equal($"--install-logon {Sid} --parent {AppPid}", launcher.LastArguments);
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
