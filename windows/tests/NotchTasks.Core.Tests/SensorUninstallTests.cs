using NotchTasks.Core.Sensors;
using Xunit;

namespace NotchTasks.Core.Tests;

/// The uninstall's order and its marker-last rule, through a fake that records every call. No schtasks, no file system.
public sealed class SensorUninstallTests
{
    private const string Sid = "S-1-5-21-1111111111-2222222222-3333333333-1001";
    private const string OtherSid = "S-1-5-21-1111111111-2222222222-3333333333-1002";
    private static readonly string Folder = Path.Combine("C:", "fake", "NotchTasks Sensors");
    private static readonly string Own = Path.Combine(Folder, Sid + ".signin");
    private static readonly string Exe = Path.Combine(Folder, "NotchTasksSensors.exe");
    private static readonly string CoreDll = Path.Combine(Folder, "NotchTasks.Core.dll");
    private static readonly string Other = Path.Combine(Folder, OtherSid + ".signin");

    private sealed class FakeSystem : ISensorSystem
    {
        public readonly List<string> Log = new();
        public readonly List<string> Files = new();
        public readonly HashSet<string> FailingDeletes = new(StringComparer.OrdinalIgnoreCase);
        public bool TaskPresent = true;
        public bool DeleteTaskFails;

        public int Schtasks(params string[] args)
        {
            Log.Add("schtasks " + args[0]);
            if (args[0] == "/Delete" && !DeleteTaskFails)
            {
                TaskPresent = false;
            }
            return 0;
        }

        public bool TaskExists(string taskName)
        {
            Log.Add("task-exists");
            return TaskPresent;
        }

        public void SignalStop(string sid) => Log.Add("stop " + sid);

        public IReadOnlyList<string> FilesIn(string folder)
        {
            Log.Add("list");
            return Files.ToList();
        }

        public bool TryDeleteFile(string path)
        {
            Log.Add("delete " + Path.GetFileName(path));
            if (FailingDeletes.Contains(path))
            {
                return false;
            }
            Files.Remove(path);
            return true;
        }

        public bool TryDeleteFolder(string folder)
        {
            Log.Add("folder");
            return true;
        }

        public int IndexOfLast(string entry) => Log.FindLastIndex(line => line == entry);
    }

    private static FakeSystem NewSystem(params string[] files)
    {
        var system = new FakeSystem();
        system.Files.AddRange(files);
        return system;
    }

    [Fact]
    public void Order_EndThenDeleteThenStop_ThenFiles_ThenMarkerLast()
    {
        var system = NewSystem(Exe, CoreDll, Own);

        var outcome = SensorUninstall.Run(Sid, Folder, system);

        Assert.Equal(UninstallOutcome.Removed, outcome);
        var log = system.Log;
        var end = log.IndexOf("schtasks /End");
        var delete = log.IndexOf("schtasks /Delete");
        var stop = log.IndexOf("stop " + Sid);
        Assert.True(end >= 0 && end < delete, "the running copy ends before the task is deleted");
        Assert.True(delete < stop, "the task is deleted before the stop");
        Assert.True(stop < log.IndexOf("delete NotchTasksSensors.exe"), "the files go after the stop");
        Assert.True(log.IndexOf("delete NotchTasksSensors.exe") < log.IndexOf("delete NotchTasks.Core.dll"));
        // The marker is the last file deleted, and the folder goes after it.
        Assert.True(system.IndexOfLast("delete " + Path.GetFileName(Own)) > system.IndexOfLast("delete NotchTasks.Core.dll"));
        Assert.True(log.IndexOf("folder") > system.IndexOfLast("delete " + Path.GetFileName(Own)));
        Assert.Empty(system.Files);
    }

    [Fact]
    public void FileInUse_KeepsTheMarker_AndReportsIt()
    {
        var system = NewSystem(Exe, CoreDll, Own);
        system.FailingDeletes.Add(Exe);

        var outcome = SensorUninstall.Run(Sid, Folder, system);

        Assert.Equal(UninstallOutcome.FilesStillInUse, outcome);
        Assert.DoesNotContain("delete " + Path.GetFileName(Own), system.Log);
        Assert.Contains(Own, system.Files);
        Assert.DoesNotContain("folder", system.Log);
    }

    [Fact]
    public void OtherUsersMarker_KeepsTheFiles_AndRemovesOnlyThisMarker()
    {
        var system = NewSystem(Exe, CoreDll, Own, Other);

        var outcome = SensorUninstall.Run(Sid, Folder, system);

        Assert.Equal(UninstallOutcome.Removed, outcome);
        Assert.DoesNotContain("delete NotchTasksSensors.exe", system.Log);
        Assert.DoesNotContain("delete NotchTasks.Core.dll", system.Log);
        Assert.Contains("delete " + Path.GetFileName(Own), system.Log);
        Assert.DoesNotContain("folder", system.Log);
        Assert.Contains(Other, system.Files);
        Assert.Contains(Exe, system.Files);
    }

    [Fact]
    public void TaskNotDeleted_ChangesNothingElse()
    {
        var system = NewSystem(Exe, Own);
        system.DeleteTaskFails = true;

        var outcome = SensorUninstall.Run(Sid, Folder, system);

        Assert.Equal(UninstallOutcome.TaskStillRegistered, outcome);
        Assert.DoesNotContain("stop " + Sid, system.Log);
        Assert.DoesNotContain("delete " + Path.GetFileName(Own), system.Log);
        Assert.Contains(Exe, system.Files);
    }

    [Fact]
    public void MarkerDeletesLast_AndNeverFirst()
    {
        // Even with no other file, the marker is not the first delete.
        var system = NewSystem(Own);

        Assert.Equal(UninstallOutcome.Removed, SensorUninstall.Run(Sid, Folder, system));
        Assert.Equal("delete " + Path.GetFileName(Own), system.Log.Last(line => line.StartsWith("delete ", StringComparison.Ordinal)));
    }
}
