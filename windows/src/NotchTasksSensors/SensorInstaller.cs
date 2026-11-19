using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using NotchTasks.Core.Sensors;

namespace NotchTasksSensors;

/// The elevated install and uninstall of a user's sign-in helper. The logon task runs the copy in Program Files as SYSTEM, and never
/// the copy in the app's own folder, which a user can write to. The shared folder holds one marker per user, "<SID>.signin": install
/// writes it, uninstall deletes it, and the files go only with the last marker. Nothing here runs from tests or from the app without
/// a UAC click.
internal static class SensorInstaller
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(10);

    /// The shared folder the logon tasks run from.
    public static string TargetFolder => SensorNames.ProgramFilesFolder;

    /// Installs this user's sign-in helper: the files (unless another user's helper runs from them), this user's task (as SYSTEM,
    /// at this user's logon), the marker, then a start of the task. Any failure undoes what this install created. With no zones
    /// there is nothing to install, so this process serves once (the app then learns there are no zones).
    public static async Task<int> InstallLogonAsync(string sid, int? parent)
    {
        var reader = new WmiThermalReader();
        if (reader.Read().Count == 0)
        {
            return await SensorHost.ServeAsync(sid, parent, reader);
        }

        var target = TargetFolder;
        var folderExisted = Directory.Exists(target);
        var taskRegistered = false;
        var markerWritten = false;
        try
        {
            EnsureInheritedFolder(target);
            SignalStopAndWait(sid);  // this user's helper, if any, releases its files
            if (!OtherHelpersRunning(target, sid))
            {
                CopyWhenFree(AppContext.BaseDirectory, target);
            }

            // The XML sits in the admin-only folder, so no user can change the task definition between writing and registering.
            var xmlPath = Path.Combine(target, sid + ".task.xml");
            var exePath = Path.Combine(target, SensorNames.ExeName);
            File.WriteAllText(xmlPath, SensorTask.Xml(sid, exePath, SensorArgs.Serve(sid, parent: null)), Encoding.Unicode);
            try
            {
                RunSchtasks("/Create", "/TN", SensorNames.TaskName(sid), "/XML", xmlPath, "/F");
                taskRegistered = true;
            }
            finally
            {
                File.Delete(xmlPath);
            }

            File.WriteAllText(SensorNames.MarkerPath(sid), string.Empty);
            markerWritten = true;
            RunSchtasks("/Run", "/TN", SensorNames.TaskName(sid));  // starts the copy as SYSTEM, the same way logon will
            return 0;
        }
        catch (Exception e)
        {
            HelperLog.Once(e);
            Rollback(sid, target, folderExisted, taskRegistered, markerWritten);
            return 1;
        }
    }

    /// Removes this user's sign-in helper: the task first (so no logon starts it), then the helper of this user, then the marker.
    /// The files and the folder go only when no marker remains, so another user's helper keeps working. Exit code 0 when done.
    public static async Task<int> UninstallLogonAsync(string? sid)
    {
        if (sid is null)
        {
            return 2;
        }

        var taskName = SensorNames.TaskName(sid);
        if (TaskExists(taskName))
        {
            RunSchtasks("/Delete", "/TN", taskName, "/F");
        }
        if (TaskExists(taskName))
        {
            return 1;  // still registered: nothing else is removed, so the task still has its copy
        }

        SignalStopAndWait(sid);
        File.Delete(SensorNames.MarkerPath(sid));

        var target = TargetFolder;
        if (AnyMarker(target) || !Directory.Exists(target))
        {
            return 0;
        }
        for (var attempt = 0; attempt < 20 && Directory.Exists(target); attempt++)
        {
            try
            {
                Directory.Delete(target, recursive: true);
            }
            catch (IOException)
            {
                await Task.Delay(250);
            }
            catch (UnauthorizedAccessException)
            {
                await Task.Delay(250);
            }
        }
        return Directory.Exists(target) ? 1 : 0;
    }

    /// The folder exists and keeps the permissions it inherits from Program Files. A new folder inherits them. An existing folder
    /// with its own rules, or with write rights for ordinary users (SensorFolderRules), is refused. Nothing is loosened here.
    internal static void EnsureInheritedFolder(string target)
    {
        Directory.CreateDirectory(target);
        var acl = new DirectoryInfo(target).GetAccessControl();
        if (acl.AreAccessRulesProtected)
        {
            throw new InvalidOperationException("The helper folder has its own permissions: " + target);
        }
        foreach (var rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>())
        {
            if (rule.IdentityReference is SecurityIdentifier sid
                && SensorFolderRules.IsOrdinaryWrite(sid.Value, rule.AccessControlType, rule.FileSystemRights))
            {
                throw new InvalidOperationException("The helper folder is writable by ordinary users: " + target);
            }
        }
    }

    /// Undoes what a failed install created: this user's task and marker, and the folder when the install created it and no other
    /// user's marker is there. Files copied into a folder that already existed are left alone: another user's helper may run them.
    private static void Rollback(string sid, string target, bool folderExisted, bool taskRegistered, bool markerWritten)
    {
        try
        {
            if (taskRegistered && TaskExists(SensorNames.TaskName(sid)))
            {
                RunSchtasks("/Delete", "/TN", SensorNames.TaskName(sid), "/F");
            }
            if (markerWritten)
            {
                File.Delete(SensorNames.MarkerPath(sid));
            }
            if (!folderExisted && !AnyMarker(target) && Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            HelperLog.Once(e);
        }
    }

    /// True when any user's marker is in the folder.
    private static bool AnyMarker(string target) =>
        Directory.Exists(target) && Directory.EnumerateFiles(target, "*.signin").Any();

    /// True when another user's helper is running: its stop event is held (the helper holds it for its whole life). Read this way,
    /// the check takes nothing from the other helper's pipe.
    private static bool OtherHelpersRunning(string target, string sid)
    {
        if (!Directory.Exists(target))
        {
            return false;
        }
        foreach (var marker in Directory.EnumerateFiles(target, "*.signin"))
        {
            var other = Path.GetFileNameWithoutExtension(marker);
            if (other == sid || !SensorNames.IsSid(other))
            {
                continue;
            }
            if (EventWaitHandleAcl.TryOpenExisting(SensorNames.StopEventName(other), EventWaitHandleRights.Synchronize, out var holder))
            {
                holder.Dispose();
                return true;
            }
        }
        return false;
    }

    /// Copies the helper's files, retrying while the copy in the folder is still running.
    private static void CopyWhenFree(string source, string target)
    {
        var deadline = DateTime.UtcNow + StopTimeout;
        while (true)
        {
            try
            {
                SensorFiles.CopyFrom(source, target);
                return;
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(200);
            }
        }
    }

    /// Sets this user's stop event, then waits until no process holds it any more.
    private static void SignalStopAndWait(string sid)
    {
        var name = SensorNames.StopEventName(sid);
        if (EventWaitHandleAcl.TryOpenExisting(name, EventWaitHandleRights.Modify, out var stop))
        {
            stop.Set();
            stop.Dispose();
        }

        var deadline = DateTime.UtcNow + StopTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!EventWaitHandleAcl.TryOpenExisting(name, EventWaitHandleRights.Synchronize, out var holder))
            {
                return;
            }
            holder.Dispose();
            Thread.Sleep(200);
        }
        throw new TimeoutException("The sensor helper did not stop.");
    }

    private static bool TaskExists(string taskName) => RunSchtasksCode("/Query", "/TN", taskName) == 0;

    private static void RunSchtasks(params string[] args)
    {
        var code = RunSchtasksCode(args);
        if (code != 0)
        {
            throw new InvalidOperationException($"schtasks {args[0]} failed with code {code}.");
        }
    }

    private static int RunSchtasksCode(params string[] args)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }
        using var process = Process.Start(start) ?? throw new InvalidOperationException("schtasks did not start.");
        process.WaitForExit();
        return process.ExitCode;
    }
}
