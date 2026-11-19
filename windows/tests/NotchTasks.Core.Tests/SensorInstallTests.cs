using NotchTasks.Core.Sensors;
using Xunit;

namespace NotchTasks.Core.Tests;

/// The install files and the task definition. Only temp folders are written; no task is created.
public sealed class SensorInstallTests : IDisposable
{
    private const string Sid = "S-1-5-21-1111111111-2222222222-3333333333-1001";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nt-sensors-" + Guid.NewGuid().ToString("N"));

    public SensorInstallTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public void CopyFiles_CopiesOnlyTheHelperFiles()
    {
        var source = Path.Combine(_dir, "src");
        var target = Path.Combine(_dir, "dst");
        Directory.CreateDirectory(source);
        foreach (var name in new[]
        {
            "NotchTasksSensors.exe", "NotchTasksSensors.dll", "NotchTasksSensors.runtimeconfig.json",
            "NotchTasksSensors.deps.json", "NotchTasks.Core.dll", "System.Management.dll", "NotchTasks.exe", "payload.dll",
        })
        {
            File.WriteAllText(Path.Combine(source, name), name);
        }
        // The runtime-specific copy the deps file points at, which must keep its folders.
        Directory.CreateDirectory(Path.Combine(source, "runtimes", "win", "lib", "net10.0"));
        File.WriteAllText(Path.Combine(source, "runtimes", "win", "lib", "net10.0", "System.Management.dll"), "rt");

        var copied = SensorFiles.CopyFrom(source, target);

        Assert.Equal(7, copied.Count);
        Assert.True(File.Exists(Path.Combine(target, "NotchTasksSensors.exe")));
        Assert.True(File.Exists(Path.Combine(target, "NotchTasks.Core.dll")));
        Assert.True(File.Exists(Path.Combine(target, "System.Management.dll")));
        Assert.True(File.Exists(Path.Combine(target, "runtimes", "win", "lib", "net10.0", "System.Management.dll")));
        Assert.False(File.Exists(Path.Combine(target, "NotchTasks.exe")));
        Assert.False(File.Exists(Path.Combine(target, "payload.dll")));
    }

    [Fact]
    public void CopyFiles_WithoutTheExe_Throws()
    {
        var source = Path.Combine(_dir, "empty");
        Directory.CreateDirectory(source);

        Assert.ThrowsAny<IOException>(() => SensorFiles.CopyFrom(source, Path.Combine(_dir, "dst2")));
    }

    [Fact]
    public void LogonTask_RunsTheGivenExeAtLogonForThatUserOnly_WithHighestPrivileges()
    {
        var xml = SensorTask.Xml(Sid, @"C:\Program Files\NotchTasks Sensors\NotchTasksSensors.exe",
            SensorArgs.Serve(Sid, parent: null));

        Assert.Contains("<LogonTrigger>", xml);
        Assert.Contains($"<UserId>{Sid}</UserId>", xml);
        Assert.Contains("<RunLevel>HighestAvailable</RunLevel>", xml);
        Assert.Contains(@"<Command>C:\Program Files\NotchTasks Sensors\NotchTasksSensors.exe</Command>", xml);
        Assert.Contains($"<Arguments>--serve --user {Sid}</Arguments>", xml);
        Assert.Contains("<ExecutionTimeLimit>PT0S</ExecutionTimeLimit>", xml);
    }

    [Fact]
    public void LogonTask_EscapesMarkupInTheCommand()
    {
        var xml = SensorTask.Xml(Sid, @"C:\a&b<c>.exe", "--serve");

        Assert.Contains(@"<Command>C:\a&amp;b&lt;c&gt;.exe</Command>", xml);
        Assert.DoesNotContain("<c>", xml);
    }

    [Theory]
    [InlineData("S-1-5-21-1-2-3-1001", true)]
    [InlineData("S-1-5-18", true)]
    [InlineData("S-1-5-21 & calc", false)]
    [InlineData("S-1-5-21-1\"", false)]
    [InlineData("", false)]
    public void IsSid_OnlyAcceptsSidText(string text, bool expected) =>
        Assert.Equal(expected, SensorNames.IsSid(text));
}
