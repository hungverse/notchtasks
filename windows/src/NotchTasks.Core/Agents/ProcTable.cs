using System.Buffers.Binary;
using System.Text;

namespace NotchTasks.Core.Agents;

/// One process from the system process table. Name is the exe name as Windows gives it.
/// CpuTicks is kernel + user time in 100 ns units. StartTicks is the creation time (100 ns units), which tells a reused pid apart.
public readonly record struct ProcInfo(int Pid, int Ppid, string Name, long StartTicks, long CpuTicks);

/// One snapshot of the process table, indexed by pid and by normalised name.
public sealed class ProcSnapshot
{
    private readonly ProcInfo[] procs;
    private readonly Dictionary<int, ProcInfo> byId = new();
    private readonly Dictionary<int, List<ProcInfo>> kids = new();
    private readonly Dictionary<string, List<ProcInfo>> byName = new(StringComparer.Ordinal);

    public ProcSnapshot(IReadOnlyList<ProcInfo> procs)
    {
        this.procs = procs.OrderBy(p => p.Pid).ToArray();
        foreach (var p in this.procs)
        {
            byId[p.Pid] = p;
            Add(kids, p.Ppid, p);
            Add(byName, ProcTable.NormName(p.Name), p);
        }
    }

    /// Every process in this snapshot, in pid order.
    public IReadOnlyList<ProcInfo> All => procs;

    /// The process with this pid. A pid appears once in a snapshot.
    public IReadOnlyDictionary<int, ProcInfo> ById => byId;

    /// The children of pid, in pid order. Empty when there are none.
    public IReadOnlyList<ProcInfo> Kids(int pid) => kids.TryGetValue(pid, out var list) ? list : Array.Empty<ProcInfo>();

    /// The processes whose normalised name equals that of name (see ProcTable.NormName): "node" matches "C:\...\Node.EXE".
    public IReadOnlyList<ProcInfo> ByNormName(string name) =>
        byName.TryGetValue(ProcTable.NormName(name), out var list) ? list : Array.Empty<ProcInfo>();

    private static void Add<TKey>(Dictionary<TKey, List<ProcInfo>> map, TKey key, ProcInfo p) where TKey : notnull
    {
        if (!map.TryGetValue(key, out var list))
        {
            list = new List<ProcInfo>();
            map[key] = list;
        }
        list.Add(p);
    }
}

/// The process table reads the Windows calls behind it through IProcessReader (the app implements it with NtQuerySystemInformation).
public interface IProcessReader
{
    /// One snapshot of every process.
    ProcSnapshot Snapshot();

    /// The command line of a process, or null when it cannot be read (access denied, or the process has gone).
    string? CommandLine(ProcInfo p);

    /// The full path of a process's program, or null when it cannot be read.
    string? ImagePath(ProcInfo p);
}

/// Pure helpers over the process table (proc.js normName, splitArgs, childrenOf) and the decoder for the system's
/// process list. Nothing here calls Windows: the app reads the bytes and hands them to Parse.
public static class ProcTable
{
    // SYSTEM_PROCESS_INFORMATION, x64 field offsets.
    private const int CreateTimeOffset = 0x20;
    private const int UserTimeOffset = 0x28;
    private const int KernelTimeOffset = 0x30;
    private const int ImageNameOffset = 0x38;       // UNICODE_STRING: Length (ushort), MaximumLength (ushort), Buffer (pointer at +8)
    private const int ImageNameBufferOffset = 0x40;
    private const int UniqueProcessIdOffset = 0x50;
    private const int InheritedFromOffset = 0x58;
    private const int EntryMinSize = 0x60;

    /// "C:\x\Node.EXE" and "/usr/bin/node" -> "node": the last path part, without .exe, lower-case.
    public static string NormName(string? s)
    {
        var text = s ?? string.Empty;
        var cut = text.LastIndexOfAny(new[] { '\\', '/' });
        var last = cut >= 0 ? text[(cut + 1)..] : text;
        if (last.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            last = last[..^4];
        }
        return last.ToLowerInvariant();
    }

    /// Command line -> argv, honouring double quotes (a quote is dropped; spaces inside quotes are kept).
    public static IReadOnlyList<string> SplitArgs(string? cmd)
    {
        var result = new List<string>();
        var cur = new StringBuilder();
        var quoted = false;
        var has = false;
        foreach (var ch in cmd ?? string.Empty)
        {
            if (ch == '"')
            {
                quoted = !quoted;
                has = true;
                continue;
            }
            if (!quoted && char.IsWhiteSpace(ch))
            {
                if (has)
                {
                    result.Add(cur.ToString());
                    cur.Clear();
                    has = false;
                }
                continue;
            }
            cur.Append(ch);
            has = true;
        }
        if (has)
        {
            result.Add(cur.ToString());
        }
        return result;
    }

    /// Decodes the buffer NtQuerySystemInformation(SystemProcessInformation) filled. bufferAddress is the address the
    /// buffer had during the call: the name pointers in the entries are absolute and are turned into offsets with it.
    /// Pid 0 is named "Idle" and pid 4 "System" when Windows gives no name.
    public static ProcSnapshot Parse(ReadOnlySpan<byte> buffer, nint bufferAddress)
    {
        var list = new List<ProcInfo>();
        var offset = 0;
        while (offset + EntryMinSize <= buffer.Length)
        {
            var entry = buffer.Slice(offset);
            var pid = (int)BinaryPrimitives.ReadInt64LittleEndian(entry[UniqueProcessIdOffset..]);
            var ppid = (int)BinaryPrimitives.ReadInt64LittleEndian(entry[InheritedFromOffset..]);
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(entry[ImageNameOffset..]);
            var namePointer = (long)BinaryPrimitives.ReadUInt64LittleEndian(entry[ImageNameBufferOffset..]);
            var nameOffset = namePointer - (long)bufferAddress;

            var name = string.Empty;
            if (nameLength > 0 && nameOffset >= 0 && nameOffset + nameLength <= buffer.Length)
            {
                name = Encoding.Unicode.GetString(buffer.Slice((int)nameOffset, nameLength));
            }
            if (name.Length == 0)
            {
                name = pid == 0 ? "Idle" : pid == 4 ? "System" : string.Empty;
            }

            var startTicks = (long)BinaryPrimitives.ReadUInt64LittleEndian(entry[CreateTimeOffset..]);
            var userTicks = (long)BinaryPrimitives.ReadUInt64LittleEndian(entry[UserTimeOffset..]);
            var kernelTicks = (long)BinaryPrimitives.ReadUInt64LittleEndian(entry[KernelTimeOffset..]);
            list.Add(new ProcInfo(pid, ppid, name, startTicks, kernelTicks + userTicks));

            var next = BinaryPrimitives.ReadUInt32LittleEndian(entry);
            if (next == 0)
            {
                break;
            }
            offset += (int)next;
        }
        return new ProcSnapshot(list);
    }
}
