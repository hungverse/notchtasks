using System.Runtime.InteropServices;
using System.Text;

namespace NotchTasks.Core;

/// Process checks the hook and the app need: is a pid alive, and who started this process.
/// The Windows calls are in NativeMethods (LibraryImport, so AOT-safe: no Process class, no reflection).
public static class ProcessChecks
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint StillActive = 259;
    private const int ErrorAccessDenied = 5;

    /// True when the process exists. A process we may not open (another user's, a protected one) counts as alive,
    /// the way EPERM does in the Electron build; a finished process does not, even while a handle to it is open.
    public static bool IsAlive(int pid)
    {
        if (pid <= 0)
        {
            return false;
        }
        var handle = NativeMethods.OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);
        if (handle == 0)
        {
            return Marshal.GetLastPInvokeError() == ErrorAccessDenied;
        }
        try
        {
            return NativeMethods.GetExitCodeProcess(handle, out var code) && code == StillActive;
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    /// True when the process is alive and its program's file name is one of fileNames (compared without case, so
    /// "NotchTasks.exe" matches "notchtasks.EXE"). A process we may not open counts as alive, as in IsAlive, and so does
    /// one whose program name cannot be read: only a process we can read and that runs another program is "not this one".
    public static bool RunsAs(int pid, string[] fileNames)
    {
        if (pid <= 0)
        {
            return false;
        }
        var handle = NativeMethods.OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);
        if (handle == 0)
        {
            return Marshal.GetLastPInvokeError() == ErrorAccessDenied;
        }
        try
        {
            if (!NativeMethods.GetExitCodeProcess(handle, out var code) || code != StillActive)
            {
                return false;
            }
            var name = ImageNameOf(handle);
            return name is null || fileNames.Contains(name, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    /// The file name of the process's program (NotchTasks.exe), or null when the process cannot be opened or read.
    public static string? ImageName(int pid)
    {
        if (pid <= 0)
        {
            return null;
        }
        var handle = NativeMethods.OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);
        if (handle == 0)
        {
            return null;
        }
        try
        {
            return ImageNameOf(handle);
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    private static string? ImageNameOf(nint handle)
    {
        // 1024 UTF-16 characters; a longer path fails the call (buffer too small), and the name is then unknown.
        var buffer = new byte[1024 * 2];
        var size = (uint)(buffer.Length / 2);
        if (!NativeMethods.QueryFullProcessImageNameW(handle, 0, buffer, ref size) || size == 0)
        {
            return null;
        }
        return Path.GetFileName(Encoding.Unicode.GetString(buffer, 0, (int)size * 2));
    }

    /// The pid of the process that started this one, or null when it cannot be read.
    public static int? CurrentParentId()
    {
        var info = new ProcessBasicInformation();
        // -1 is the pseudo handle of this process: always openable with full access.
        var status = NativeMethods.NtQueryInformationProcess(new nint(-1), 0, ref info, Marshal.SizeOf<ProcessBasicInformation>(), out _);
        return status == 0 && info.InheritedFromUniqueProcessId != 0 ? (int)info.InheritedFromUniqueProcessId : null;
    }
}
