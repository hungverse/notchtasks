using System.Runtime.InteropServices;

namespace NotchTasks.Core;

/// The Windows calls behind ProcessChecks. LibraryImport generates the marshalling.
internal static partial class NativeMethods
{
    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetExitCodeProcess(nint process, out uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint handle);

    [LibraryImport("ntdll.dll")]
    public static partial int NtQueryInformationProcess(nint process, int informationClass, ref ProcessBasicInformation information, int length, out int returnLength);
}

/// PROCESS_BASIC_INFORMATION: only InheritedFromUniqueProcessId (the parent pid) is read.
[StructLayout(LayoutKind.Sequential)]
internal struct ProcessBasicInformation
{
    public nint Reserved1;
    public nint PebBaseAddress;
    public nint Reserved2;
    public nint Reserved3;
    public nuint UniqueProcessId;
    public nuint InheritedFromUniqueProcessId;
}
