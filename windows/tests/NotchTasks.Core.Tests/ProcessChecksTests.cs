using System.Diagnostics;
using NotchTasks.Core;
using Xunit;

namespace NotchTasks.Core.Tests;

// The real Windows calls behind ProcessChecks: our own process, a child we start, and a process that has exited.
public sealed class ProcessChecksTests
{
    private static string OwnFileName => Path.GetFileName(Environment.ProcessPath!);

    /// The pid of a process that has already exited.
    private static int DeadPid()
    {
        using var p = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        p.WaitForExit();
        return p.Id;
    }

    [Fact]
    public void ImageName_OwnProcess_IsItsExeFileName()
    {
        var name = ProcessChecks.ImageName(Environment.ProcessId);

        Assert.Equal(OwnFileName, name, ignoreCase: true);
    }

    [Fact]
    public void RunsAs_OwnProcess_MatchesItsFileNameOnly()
    {
        Assert.True(ProcessChecks.RunsAs(Environment.ProcessId, [OwnFileName.ToUpperInvariant()]));
        Assert.False(ProcessChecks.RunsAs(Environment.ProcessId, ["NotchTasks.exe"]));
    }

    [Fact]
    public void RunsAs_LiveOtherProgram_IsNotNotchTasks()
    {
        using var child = Process.Start(new ProcessStartInfo("ping.exe", "-n 60 127.0.0.1")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        try
        {
            Assert.True(ProcessChecks.IsAlive(child.Id));
            Assert.Equal("ping.exe", ProcessChecks.ImageName(child.Id), ignoreCase: true);
            Assert.False(ProcessChecks.RunsAs(child.Id, ["NotchTasks.exe", "NotchTasksHook.exe"]));
            Assert.True(ProcessChecks.RunsAs(child.Id, ["PING.EXE"]));
        }
        finally
        {
            child.Kill();
        }
    }

    [Fact]
    public void RunsAs_ExitedProcess_IsFalse()
    {
        var pid = DeadPid();

        Assert.False(ProcessChecks.RunsAs(pid, ["NotchTasks.exe", "NotchTasksHook.exe"]));
        Assert.Null(ProcessChecks.ImageName(pid));
    }
}
