using System.ComponentModel;
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
            // A reinstall for the same user: end this user's running copy (session 0, as SYSTEM) before its files are replaced.
            if (TaskExists(SensorNames.TaskName(sid)))
            {
                RunSchtasksCode("/End", "/TN", SensorNames.TaskName(sid));  // "not running" is fine
            }
            SignalStopAndWait(sid);  // this user's helper releases its files; the Global stop event reaches it from session 0
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

    /// Removes this user's sign-in helper through the tested sequence (SensorUninstall.Run). Exit code 0 when the task and the
    /// marker are removed; 1 otherwise, with the marker kept so Turn off can be tried again.
    public static Task<int> UninstallLogonAsync(string? sid)
    {
        if (sid is null)
        {
            return Task.FromResult(2);
        }
        var outcome = SensorUninstall.Run(sid, TargetFolder, new RealSensorSystem());
        return Task.FromResult(outcome == UninstallOutcome.Removed ? 0 : 1);
    }

    /// The installer's opt-in removal, run by the user's own process: when this user has a sign-in helper (their marker is there), one
    /// UAC prompt runs --uninstall-logon for them. Exit 0 when the removal succeeded or there was nothing to remove; 1 otherwise,
    /// including a UAC prompt the user cancelled.
    public static async Task<int> RemoveOwnSignInAsync()
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value;
        if (!SensorNames.IsSid(sid) || !File.Exists(SensorNames.MarkerPath(sid!)))
        {
            return 0;
        }
        var self = Environment.ProcessPath ?? throw new InvalidOperationException("The helper's path is unknown.");
        var start = new ProcessStartInfo(self)
        {
            UseShellExecute = true,
            Verb = "runas",
            Arguments = SensorArgs.UninstallLogonArgs(sid),
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        try
        {
            using var elevated = Process.Start(start) ?? throw new InvalidOperationException("The elevated uninstall did not start.");
            await elevated.WaitForExitAsync();
            return elevated.ExitCode == 0 ? 0 : 1;
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223)
        {
            return 1;  // the user said no to UAC: nothing changes
        }
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

    internal static int RunSchtasksCode(params string[] args)
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

/// The real side effects of an uninstall: schtasks from System32, the Global stop event, and the file system (with retries while a
/// file is still in use).
internal sealed class RealSensorSystem : ISensorSystem
{
    public int Schtasks(params string[] args) => SensorInstaller.RunSchtasksCode(args);

    public bool TaskExists(string taskName) => SensorInstaller.RunSchtasksCode("/Query", "/TN", taskName) == 0;

    public void SignalStop(string sid)
    {
        if (EventWaitHandleAcl.TryOpenExisting(SensorNames.StopEventName(sid), EventWaitHandleRights.Modify, out var stop))
        {
            stop.Set();
            stop.Dispose();
        }
    }

    public IReadOnlyList<string> FilesIn(string folder) =>
        Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).ToList() : Array.Empty<string>();

    public IReadOnlyList<string> FoldersIn(string folder) =>
        Directory.Exists(folder) ? Directory.EnumerateDirectories(folder, "*", SearchOption.AllDirectories).ToList() : Array.Empty<string>();

    public bool TryDeleteFile(string path)
    {
        for (var attempt = 0; attempt < 20 && File.Exists(path); attempt++)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                Thread.Sleep(250);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(250);
            }
        }
        return !File.Exists(path);
    }

    /// Deletes an empty folder only (non-recursive): the uninstall empties the tree first, deepest folders first.
    public bool TryDeleteFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: false);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        return !Directory.Exists(folder);
    }
}
