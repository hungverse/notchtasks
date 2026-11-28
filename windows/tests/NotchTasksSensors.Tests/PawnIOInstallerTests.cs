using NotchTasks.Core.Sensors;
using Xunit;

namespace NotchTasksSensors.Tests;

/// The PawnIO step over a fake machine: no service, no winget, no download, no process is started. The fake's downloaded setup records
/// whether it was held open when it was checked and when it ran, and whether it was released and deleted afterwards.
public sealed class PawnIOInstallerTests
{
    private const string Setup = @"C:\Temp\PawnIO_setup-test.exe";
    private const string AdminWinget = @"C:\Program Files\WindowsApps\Microsoft.DesktopAppInstaller_1.25.0.0_x64__8wekyb3d8bbwe\winget.exe";

    private sealed class FakeSetup : IPawnIOSetupFile
    {
        public FakeSetup(string path) => Path = path;

        public string Path { get; }
        public bool Held { get; private set; } = true;
        public bool Deleted { get; private set; }

        public void Dispose()
        {
            Held = false;
            Deleted = true;
        }
    }

    private sealed class FakeSystem : IPawnIOSystem
    {
        public bool ServiceAfterInstall { get; set; }
        public bool ServiceBefore { get; set; }
        public string? Winget { get; set; }
        public bool WingetMicrosoftSigned { get; set; } = true;
        public int RunExitCode { get; set; }
        public bool DownloadFails { get; set; }
        public bool Signed { get; set; } = true;
        public List<(string File, string Arguments)> Runs { get; } = new();
        public int Downloads { get; private set; }
        public FakeSetup? Download { get; private set; }
        public List<string> Checked { get; } = new();
        public List<bool> HeldAtCheck { get; } = new();
        public List<bool> HeldAtRun { get; } = new();

        public bool ServiceExists(string name) => ServiceBefore || (Runs.Count > 0 && ServiceAfterInstall);

        public string? FindWinget() => Winget;

        public bool IsMicrosoftSigned(string path) => WingetMicrosoftSigned && path == Winget;

        public int Run(string fileName, string arguments)
        {
            Runs.Add((fileName, arguments));
            if (Download is not null && fileName == Download.Path)
            {
                HeldAtRun.Add(Download.Held);
            }
            return RunExitCode;
        }

        public Task<IPawnIOSetupFile?> DownloadSetupAsync(CancellationToken ct)
        {
            Downloads++;
            if (DownloadFails)
            {
                return Task.FromResult<IPawnIOSetupFile?>(null);
            }
            Download = new FakeSetup(Setup);
            return Task.FromResult<IPawnIOSetupFile?>(Download);
        }

        public bool HasValidSignature(string path)
        {
            Checked.Add(path);
            if (Download is not null && path == Download.Path)
            {
                HeldAtCheck.Add(Download.Held);
            }
            return Signed;
        }
    }

    [Fact]
    public async Task ServicePresent_InstallsNothing()
    {
        var system = new FakeSystem { ServiceBefore = true, Winget = AdminWinget };

        var code = await new PawnIOInstaller(system).RunAsync();

        Assert.Equal(PawnIOExit.Installed, code);
        Assert.Empty(system.Runs);
        Assert.Equal(0, system.Downloads);
    }

    [Fact]
    public async Task AdminWinget_RunsTheWingetCommandLineExactly()
    {
        var system = new FakeSystem { Winget = AdminWinget, RunExitCode = 0, ServiceAfterInstall = true };

        var code = await new PawnIOInstaller(system).RunAsync();

        Assert.Equal(PawnIOExit.Installed, code);
        var run = Assert.Single(system.Runs);
        Assert.Equal(AdminWinget, run.File);  // by its full path, never a name to search for
        Assert.Equal(
            "install --id namazso.PawnIO --exact --silent --scope machine --accept-package-agreements --accept-source-agreements",
            run.Arguments);
        Assert.Equal(0, system.Downloads);
    }

    [Fact]
    public async Task NoWingetInTheAdminFolder_TakesTheDownload()
    {
        // winget may be on PATH or in the user's alias folder: the step never looks there, so it is not found and the download runs.
        var system = new FakeSystem { Winget = null, Signed = true, ServiceAfterInstall = true };

        var code = await new PawnIOInstaller(system).RunAsync();

        Assert.Equal(PawnIOExit.Installed, code);
        Assert.Equal(1, system.Downloads);
        var run = Assert.Single(system.Runs);
        Assert.Equal(Setup, run.File);
        Assert.DoesNotContain(system.Runs, r => r.File == "winget");
    }

    [Fact]
    public async Task AdminWingetNotMicrosoftSigned_IsNotRun_AndTheDownloadRuns()
    {
        var system = new FakeSystem { Winget = AdminWinget, WingetMicrosoftSigned = false, Signed = true, ServiceAfterInstall = true };

        var code = await new PawnIOInstaller(system).RunAsync();

        Assert.Equal(PawnIOExit.Installed, code);
        Assert.DoesNotContain(system.Runs, r => r.File == AdminWinget);
        Assert.Equal(1, system.Downloads);
    }

    [Fact]
    public async Task WingetFails_ReportsWingetFailed()
    {
        var system = new FakeSystem { Winget = AdminWinget, RunExitCode = 1 };

        var code = await new PawnIOInstaller(system).RunAsync();

        Assert.Equal(PawnIOExit.WingetFailed, code);
    }

    [Fact]
    public async Task WingetReturnsZeroButNoService_ReportsServiceMissing()
    {
        var system = new FakeSystem { Winget = AdminWinget, RunExitCode = 0, ServiceAfterInstall = false };

        var code = await new PawnIOInstaller(system).RunAsync();

        Assert.Equal(PawnIOExit.ServiceMissing, code);
    }

    [Fact]
    public async Task NoWinget_AndNoDownload_ReportsDownloadFailed_AndRunsNothing()
    {
        var system = new FakeSystem { Winget = null, DownloadFails = true };

        var code = await new PawnIOInstaller(system).RunAsync();

        Assert.Equal(PawnIOExit.DownloadFailed, code);
        Assert.Equal(1, system.Downloads);
        Assert.Empty(system.Runs);
    }

    [Fact]
    public async Task BadSignature_ReportsBadSignature_RunsNothing_AndDeletesTheFile()
    {
        var system = new FakeSystem { Winget = null, Signed = false };

        var code = await new PawnIOInstaller(system).RunAsync();

        Assert.Equal(PawnIOExit.BadSignature, code);
        Assert.Equal([Setup], system.Checked);
        Assert.Empty(system.Runs);
        Assert.NotNull(system.Download);
        Assert.True(system.Download!.Deleted);
        Assert.False(system.Download.Held);
    }

    [Fact]
    public async Task GoodSignature_RunsTheSetupSilently()
    {
        var system = new FakeSystem { Winget = null, Signed = true, RunExitCode = 0, ServiceAfterInstall = true };

        var code = await new PawnIOInstaller(system).RunAsync();

        Assert.Equal(PawnIOExit.Installed, code);
        var run = Assert.Single(system.Runs);
        Assert.Equal(Setup, run.File);
        Assert.Equal("-install -silent", run.Arguments);
    }

    [Fact]
    public async Task TheDownloadStaysLocked_FromTheCheckToTheRun_AndIsDeletedAfter()
    {
        var system = new FakeSystem { Winget = null, Signed = true, RunExitCode = 0, ServiceAfterInstall = true };

        await new PawnIOInstaller(system).RunAsync();

        Assert.Equal([true], system.HeldAtCheck);  // checked while held
        Assert.Equal([true], system.HeldAtRun);    // run while held: nothing could have changed it since the check
        Assert.NotNull(system.Download);
        Assert.False(system.Download!.Held);       // released once the run is over
        Assert.True(system.Download.Deleted);
    }

    [Fact]
    public async Task SetupFails_ReportsInstallerFailed_AndStillReleasesTheFile()
    {
        var system = new FakeSystem { Winget = null, Signed = true, RunExitCode = 3 };

        var code = await new PawnIOInstaller(system).RunAsync();

        Assert.Equal(PawnIOExit.InstallerFailed, code);
        Assert.True(system.Download!.Deleted);
    }

    [Fact]
    public void NewestWingetFolder_PicksTheHighestVersion_AndIgnoresOthers()
    {
        var folders = new[]
        {
            "Microsoft.DesktopAppInstaller_1.9.0.0_x64__8wekyb3d8bbwe",
            "Microsoft.DesktopAppInstaller_1.25.0.0_x64__8wekyb3d8bbwe",
            "Microsoft.DesktopAppInstaller_1.21.2.0_x64__8wekyb3d8bbwe",
            "Microsoft.DesktopAppInstaller_9.9.9.9_arm64__8wekyb3d8bbwe",
            "Microsoft.WindowsTerminal_1.99.0.0_x64__8wekyb3d8bbwe",
        };

        Assert.Equal("Microsoft.DesktopAppInstaller_1.25.0.0_x64__8wekyb3d8bbwe", PawnIOInstaller.NewestWingetFolder(folders));
        Assert.Null(PawnIOInstaller.NewestWingetFolder(["Microsoft.WindowsTerminal_1.99.0.0_x64__8wekyb3d8bbwe"]));
    }

    [Fact]
    public void SetupUrl_TakesTheSetupAssetOverHttps()
    {
        const string json = """
            {
              "tag_name": "v0.1",
              "assets": [
                { "name": "PawnIO_setup.exe.sig", "browser_download_url": "https://github.com/x/sig" },
                { "name": "PawnIO_setup.exe", "browser_download_url": "https://github.com/x/PawnIO_setup.exe" }
              ]
            }
            """;

        Assert.Equal("https://github.com/x/PawnIO_setup.exe", WindowsPawnIOSystem.SetupUrl(json));
    }

    [Fact]
    public void SetupUrl_RefusesAPlainHttpAddress()
    {
        const string json = """{ "assets": [ { "name": "PawnIO_setup.exe", "browser_download_url": "http://example.test/s.exe" } ] }""";

        Assert.Null(WindowsPawnIOSystem.SetupUrl(json));
    }
}
