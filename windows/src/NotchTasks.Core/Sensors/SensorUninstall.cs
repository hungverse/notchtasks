using System.IO;

namespace NotchTasks.Core.Sensors;

/// The side effects of an uninstall: schtasks, the stop event and the file system. The helper's real implementation calls the
/// system; tests pass a fake that records the order of the calls.
public interface ISensorSystem
{
    /// Runs schtasks with the arguments. Returns its exit code.
    int Schtasks(params string[] args);

    /// True when the task is registered.
    bool TaskExists(string taskName);

    /// Sets the user's stop event (no-op when no helper holds it).
    void SignalStop(string sid);

    /// The full paths of the files in the folder (none when the folder is gone).
    IReadOnlyList<string> FilesIn(string folder);

    /// Deletes the file, retrying while it is in use. True when the file is gone afterwards.
    bool TryDeleteFile(string path);

    /// Deletes the folder when it is empty. True when the folder is gone afterwards.
    bool TryDeleteFolder(string folder);
}

/// How an uninstall ended.
public enum UninstallOutcome
{
    /// The task is gone, and this user's files and marker are removed (the files only with the last marker).
    Removed,

    /// The task could not be deleted: nothing else was touched.
    TaskStillRegistered,

    /// A file is in use or could not be deleted: the marker is kept, so the row still offers Turn off and the user can retry.
    FilesStillInUse,
}

/// The uninstall of one user's sign-in helper, in this order:
///   1. stop the task's running copy (schtasks /End; "not running" is fine),
///   2. delete the task (schtasks /Delete /F), and stop if it is still there,
///   3. set the user's stop event,
///   4. with other users' markers still in the folder: delete this user's marker and stop there,
///   5. with no other marker: delete every other file in the folder, then delete this user's marker LAST, then the folder.
/// If a file cannot be deleted in step 5, the marker stays, so the next Turn off finds the install and tries again.
public static class SensorUninstall
{
    public static UninstallOutcome Run(string sid, string folder, ISensorSystem system)
    {
        var task = SensorNames.TaskName(sid);
        var own = Path.Combine(folder, SensorNames.MarkerName(sid));

        system.Schtasks("/End", "/TN", task);
        system.Schtasks("/Delete", "/TN", task, "/F");
        if (system.TaskExists(task))
        {
            return UninstallOutcome.TaskStillRegistered;
        }

        system.SignalStop(sid);

        var others = system.FilesIn(folder)
            .Any(file => IsMarker(file) && !string.Equals(file, own, StringComparison.OrdinalIgnoreCase));
        if (others)
        {
            return system.TryDeleteFile(own) ? UninstallOutcome.Removed : UninstallOutcome.FilesStillInUse;
        }

        foreach (var file in system.FilesIn(folder))
        {
            if (string.Equals(file, own, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (!system.TryDeleteFile(file))
            {
                return UninstallOutcome.FilesStillInUse;  // the marker stays: the files are not all gone
            }
        }

        if (!system.TryDeleteFile(own))
        {
            return UninstallOutcome.FilesStillInUse;
        }
        system.TryDeleteFolder(folder);
        return UninstallOutcome.Removed;
    }

    /// True for a user's marker file name ("<SID>.signin").
    private static bool IsMarker(string path) => path.EndsWith(".signin", StringComparison.OrdinalIgnoreCase);
}
