using NotchTasks.Core.Modules;
using NotchTasks.Core.Sensors;
using Xunit;

namespace NotchTasksSensors.Tests;

/// The sign-in install over a fake machine. A reader that fails on its first read does not stop the install; the steps run in order and
/// the install completes. Nothing is created, copied or scheduled.
public sealed class SensorInstallLogonTests
{
    private const string Sid = "S-1-5-21-1111111111-2222222222-3333333333-1001";

    private sealed class NoSink : IFailureSink
    {
        public void Report(Exception error, int eventId)
        {
        }
    }

    /// A machine that records each step. Its failure switches make one step throw.
    private sealed class FakeInstallSystem : IInstallSystem
    {
        public List<string> Steps { get; } = new();
        public bool FolderExists { get; set; }
        public bool OtherHelpers { get; set; }
        public bool RegisterFails { get; set; }
        public bool TaskPresent { get; set; }

        public string TargetFolder => @"C:\Program Files\NotchTasks Sensors";

        public bool FolderExisted(string target) => FolderExists;

        public void EnsureFolder(string target) => Steps.Add("EnsureFolder");

        public bool TaskExists(string taskName) => TaskPresent;

        public void EndTask(string taskName) => Steps.Add("EndTask");

        public void SignalStopAndWait(string sid) => Steps.Add("SignalStopAndWait");

        public bool OtherHelpersRunning(string target, string sid) => OtherHelpers;

        public void CopyFiles(string target) => Steps.Add("CopyFiles");

        public void RegisterTask(string sid, string target, int? parent)
        {
            Steps.Add("RegisterTask");
            if (RegisterFails)
            {
                throw new InvalidOperationException("schtasks create failed");
            }
        }

        public void WriteMarker(string sid) => Steps.Add("WriteMarker");

        public void RunTask(string sid) => Steps.Add("RunTask");

        public void Rollback(string sid, string target, bool folderExisted, bool taskRegistered, bool markerWritten) =>
            Steps.Add($"Rollback(registered={taskRegistered},marker={markerWritten})");
    }

    /// A reader with a fixed answer, or a failure on its first read.
    private sealed class FakeReader : IThermalReader
    {
        private readonly IReadOnlyList<ThermalReading>? _zones;
        private readonly Exception? _failure;
        private bool _read;

        public FakeReader(IReadOnlyList<ThermalReading> zones)
        {
            _zones = zones;
        }

        public FakeReader(Exception failure)
        {
            _failure = failure;
        }

        public IReadOnlyList<ThermalReading> Read()
        {
            var first = !_read;
            _read = true;
            if (_failure is not null && first)
            {
                throw _failure;
            }
            return _zones ?? Array.Empty<ThermalReading>();
        }
    }

    private static readonly IReadOnlyList<ThermalReading> Zones = [new ThermalReading("GPU", 55)];

    public SensorInstallLogonTests()
    {
        HelperLog.Sink = new NoSink();
    }

    [Fact]
    public async Task ReaderFailsOnItsFirstRead_TheInstallStillCompletes()
    {
        var system = new FakeInstallSystem();
        var reader = new FakeReader(new ManagementFailure());

        var code = await SensorInstaller.InstallLogonAsync(Sid, null, reader, system);

        Assert.Equal(0, code);
        Assert.Equal(
            ["EnsureFolder", "SignalStopAndWait", "CopyFiles", "RegisterTask", "WriteMarker", "RunTask"],
            system.Steps);
    }

    [Fact]
    public async Task ReaderGivesZones_TheInstallCompletesTheSameWay()
    {
        var system = new FakeInstallSystem();

        var code = await SensorInstaller.InstallLogonAsync(Sid, 4242, new FakeReader(Zones), system);

        Assert.Equal(0, code);
        Assert.Equal(
            ["EnsureFolder", "SignalStopAndWait", "CopyFiles", "RegisterTask", "WriteMarker", "RunTask"],
            system.Steps);
    }

    [Fact]
    public async Task AnotherUsersHelperRunsFromTheFolder_TheFilesAreNotCopied()
    {
        var system = new FakeInstallSystem { OtherHelpers = true };

        var code = await SensorInstaller.InstallLogonAsync(Sid, null, new FakeReader(Zones), system);

        Assert.Equal(0, code);
        Assert.DoesNotContain("CopyFiles", system.Steps);
    }

    [Fact]
    public async Task ExistingTask_IsEndedBeforeItsFilesChange()
    {
        var system = new FakeInstallSystem { TaskPresent = true, FolderExists = true };

        await SensorInstaller.InstallLogonAsync(Sid, null, new FakeReader(Zones), system);

        Assert.Equal("EndTask", system.Steps[1]);
    }

    [Fact]
    public async Task RegisterFails_RollsBackWithoutTheTask_AndReturnsOne()
    {
        var system = new FakeInstallSystem { RegisterFails = true };

        var code = await SensorInstaller.InstallLogonAsync(Sid, null, new FakeReader(Zones), system);

        Assert.Equal(1, code);
        Assert.Equal("Rollback(registered=False,marker=False)", system.Steps[^1]);
    }

    /// The exception a failing WMI query gives in place of a reading.
    private sealed class ManagementFailure : Exception
    {
        public ManagementFailure()
            : base("Not supported")
        {
        }
    }
}
