using System.Security.AccessControl;
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
    public void CopyFiles_CopiesTheFilesTheDepsFileLists()
    {
        var source = Path.Combine(_dir, "self");
        var target = Path.Combine(_dir, "self-dst");
        Directory.CreateDirectory(source);
        foreach (var name in new[] { "NotchTasksSensors.exe", "NotchTasksSensors.dll", "NotchTasksSensors.runtimeconfig.json", "NotchTasks.Core.dll" })
        {
            File.WriteAllText(Path.Combine(source, name), name);
        }
        // The deps file: the runtime pack's native file, a package's managed file under lib\, and one it does not list (not copied).
        File.WriteAllText(Path.Combine(source, "NotchTasksSensors.deps.json"), """
            {
              "targets": {
                ".NETCoreApp,Version=v10.0/win-x64": {
                  "LibreHardwareMonitorLib/0.9.6": {
                    "runtime": { "runtimes/win-x64/lib/net10.0/LibreHardwareMonitorLib.dll": {} },
                    "native": { "runtimes/win-x64/native/MonoPosixHelper.dll": {} }
                  },
                  "Microsoft.NETCore.App/10.0.0": { "native": { "hostpolicy.dll": {} } }
                }
              }
            }
            """);
        File.WriteAllText(Path.Combine(source, "LibreHardwareMonitorLib.dll"), "lhm");
        File.WriteAllText(Path.Combine(source, "MonoPosixHelper.dll"), "mono");
        File.WriteAllText(Path.Combine(source, "hostpolicy.dll"), "host");
        File.WriteAllText(Path.Combine(source, "unlisted.dll"), "app");

        var copied = SensorFiles.CopyFrom(source, target);

        Assert.Contains("LibreHardwareMonitorLib.dll", copied);
        Assert.Contains("MonoPosixHelper.dll", copied);
        Assert.Contains("hostpolicy.dll", copied);
        Assert.True(File.Exists(Path.Combine(target, "hostpolicy.dll")));
        Assert.False(File.Exists(Path.Combine(target, "unlisted.dll")));
    }

    [Fact]
    public void DeclaredFiles_IsEmptyForAMissingOrBrokenDepsFile()
    {
        Assert.Empty(SensorFiles.DeclaredFiles(Path.Combine(_dir, "missing.json")));

        var broken = Path.Combine(_dir, "broken.json");
        File.WriteAllText(broken, "not json");

        Assert.Empty(SensorFiles.DeclaredFiles(broken));
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

    [Fact]
    public void LogonTask_RunsAsSystem_TriggeredByTheUsersLogon()
    {
        var xml = SensorTask.Xml(Sid, @"C:\Program Files\NotchTasks Sensors\NotchTasksSensors.exe", SensorArgs.Serve(Sid, null));

        // The principal is SYSTEM, never the user: no user environment reaches it.
        var principal = xml.Substring(xml.IndexOf("<Principals>", StringComparison.Ordinal));
        Assert.Contains("<UserId>S-1-5-18</UserId>", principal);
        Assert.Contains("<LogonType>ServiceAccount</LogonType>", principal);
        Assert.DoesNotContain("InteractiveToken", xml);
        Assert.Contains("<RunLevel>HighestAvailable</RunLevel>", principal);
        // The trigger fires only for the user's own logon: the user's SID sits inside the LogonTrigger element.
        var trigger = xml.Substring(xml.IndexOf("<LogonTrigger>", StringComparison.Ordinal),
            xml.IndexOf("</LogonTrigger>", StringComparison.Ordinal) - xml.IndexOf("<LogonTrigger>", StringComparison.Ordinal));
        Assert.Contains($"<UserId>{Sid}</UserId>", trigger);
    }

    [Fact]
    public void LogonTask_ActionIsTheAbsoluteProgramFilesPath()
    {
        var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NotchTasks Sensors", "NotchTasksSensors.exe");
        var xml = SensorTask.Xml(Sid, exe, SensorArgs.Serve(Sid, null));

        Assert.Contains($"<Command>{exe}</Command>", xml);
        Assert.Contains($"<Arguments>--serve --user {Sid}</Arguments>", xml);
    }

    [Fact]
    public void LogonTask_RejectsARelativeAction()
    {
        Assert.Throws<ArgumentException>(() => SensorTask.Xml(Sid, "NotchTasksSensors.exe", "--serve"));
    }

    [Fact]
    public void TaskAndMarkerNames_ArePerUser()
    {
        var other = "S-1-5-21-1111111111-2222222222-3333333333-1002";

        Assert.Equal("NotchTasks Sensors " + Sid, SensorNames.TaskName(Sid));
        Assert.NotEqual(SensorNames.TaskName(Sid), SensorNames.TaskName(other));
        Assert.Equal(Sid + ".signin", SensorNames.MarkerName(Sid));
        Assert.EndsWith(SensorNames.MarkerName(Sid), SensorNames.MarkerPath(Sid), StringComparison.Ordinal);
    }
}

public sealed class SensorFolderRulesTests
{
    private const string Users = "S-1-5-32-545";
    private const string AuthenticatedUsers = "S-1-5-11";
    private const string Everyone = "S-1-1-0";
    private const string Interactive = "S-1-5-4";
    private const string Administrators = "S-1-5-32-544";

    [Fact]
    public void UsersReadAndExecute_IsAllowed()
    {
        // Program Files gives Users:(RX). The read bits are not write-type bits, so the folder is kept.
        Assert.False(SensorFolderRules.IsOrdinaryWrite(Users, AccessControlType.Allow, FileSystemRights.ReadAndExecute));
        Assert.False(SensorFolderRules.GrantsWrite(FileSystemRights.ReadAndExecute));
    }

    [Fact]
    public void UsersModify_IsRejected()
    {
        Assert.True(SensorFolderRules.IsOrdinaryWrite(Users, AccessControlType.Allow, FileSystemRights.Modify));
        Assert.True(SensorFolderRules.GrantsWrite(FileSystemRights.Modify));
    }

    [Theory]
    [InlineData(AuthenticatedUsers, FileSystemRights.WriteData)]
    [InlineData(Everyone, FileSystemRights.AppendData)]
    [InlineData(Interactive, FileSystemRights.WriteAttributes)]
    [InlineData(Users, FileSystemRights.Delete)]
    [InlineData(Users, FileSystemRights.TakeOwnership)]
    public void OtherOrdinaryIdentities_WithAWriteRight_AreRejected(string sid, FileSystemRights rights) =>
        Assert.True(SensorFolderRules.IsOrdinaryWrite(sid, AccessControlType.Allow, rights));

    [Fact]
    public void DenyRules_AndAdministrators_AreNotOrdinaryWrite()
    {
        Assert.False(SensorFolderRules.IsOrdinaryWrite(Users, AccessControlType.Deny, FileSystemRights.Modify));
        Assert.False(SensorFolderRules.IsOrdinaryWrite(Administrators, AccessControlType.Allow, FileSystemRights.FullControl));
    }
}
