using NotchTasks.Core.Sensors;
using Xunit;

namespace NotchTasksSensors.Tests;

/// The PawnIO step over a fake machine: no service, no winget, no download, no process is started.
public sealed class PawnIOInstallerTests
{
    private const string Setup = @"C:\Temp\PawnIO_setup-test.exe";

    private sealed class FakeSystem : IPawnIOSystem
    {
        public bool ServiceAfterInstall { get; set; }
        public bool ServiceBefore { get; set; }
        public bool WingetOnPath { get; set; }
        public int RunExitCode { get; set; }
        public string? DownloadResult { get; set; } = Setup;
        public bool Signed { get; set; } = true;
        public List<(string File, string Arguments)> Runs { get; } = new();
        public int Downloads { get; private set; }
        public List<string> Discarded { get; } = new();
        public List<string> Checked { get; } = new();

        public bool ServiceExists(string name) => ServiceBefore || (Runs.Count > 0 && ServiceAfterInstall);

        public bool HasProgram(string fileName) => WingetOnPath && fileName == "winget.exe";

        public int Run(string fileName, string arguments)
        {
            Runs.Add((fileName, arguments));
            return RunExitCode;
        }

        public Task<string?> DownloadSetupAsync(CancellationToken ct)
        {
            Downloads++;
            return Task.FromResult(DownloadResult);
        }

        public bool HasValidSignature(string path)
        {
            Checked.Add(path);
            return Signed;
        }

        public void Discard(string path) => Discarded.Add(path);
    }

    [Fact]
    public async Task ServicePresent_InstallsNothing()
    {
        var system = new FakeSystem { ServiceBefore = true, WingetOnPath = true };

        var code = await new PawnIOInstaller(system).RunAsync();

        Assert.Equal(PawnIOExit.Installed, code);
        Assert.Empty(system.Runs);
        Assert.Equal(0, system.Downloads);
    }

    [Fact]
    public async Task WingetOnPath_RunsTheWingetCommandLineExactly()
    {
        var system = new FakeSystem { WingetOnPath = true, RunExitCode = 0, ServiceAfterInstall = true };

        var code = await new PawnIOInstaller(system).RunAsync();

        Assert.Equal(PawnIOExit.Installed, code);
        var run = Assert.Single(system.Runs);
        Assert.Equal("winget", run.File);
        Assert.Equal(
            "install --id namazso.PawnIO --exact --silent --scope machine --accept-package-agreements --accept-source-agreements",
            run.Arguments);
        Assert.Equal(0, system.Downloads);
    }

    [Fact]
    public async Task WingetFails_ReportsWingetFailed()
    {
        var system = new FakeSystem { WingetOnPath = true, RunExitCode = 1 };

        var code = await new PawnIOInstaller(system).RunAsync();

        Assert.Equal(PawnIOExit.WingetFailed, code);
    }

    [Fact]
    public async Task WingetReturnsZeroButNoService_ReportsServiceMissing()
    {
        var system = new FakeSystem { WingetOnPath = true, RunExitCode = 0, ServiceAfterInstall = false };

        var code = await new PawnIOInstaller(system).RunAsync();

        Assert.Equal(PawnIOExit.ServiceMissing, code);
    }

    [Fact]
    public async Task NoWinget_AndNoDownload_ReportsDownloadFailed_AndRunsNothing()
    {
        var system = new FakeSystem { WingetOnPath = false, DownloadResult = null };

        var code = await new PawnIOInstaller(system).RunAsync();

        Assert.Equal(PawnIOExit.DownloadFailed, code);
        Assert.Equal(1, system.Downloads);
        Assert.Empty(system.Runs);
    }

    [Fact]
    public async Task NoWinget_BadSignature_ReportsBadSignature_AndRunsNothing()
    {
        var system = new FakeSystem { WingetOnPath = false, Signed = false };

        var code = await new PawnIOInstaller(system).RunAsync();

        Assert.Equal(PawnIOExit.BadSignature, code);
        Assert.Equal([Setup], system.Checked);
        Assert.Empty(system.Runs);
        Assert.Equal([Setup], system.Discarded);
    }

    [Fact]
    public async Task NoWinget_GoodSignature_RunsTheSetupSilently()
    {
        var system = new FakeSystem { WingetOnPath = false, Signed = true, RunExitCode = 0, ServiceAfterInstall = true };

        var code = await new PawnIOInstaller(system).RunAsync();

        Assert.Equal(PawnIOExit.Installed, code);
        var run = Assert.Single(system.Runs);
        Assert.Equal(Setup, run.File);
        Assert.Equal("-install -silent", run.Arguments);
        Assert.Equal([Setup], system.Discarded);
    }

    [Fact]
    public async Task SetupFails_ReportsInstallerFailed()
    {
        var system = new FakeSystem { WingetOnPath = false, Signed = true, RunExitCode = 3 };

        var code = await new PawnIOInstaller(system).RunAsync();

        Assert.Equal(PawnIOExit.InstallerFailed, code);
        Assert.Equal([Setup], system.Discarded);
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
