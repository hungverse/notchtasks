using NotchTasks.Core.Sensors;
using Xunit;

namespace NotchTasks.Core.Tests;

/// The PawnIO step's exit codes and the helper's command lines that run it first.
public sealed class PawnIOExitTests
{
    private const string Sid = "S-1-5-21-1111111111-2222222222-3333333333-1001";

    [Fact]
    public void EveryCode_HasItsOwnReadableLine()
    {
        var codes = new[]
        {
            PawnIOExit.Installed, PawnIOExit.WingetFailed, PawnIOExit.DownloadFailed,
            PawnIOExit.BadSignature, PawnIOExit.InstallerFailed, PawnIOExit.ServiceMissing,
        };

        Assert.Equal(codes.Length, codes.Distinct().Count());
        foreach (var code in codes)
        {
            Assert.False(string.IsNullOrWhiteSpace(PawnIOExit.Message(code)));
        }
        Assert.Contains("signature", PawnIOExit.Message(PawnIOExit.BadSignature));
    }

    [Fact]
    public void InstallPawnIO_AloneIsItsOwnMode()
    {
        var command = SensorArgs.Parse(["--install-pawnio"]);

        Assert.Equal(SensorMode.InstallPawnIO, command?.Mode);
    }

    [Fact]
    public void WithPawnIO_RunsTheStepBeforeAServe()
    {
        var args = SensorArgs.WithPawnIO(SensorArgs.Serve(Sid, 4242));

        Assert.Equal("--install-pawnio --serve --parent 4242 --user " + Sid, args);
        var command = SensorArgs.Parse(args.Split(' '));
        Assert.Equal(SensorMode.Serve, command?.Mode);
        Assert.True(command?.InstallPawnIO);
        Assert.Equal(Sid, command?.Sid);
        Assert.Equal(4242, command?.Parent);
    }

    [Fact]
    public void WithPawnIO_RunsTheStepBeforeAnInstallLogon()
    {
        var command = SensorArgs.Parse(SensorArgs.WithPawnIO(SensorArgs.InstallLogon(Sid, null)).Split(' '));

        Assert.Equal(SensorMode.InstallLogon, command?.Mode);
        Assert.True(command?.InstallPawnIO);
    }

    [Fact]
    public void PawnIOBeforeAnythingElse_IsNotACommand()
    {
        Assert.Null(SensorArgs.Parse(["--install-pawnio", "--uninstall-logon"]));
        Assert.Null(SensorArgs.Parse(["--install-pawnio", "--uninstall-own"]));
    }

    [Fact]
    public void PlainServe_DoesNotRunThePawnIOStep()
    {
        var command = SensorArgs.Parse(["--serve", "--user", Sid]);

        Assert.False(command?.InstallPawnIO);
    }
}
