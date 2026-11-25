using System.Buffers.Binary;

namespace NotchTasks.Core.Sync;

/// Splits a TCP byte stream into frame payloads. Throws InvalidDataException on a length of 0 or over MaxFrame: the caller drops the connection.
public sealed class FrameReader
{
    private byte[] _buffer = Array.Empty<byte>();

    /// Adds the chunk and returns every frame body it completes, in order. A partial frame waits for the next chunk.
    public IReadOnlyList<byte[]> Push(ReadOnlySpan<byte> chunk)
    {
        var all = new byte[_buffer.Length + chunk.Length];
        _buffer.CopyTo(all, 0);
        chunk.CopyTo(all.AsSpan(_buffer.Length));

        var frames = new List<byte[]>();
        var offset = 0;
        while (all.Length - offset >= 4)
        {
            var n = BinaryPrimitives.ReadUInt32BigEndian(all.AsSpan(offset));
            if (n == 0 || n > Box.MaxFrame)
            {
                throw new InvalidDataException($"frame length {n} out of range");
            }
            if (all.Length - offset - 4 < n)
            {
                break;
            }
            frames.Add(all.AsSpan(offset + 4, (int)n).ToArray());
            offset += 4 + (int)n;
        }
        _buffer = all[offset..];
        return frames;
    }
}
