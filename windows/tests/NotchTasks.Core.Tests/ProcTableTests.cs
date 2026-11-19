using System.Buffers.Binary;
using System.Text;
using NotchTasks.Core.Agents;
using Xunit;

namespace NotchTasks.Core.Tests;

// Ported from electron/test/linux-proc.test.js (normName, splitArgs, childrenOf). Platform-neutral cases only.
public sealed class ProcTableTests
{
    [Theory]
    [InlineData(@"C:\x\Node.EXE", "node")]
    [InlineData("/usr/bin/node", "node")]
    [InlineData("Code.exe", "code")]
    public void NormName(string s, string want)
    {
        Assert.Equal(want, ProcTable.NormName(s));
    }

    [Fact]
    public void SplitArgs_HonoursQuotes()
    {
        var args = ProcTable.SplitArgs("\"C:\\Program Files\\node.exe\" cli.js --x \"a b\"");
        Assert.Equal(new[] { @"C:\Program Files\node.exe", "cli.js", "--x", "a b" }, args);
    }

    [Fact]
    public void Kids_InPidOrder()
    {
        var snap = new ProcSnapshot(new[]
        {
            new ProcInfo(30, 1, "a.exe", 0, 0),
            new ProcInfo(10, 1, "b.exe", 0, 0),
            new ProcInfo(20, 1, "c.exe", 0, 0),
            new ProcInfo(40, 10, "d.exe", 0, 0),
        });
        Assert.Equal(new[] { 10, 20, 30 }, snap.Kids(1).Select(p => p.Pid));
        Assert.Equal(new[] { 40 }, snap.Kids(10).Select(p => p.Pid));
        Assert.Empty(snap.Kids(99));
    }

    [Fact]
    public void ByNormName_MatchesWithoutPathOrExe()
    {
        var snap = new ProcSnapshot(new[]
        {
            new ProcInfo(5, 1, @"C:\Program Files\nodejs\NODE.EXE", 0, 0),
            new ProcInfo(6, 1, "notepad.exe", 0, 0),
        });
        Assert.Equal(new[] { 5 }, snap.ByNormName("node").Select(p => p.Pid));
    }

    [Fact]
    public void Parse_ReadsX64SystemProcessInformation()
    {
        // Three SYSTEM_PROCESS_INFORMATION entries (0x60 bytes each, x64 layout) and the name of the first one after them.
        // Pointers in the entries are absolute addresses of a buffer that starts at Base.
        const long Base = 0x7000_0000;
        var name = Encoding.Unicode.GetBytes("node.exe");
        var buf = new byte[0x60 * 3 + name.Length];
        name.CopyTo(buf, 0x60 * 3);

        WriteProcess(buf, 0x00, next: 0x60, pid: 1234, ppid: 4, create: 111, user: 1000, kernel: 500,
            nameLen: name.Length, nameAddr: Base + 0x60 * 3);
        WriteProcess(buf, 0x60, next: 0x60, pid: 4, ppid: 0, create: 0, user: 0, kernel: 0, nameLen: 0, nameAddr: 0);
        WriteProcess(buf, 0xC0, next: 0, pid: 0, ppid: 0, create: 0, user: 0, kernel: 0, nameLen: 0, nameAddr: 0);

        var snap = ProcTable.Parse(buf, (nint)Base);

        var node = snap.ById[1234];
        Assert.Equal("node.exe", node.Name);
        Assert.Equal(4, node.Ppid);
        Assert.Equal(111, node.StartTicks);
        Assert.Equal(1500, node.CpuTicks);
        Assert.Equal("System", snap.ById[4].Name);
        Assert.Equal("Idle", snap.ById[0].Name);
    }

    private static void WriteProcess(byte[] b, int at, uint next, int pid, int ppid, long create, long user, long kernel,
        int nameLen, long nameAddr)
    {
        var s = b.AsSpan(at);
        BinaryPrimitives.WriteUInt32LittleEndian(s, next);
        BinaryPrimitives.WriteInt64LittleEndian(s[0x20..], create);
        BinaryPrimitives.WriteInt64LittleEndian(s[0x28..], user);
        BinaryPrimitives.WriteInt64LittleEndian(s[0x30..], kernel);
        BinaryPrimitives.WriteUInt16LittleEndian(s[0x38..], (ushort)nameLen);
        BinaryPrimitives.WriteUInt16LittleEndian(s[0x3A..], (ushort)nameLen);
        BinaryPrimitives.WriteInt64LittleEndian(s[0x40..], nameAddr);
        BinaryPrimitives.WriteInt64LittleEndian(s[0x50..], pid);
        BinaryPrimitives.WriteInt64LittleEndian(s[0x58..], ppid);
    }
}
