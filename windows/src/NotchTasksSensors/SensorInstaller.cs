using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using NotchTasks.Core.Sensors;

namespace NotchTasksSensors;

/// The elevated install and uninstall of the logon task. The task runs a copy in Program Files, an admin-only folder, and never
/// the copy in the app's own folder, which a user can write to. Nothing here is run by tests or by the app without a UAC click.
internal static class SensorInstaller
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(10);

    /// The folder the logon task runs from.
    public static string TargetFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), SensorNames.FolderName);

    /// Copies the helper into Program Files, registers the logon task for the user, then starts the copy to serve now. With no
    /// zones there is nothing to install, so this process serves once (the app then learns there are no zones) and stays.
    public static async Task<int> InstallLogonAsync(string sid, int? parent)
    {
        var reader = new WmiThermalReader();
        if (reader.Read().Count == 0)
        {
            return await SensorHost.ServeAsync(sid, parent, reader);
        }

        SignalStopAndWait(sid);  // a helper already running from the copy releases its files
        var target = TargetFolder;
        EnsureInheritedFolder(target);
        CopyWhenFree(AppContext.BaseDirectory, target);

        // The XML sits in the admin-only folder, so no user can change the task definition between writing and registering.
        var xmlPath = Path.Combine(target, "task.xml");
        var copiedExe = Path.Combine(target, SensorNames.ExeName);
        File.WriteAllText(xmlPath, SensorTask.Xml(sid, copiedExe, SensorArgs.Serve(sid, parent: null)), Encoding.Unicode);
        try
        {
            RunSchtasks("/Create", "/TN", SensorNames.TaskName, "/XML", xmlPath, "/F");
        }
        finally
        {
            File.Delete(xmlPath);
        }

        using var copy = Process.Start(new ProcessStartInfo(copiedExe, SensorArgs.Serve(sid, parent: null))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("The helper copy did not start.");
        return 0;
    }

    /// Stops the user's helper, removes the logon task, and removes the Program Files copy. Exit code 0 when the task is gone
    /// and the copy is removed. The task is removed first, so no logon starts the helper while the copy is removed.
    public static async Task<int> UninstallLogonAsync(string? sid)
    {
        if (TaskExists())
        {
            RunSchtasks("/Delete", "/TN", SensorNames.TaskName, "/F");
        }
        if (TaskExists())
        {
            return 1;  // still registered: the copy stays, so a logon cannot run a copy that was removed
        }

        if (sid is not null)
        {
            SignalStopAndWait(sid);
        }

        var target = TargetFolder;
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
    /// with its own rules, or with write rights for ordinary users, is refused: this process does not loosen anything.
    internal static void EnsureInheritedFolder(string target)
    {
        Directory.CreateDirectory(target);
        var acl = new DirectoryInfo(target).GetAccessControl();
        if (acl.AreAccessRulesProtected)
        {
            throw new InvalidOperationException("The helper folder has its own permissions: " + target);
        }

        var ordinary = new[]
        {
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
        };
        const FileSystemRights Write = FileSystemRights.WriteData | FileSystemRights.CreateFiles
            | FileSystemRights.Modify | FileSystemRights.FullControl;
        foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType == AccessControlType.Allow
                && rule.IdentityReference is SecurityIdentifier sid
                && ordinary.Any(o => o.Equals(sid))
                && (rule.FileSystemRights & Write) != 0)
            {
                throw new InvalidOperationException("The helper folder is writable by ordinary users: " + target);
            }
        }
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

    /// Sets the user's stop event, then waits until no process holds it any more.
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

    private static bool TaskExists() => RunSchtasksCode("/Query", "/TN", SensorNames.TaskName) == 0;

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
