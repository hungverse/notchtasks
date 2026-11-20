using System.Runtime.InteropServices;
using System.Text;
using NotchTasks.Core.Agents;

namespace NotchTasks.Services;

/// Reads the process table with one NtQuerySystemInformation call per snapshot. Command lines and image paths are read
/// on demand and cached by (pid, StartTicks); an entry is dropped once its process leaves the snapshot.
/// The Windows calls are LibraryImport and every handle is closed in a finally block. Access denied gives null.
/// Core's NativeMethods is internal, so the few declarations this reader needs live here.
public sealed class ProcessReader : IProcessReader
{
    private const int SystemProcessInformation = 5;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
    private const int StatusSuccess = 0;
    private const int ProcessCommandLineInformation = 60;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int InitialBufferSize = 256 * 1024;
    private const int MaxSnapshotAttempts = 8;
    // The longest command line is 32767 UTF-16 characters, behind a UNICODE_STRING header (16 bytes on x64).
    private const int CommandLineBufferSize = 32767 * 2 + 64;
    private const int ImagePathChars = 1024;
    private const uint ProcessVmRead = 0x0010;
    private const int ProcessBasicInformation = 0;
    private const int ProcessWow64Information = 26;
    private const int BasicInformationSize = 48;     // PROCESS_BASIC_INFORMATION on x64
    private const int PebBaseOffset = 8;             // PebBaseAddress in PROCESS_BASIC_INFORMATION
    private const int PebProcessParametersOffset = 0x20;
    private const int ParamsCurrentDirectoryOffset = 0x38;  // CURDIR: DosPath (UNICODE_STRING), then the handle
    private const int MaxCwdBytes = 32767 * 2;

    private readonly object gate = new();
    private readonly object tableGate = new();
    private readonly Dictionary<(int Pid, long Start), string?> commandLines = new();
    private readonly Dictionary<(int Pid, long Start), string?> imagePaths = new();
    private readonly Dictionary<(int Pid, long Start), string?> workingFolders = new();

    // One buffer for every poll, kept between polls so the table does not allocate on the large object heap every 2 s.
    // Only Snapshot uses it, under tableGate.
    private byte[] tableBuffer = new byte[InitialBufferSize];

    /// One snapshot of every process. Returns an empty snapshot when the call keeps failing (never throws on a poll).
    public ProcSnapshot Snapshot()
    {
        ProcSnapshot? table;
        lock (tableGate)
        {
            table = ReadProcessTable();
        }
        var snapshot = table ?? new ProcSnapshot(Array.Empty<ProcInfo>());
        Evict(snapshot);
        return snapshot;
    }

    public string? CommandLine(ProcInfo p) => Cached(commandLines, p, ReadCommandLine);

    public string? ImagePath(ProcInfo p) => Cached(imagePaths, p, ReadImagePath);

    public string? Cwd(ProcInfo p) => Cached(workingFolders, p, ReadWorkingFolder);

    /// Call with tableGate held.
    private ProcSnapshot? ReadProcessTable()
    {
        for (var attempt = 0; attempt < MaxSnapshotAttempts; attempt++)
        {
            var pinned = GCHandle.Alloc(tableBuffer, GCHandleType.Pinned);
            try
            {
                var address = pinned.AddrOfPinnedObject();
                var status = ProcessNative.NtQuerySystemInformation(SystemProcessInformation, address, tableBuffer.Length,
                    out var needed);
                if (status == StatusSuccess)
                {
                    // needed is the number of bytes the table used. The name pointers are absolute: the buffer address
                    // of this call turns them into offsets.
                    var used = Math.Min(needed, tableBuffer.Length);
                    return ProcTable.Parse(tableBuffer.AsSpan(0, used), address);
                }
                if (status != StatusInfoLengthMismatch)
                {
                    return null;
                }
                // The table grew: keep a buffer of the size Windows asked for plus 25% headroom, for this and later polls.
                tableBuffer = new byte[needed + needed / 4];
            }
            finally
            {
                pinned.Free();
            }
        }
        return null;
    }

    private static string? ReadCommandLine(ProcInfo p)
    {
        if (p.Pid <= 0)
        {
            return null;
        }
        var handle = ProcessNative.OpenProcess(ProcessQueryLimitedInformation, false, (uint)p.Pid);
        if (handle == 0)
        {
            return null;
        }
        try
        {
            var buffer = new byte[CommandLineBufferSize];
            var pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                var status = ProcessNative.NtQueryInformationProcess(handle, ProcessCommandLineInformation,
                    pinned.AddrOfPinnedObject(), buffer.Length, out _);
                if (status != StatusSuccess)
                {
                    return null;
                }
            }
            finally
            {
                pinned.Free();
            }

            // UNICODE_STRING header (Length, MaximumLength, Buffer), then the characters right behind it.
            var headerSize = nint.Size == 8 ? 16 : 8;
            var length = BitConverter.ToUInt16(buffer, 0);
            if (headerSize + length > buffer.Length)
            {
                return null;
            }
            return Encoding.Unicode.GetString(buffer, headerSize, length);
        }
        finally
        {
            ProcessNative.CloseHandle(handle);
        }
    }

    private static string? ReadImagePath(ProcInfo p)
    {
        if (p.Pid <= 0)
        {
            return null;
        }
        var handle = ProcessNative.OpenProcess(ProcessQueryLimitedInformation, false, (uint)p.Pid);
        if (handle == 0)
        {
            return null;
        }
        try
        {
            var buffer = new byte[ImagePathChars * 2];
            var size = (uint)ImagePathChars;
            if (!ProcessNative.QueryFullProcessImageNameW(handle, 0, buffer, ref size) || size == 0)
            {
                return null;
            }
            return Encoding.Unicode.GetString(buffer, 0, (int)size * 2);
        }
        finally
        {
            ProcessNative.CloseHandle(handle);
        }
    }

    /// The working folder from the process's PEB: RTL_USER_PROCESS_PARAMETERS.CurrentDirectory.DosPath, read with
    /// ReadProcessMemory. A 32-bit (WOW64) process has another PEB layout and gives null, as does any failed read.
    private static string? ReadWorkingFolder(ProcInfo p)
    {
        if (p.Pid <= 0 || !Environment.Is64BitProcess)
        {
            return null;
        }
        var handle = ProcessNative.OpenProcess(ProcessQueryLimitedInformation | ProcessVmRead, false, (uint)p.Pid);
        if (handle == 0)
        {
            return null;
        }
        try
        {
            var wow64 = QueryBlock(handle, ProcessWow64Information, nint.Size);
            if (wow64 is null || BitConverter.ToInt64(wow64, 0) != 0)
            {
                return null;
            }
            var basic = QueryBlock(handle, ProcessBasicInformation, BasicInformationSize);
            if (basic is null)
            {
                return null;
            }
            var peb = BitConverter.ToInt64(basic, PebBaseOffset);
            var paramsPointer = ReadMemory(handle, peb + PebProcessParametersOffset, nint.Size);
            if (paramsPointer is null)
            {
                return null;
            }
            var parameters = BitConverter.ToInt64(paramsPointer, 0);
            var curDir = ReadMemory(handle, parameters + ParamsCurrentDirectoryOffset, 16);
            if (curDir is null)
            {
                return null;
            }
            var length = BitConverter.ToUInt16(curDir, 0);
            var buffer = BitConverter.ToInt64(curDir, 8);
            if (length == 0 || length > MaxCwdBytes || (length & 1) != 0)
            {
                return null;
            }
            var text = ReadMemory(handle, buffer, length);
            return text is null ? null : TrimFolder(Encoding.Unicode.GetString(text));
        }
        finally
        {
            ProcessNative.CloseHandle(handle);
        }
    }

    /// One NtQueryInformationProcess class, as its raw bytes, or null when the call fails.
    private static byte[]? QueryBlock(nint handle, int infoClass, int size)
    {
        var buffer = new byte[size];
        var pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            var status = ProcessNative.NtQueryInformationProcess(handle, infoClass, pinned.AddrOfPinnedObject(), size, out _);
            return status == StatusSuccess ? buffer : null;
        }
        finally
        {
            pinned.Free();
        }
    }

    /// count bytes at address in the process, or null when the read fails or is short.
    private static byte[]? ReadMemory(nint handle, long address, int count)
    {
        if (address <= 0 || count <= 0)
        {
            return null;
        }
        var buffer = new byte[count];
        var pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            var ok = ProcessNative.ReadProcessMemory(handle, (nint)address, pinned.AddrOfPinnedObject(), count, out var read);
            return ok && read == count ? buffer : null;
        }
        finally
        {
            pinned.Free();
        }
    }

    /// Drops trailing separators, except the one of a drive root ("C:\").
    private static string TrimFolder(string path)
    {
        var end = path.Length;
        while (end > 3 && path[end - 1] == '\\')
        {
            end--;
        }
        return path[..end];
    }

    /// The cached value for p, read once with read. The OS call runs outside the lock.
    private string? Cached(Dictionary<(int Pid, long Start), string?> cache, ProcInfo p, Func<ProcInfo, string?> read)
    {
        var key = (p.Pid, p.StartTicks);
        lock (gate)
        {
            if (cache.TryGetValue(key, out var hit))
            {
                return hit;
            }
        }
        var value = read(p);
        lock (gate)
        {
            cache[key] = value;
        }
        return value;
    }

    /// Drops the cached entries of processes that are no longer in the snapshot (or whose pid now belongs to another start).
    private void Evict(ProcSnapshot snapshot)
    {
        lock (gate)
        {
            var live = new HashSet<(int Pid, long Start)>();
            foreach (var p in snapshot.All)
            {
                live.Add((p.Pid, p.StartTicks));
            }
            foreach (var key in commandLines.Keys.Where(k => !live.Contains(k)).ToList())
            {
                commandLines.Remove(key);
            }
            foreach (var key in imagePaths.Keys.Where(k => !live.Contains(k)).ToList())
            {
                imagePaths.Remove(key);
            }
            foreach (var key in workingFolders.Keys.Where(k => !live.Contains(k)).ToList())
            {
                workingFolders.Remove(key);
            }
        }
    }
}

/// The ntdll and kernel32 calls ProcessReader makes. Every OpenProcess is paired with a CloseHandle in the caller's finally.
internal static partial class ProcessNative
{
    [LibraryImport("ntdll.dll")]
    public static partial int NtQuerySystemInformation(int informationClass, nint buffer, int length, out int returnLength);

    [LibraryImport("ntdll.dll")]
    public static partial int NtQueryInformationProcess(nint process, int informationClass, nint buffer, int length, out int returnLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ReadProcessMemory(nint process, nint baseAddress, nint buffer, nint size, out nint bytesRead);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool QueryFullProcessImageNameW(nint process, uint flags, byte[] name, ref uint size);
}
