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
    private static readonly string Runtimes = Path.Combine(Folder, "runtimes");
    private static readonly string RuntimesWin = Path.Combine(Runtimes, "win");
    private static readonly string RuntimesLib = Path.Combine(RuntimesWin, "lib");
    private static readonly string Nested = Path.Combine(RuntimesLib, "System.Management.dll");
    private static readonly string Other = Path.Combine(Folder, OtherSid + ".signin");

    private sealed class FakeSystem : ISensorSystem
    {
        public readonly List<string> Log = new();
        public readonly List<string> Files = new();
        public readonly List<string> Folders = new();
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
            Log.Add("list-files");
            return Files.ToList();
        }

        public IReadOnlyList<string> FoldersIn(string folder)
        {
            Log.Add("list-folders");
            return Folders.ToList();
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

        public bool TryDeleteFolder(string path)
        {
            Log.Add("folder " + Path.GetFileName(path));
            if (FailingDeletes.Contains(path))
            {
                return false;
            }
            Folders.Remove(path);
            return true;
        }

        public int IndexOfLast(string entry) => Log.FindLastIndex(line => line == entry);
    }

    private static FakeSystem NewSystem(IEnumerable<string> files, IEnumerable<string>? folders = null)
    {
        var system = new FakeSystem();
        system.Files.AddRange(files);
        system.Folders.AddRange(folders ?? Array.Empty<string>());
        return system;
    }

    private static FakeSystem Install() =>
        NewSystem(new[] { Exe, CoreDll, Nested, Own }, new[] { Runtimes, RuntimesWin, RuntimesLib });

    [Fact]
    public void Order_EndThenDelete_ThenStop_ThenTree_ThenMarkerLast_ThenFolder()
    {
        var system = Install();

        var outcome = SensorUninstall.Run(Sid, Folder, system);

        Assert.Equal(UninstallOutcome.Removed, outcome);
        var log = system.Log;
        var end = log.IndexOf("schtasks /End");
        var delete = log.IndexOf("schtasks /Delete");
        var stop = log.IndexOf("stop " + Sid);
        Assert.True(end >= 0 && end < delete, "the running copy ends before the task is deleted");
        Assert.True(delete < stop, "the task is deleted before the stop");
        Assert.True(stop < log.IndexOf("delete NotchTasksSensors.exe"), "the files go after the stop");
        Assert.True(log.IndexOf("delete NotchTasks.Core.dll") < log.IndexOf("folder runtimes"), "files go before their folders");
        Assert.True(log.IndexOf("folder runtimes") < system.IndexOfLast("delete " + Path.GetFileName(Own)), "the marker is last");
        Assert.True(system.IndexOfLast("delete " + Path.GetFileName(Own)) < log.IndexOf("folder NotchTasks Sensors"),
            "the folder itself goes after the marker");
        Assert.Empty(system.Files);
        Assert.Empty(system.Folders);
    }

    [Fact]
    public void NestedRuntimesFile_IsDeleted_BeforeTheFoldersAndTheMarker()
    {
        var system = Install();

        Assert.Equal(UninstallOutcome.Removed, SensorUninstall.Run(Sid, Folder, system));

        Assert.Contains("delete System.Management.dll", system.Log);
        Assert.True(system.Log.IndexOf("delete System.Management.dll") < system.Log.IndexOf("folder lib"));
        Assert.DoesNotContain(Nested, system.Files);
    }

    [Fact]
    public void FoldersGoDeepestFirst()
    {
        var system = Install();

        SensorUninstall.Run(Sid, Folder, system);

        var lib = system.Log.IndexOf("folder lib");
        var win = system.Log.IndexOf("folder win");
        var runtimes = system.Log.IndexOf("folder runtimes");
        Assert.True(lib < win && win < runtimes);
    }

    [Fact]
    public void FileInUse_KeepsTheMarker_AndReportsIt()
    {
        var system = Install();
        system.FailingDeletes.Add(Exe);

        var outcome = SensorUninstall.Run(Sid, Folder, system);

        Assert.Equal(UninstallOutcome.FilesStillInUse, outcome);
        Assert.DoesNotContain("delete " + Path.GetFileName(Own), system.Log);
        Assert.Contains(Own, system.Files);
    }

    [Fact]
    public void NestedFileInUse_KeepsTheMarker_AndTheFolders()
    {
        // A file deep in runtimes\ cannot be deleted: nothing from the marker on is touched.
        var system = Install();
        system.FailingDeletes.Add(Nested);

        var outcome = SensorUninstall.Run(Sid, Folder, system);

        Assert.Equal(UninstallOutcome.FilesStillInUse, outcome);
        Assert.DoesNotContain("delete " + Path.GetFileName(Own), system.Log);
        Assert.DoesNotContain("folder NotchTasks Sensors", system.Log);
        Assert.Contains(Own, system.Files);
        Assert.Contains(Nested, system.Files);
    }

    [Fact]
    public void OtherUsersMarker_KeepsTheFiles_AndRemovesOnlyThisMarker()
    {
        var system = NewSystem(new[] { Exe, CoreDll, Nested, Own, Other }, new[] { Runtimes, RuntimesWin, RuntimesLib });

        var outcome = SensorUninstall.Run(Sid, Folder, system);

        Assert.Equal(UninstallOutcome.Removed, outcome);
        Assert.DoesNotContain("delete NotchTasksSensors.exe", system.Log);
        Assert.DoesNotContain("delete System.Management.dll", system.Log);
        Assert.DoesNotContain("folder runtimes", system.Log);
        Assert.Contains("delete " + Path.GetFileName(Own), system.Log);
        Assert.DoesNotContain("folder NotchTasks Sensors", system.Log);
        Assert.Contains(Other, system.Files);
        Assert.Contains(Exe, system.Files);
    }

    [Fact]
    public void TaskNotDeleted_ChangesNothingElse()
    {
        var system = Install();
        system.DeleteTaskFails = true;

        var outcome = SensorUninstall.Run(Sid, Folder, system);

        Assert.Equal(UninstallOutcome.TaskStillRegistered, outcome);
        Assert.DoesNotContain("stop " + Sid, system.Log);
        Assert.DoesNotContain("delete " + Path.GetFileName(Own), system.Log);
        Assert.Contains(Exe, system.Files);
    }
}
