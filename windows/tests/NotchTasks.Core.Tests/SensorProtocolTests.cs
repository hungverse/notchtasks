using System.Text;
using NotchTasks.Core.Modules;
using NotchTasks.Core.Sensors;
using Xunit;

namespace NotchTasks.Core.Tests;

/// The helper's pipe handler, tested with a fake reader and an in-memory stream: no pipe, no WMI, no UAC.
public sealed class SensorProtocolTests
{
    private const string Sid = "S-1-5-21-1111111111-2222222222-3333333333-1001";

    private sealed class FakeReader : IThermalReader
    {
        public int Calls;
        public bool Throws;
        public IReadOnlyList<ThermalReading> Result = Array.Empty<ThermalReading>();

        public IReadOnlyList<ThermalReading> Read()
        {
            Calls++;
            if (Throws)
            {
                throw new InvalidOperationException("WMI failed");
            }
            return Result;
        }
    }

    /// A stream whose reads come from the request and whose writes go to Output, like one pipe connection.
    private sealed class DuplexStream : Stream
    {
        private readonly MemoryStream _input;

        public DuplexStream(string request)
        {
            _input = new MemoryStream(Encoding.ASCII.GetBytes(request));
        }

        public MemoryStream Output { get; } = new();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => Output.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _input.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Output.Write(buffer, offset, count);
    }

    private static async Task<(FakeReader Reader, string Reply)> ServeAsync(string request, FakeReader? reader = null)
    {
        var fake = reader ?? new FakeReader();
        using var stream = new DuplexStream(request);
        await SensorProtocol.ServeAsync(stream, fake, CancellationToken.None);
        return (fake, Encoding.ASCII.GetString(stream.Output.ToArray()));
    }

    [Fact]
    public async Task Pipe_RejectsUnknownCommand()
    {
        // "exec x" is not "read": nothing is read, nothing is answered, the connection closes.
        var (reader, reply) = await ServeAsync("exec x\n");

        Assert.Equal(0, reader.Calls);
        Assert.Equal(string.Empty, reply);
    }

    [Fact]
    public async Task Pipe_RejectsLineOver64Bytes()
    {
        var (reader, reply) = await ServeAsync(new string('r', 65) + "\n");
        Assert.Equal(0, reader.Calls);
        Assert.Equal(string.Empty, reply);

        // "read" padded to 64 bytes is not "read" either.
        (reader, reply) = await ServeAsync("read" + new string(' ', 60) + "\n");
        Assert.Equal(0, reader.Calls);
        Assert.Equal(string.Empty, reply);
    }

    [Fact]
    public async Task Pipe_RejectsLineWithoutNewline()
    {
        var (reader, reply) = await ServeAsync("read");

        Assert.Equal(0, reader.Calls);
        Assert.Equal(string.Empty, reply);
    }

    [Fact]
    public async Task Pipe_AnswersRead_WithOneJsonLine()
    {
        var reader = new FakeReader { Result = new[] { new ThermalReading(@"ACPI\THM0", 61.5) } };

        var (_, reply) = await ServeAsync("read\n", reader);

        Assert.Equal(1, reader.Calls);
        Assert.Equal("[{\"n\":\"ACPI\\\\THM0\",\"c\":61.5}]\n", reply);
    }

    [Fact]
    public async Task Pipe_ReaderFailure_AnswersNothing()
    {
        // A failed read closes without an answer, so the app can tell it from "no zones" ([]).
        var reader = new FakeReader { Throws = true };

        var (_, reply) = await ServeAsync("read\n", reader);

        Assert.Equal(1, reader.Calls);
        Assert.Equal(string.Empty, reply);
    }

    [Fact]
    public void ThermalWire_RoundTrips_AndRejectsOtherShapes()
    {
        var readings = new[] { new ThermalReading("TZ\"00\\x", 48.25) };
        var back = ThermalWire.Parse(ThermalWire.Write(readings));

        Assert.NotNull(back);
        Assert.Equal(readings[0], back.Single());
        Assert.Empty(ThermalWire.Parse("[]")!);
        Assert.Null(ThermalWire.Parse("{}"));
        Assert.Null(ThermalWire.Parse("not json"));
    }

    [Fact]
    public void SensorArgs_ParseTheThreeForms_AndRejectABadSid()
    {
        var serve = SensorArgs.Parse(new[] { "--serve", "--parent", "4242", "--user", Sid });
        Assert.Equal(SensorMode.Serve, serve!.Mode);
        Assert.Equal(Sid, serve.Sid);
        Assert.Equal(4242, serve.Parent);

        var logon = SensorArgs.Parse(new[] { "--install-logon", Sid });
        Assert.Equal(SensorMode.InstallLogon, logon!.Mode);
        Assert.Null(logon.Parent);

        Assert.Equal(SensorMode.UninstallLogon, SensorArgs.Parse(new[] { "--uninstall-logon" })!.Mode);
        Assert.Null(SensorArgs.Parse(new[] { "--serve" }));
        Assert.Null(SensorArgs.Parse(new[] { "--install-logon", "S-1-5-21 & calc" }));
        Assert.Null(SensorArgs.Parse(new[] { "--serve", "--user", Sid, "--parent", "-3" }));
    }
}
