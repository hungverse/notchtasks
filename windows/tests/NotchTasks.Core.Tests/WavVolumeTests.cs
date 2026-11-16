using System.Buffers.Binary;
using System.Text;
using NotchTasks.Core.Sounds;
using Xunit;

namespace NotchTasks.Core.Tests;

public class WavVolumeTests
{
    [Fact]
    public void Scale_Half_Halves16BitSamples()
    {
        var wav = Wav(format: 1, bits: 16, data: Samples16(1000, -2000, 30000, -32768));

        var result = WavVolume.Scale(wav, 0.5);

        Assert.Equal(Samples16(500, -1000, 15000, -16384), DataOf(result));
    }

    [Fact]
    public void Scale_AboveOne_IsFullVolume()
    {
        var wav = Wav(format: 1, bits: 16, data: Samples16(1000, -2000));

        var result = WavVolume.Scale(wav, 2.0);

        Assert.Equal(wav, result);
    }

    [Fact]
    public void Scale_BelowZero_IsSilent()
    {
        var wav = Wav(format: 1, bits: 16, data: Samples16(1000, -2000));

        var result = WavVolume.Scale(wav, -1.0);

        Assert.Equal(Samples16(0, 0), DataOf(result));
    }

    [Fact]
    public void Scale_NonPcm_Unchanged()
    {
        var wav = Wav(format: 3, bits: 16, data: Samples16(1000, -2000));

        var result = WavVolume.Scale(wav, 0.5);

        Assert.Equal(wav, result);
    }

    [Fact]
    public void Scale_Malformed_Unchanged()
    {
        var wav = Wav(format: 1, bits: 16, data: Samples16(1000, -2000));
        var truncated = wav[..30];  // cut inside the fmt chunk
        var notRiff = (byte[])wav.Clone();
        notRiff[0] = (byte)'X';

        Assert.Equal(truncated, WavVolume.Scale(truncated, 0.5));
        Assert.Equal(notRiff, WavVolume.Scale(notRiff, 0.5));
        Assert.Equal(Array.Empty<byte>(), WavVolume.Scale([], 0.5));
    }

    [Fact]
    public void Scale_KeepsHeader()
    {
        var wav = Wav(format: 1, bits: 16, data: Samples16(1000, -2000));

        var result = WavVolume.Scale(wav, 0.5);

        Assert.Equal(wav.Length, result.Length);
        Assert.Equal(wav[..44], result[..44]);
    }

    [Fact]
    public void Scale_8Bit_IsScaledAroundCentre()
    {
        var wav = Wav(format: 1, bits: 8, data: [228, 28, 128]);

        var result = WavVolume.Scale(wav, 0.5);

        Assert.Equal(new byte[] { 178, 78, 128 }, DataOf(result));
    }

    [Fact]
    public void Scale_SkipsListChunkBeforeFormat()
    {
        var list = Chunk("LIST", Encoding.ASCII.GetBytes("INFOISFT\x05\0\0\0test\0"));
        var wav = Wav(format: 1, bits: 16, data: Samples16(1000, -2000), before: list);

        var result = WavVolume.Scale(wav, 0.5);

        Assert.Equal(Samples16(500, -1000), DataOf(result));
        Assert.Equal(wav.Length, result.Length);
    }

    [Fact]
    public void Scale_TruncatedData_StillScaled()
    {
        var wav = Wav(format: 1, bits: 16, data: Samples16(1000, -2000, 30000, -32768));
        var truncated = wav[..(wav.Length - 2)];  // the data chunk declares 8 bytes; 6 are left

        var result = WavVolume.Scale(truncated, 0.5);

        Assert.Equal(truncated.Length, result.Length);
        Assert.Equal(Samples16(500, -1000, 15000), result[44..]);
    }

    [Fact]
    public void Scale_DataBeforeFmt()
    {
        var wav = Wav(format: 1, bits: 16, data: Samples16(1000, -2000), dataFirst: true);

        var result = WavVolume.Scale(wav, 0.5);

        Assert.Equal(Samples16(500, -1000), DataOf(result));
        Assert.Equal(wav.Length, result.Length);
    }

    [Fact]
    public void Scale_OddPaddedChunk()
    {
        var odd = Chunk("LIST", [1, 2, 3]);  // 3 bytes, padded to 4 on disk
        var wav = Wav(format: 1, bits: 16, data: Samples16(1000, -2000), before: odd);

        var result = WavVolume.Scale(wav, 0.5);

        Assert.Equal(Samples16(500, -1000), DataOf(result));
        Assert.Equal(wav.Length, result.Length);
    }

    /// A RIFF/WAVE file: the optional chunks, then "fmt " and "data" (or "data" first when dataFirst).
    private static byte[] Wav(ushort format, ushort bits, byte[] data, byte[]? before = null, bool dataFirst = false)
    {
        var fmt = new byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(0), format);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(fmt.AsSpan(4), 22050);
        BinaryPrimitives.WriteUInt32LittleEndian(fmt.AsSpan(8), 22050u * bits / 8);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(12), (ushort)(bits / 8));
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(14), bits);

        var chunks = dataFirst
            ? Concat(Chunk("data", data), Chunk("fmt ", fmt))
            : Concat(Chunk("fmt ", fmt), Chunk("data", data));
        var body = Concat(before ?? [], chunks);
        var header = new byte[12];
        Encoding.ASCII.GetBytes("RIFF", header.AsSpan(0));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), (uint)(4 + body.Length));
        Encoding.ASCII.GetBytes("WAVE", header.AsSpan(8));
        return Concat(header, body);
    }

    private static byte[] Chunk(string id, byte[] body)
    {
        var chunk = new byte[8 + body.Length + (body.Length & 1)];
        Encoding.ASCII.GetBytes(id, chunk.AsSpan(0));
        BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(4), (uint)body.Length);
        body.CopyTo(chunk, 8);
        return chunk;
    }

    private static byte[] Samples16(params short[] samples)
    {
        var bytes = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), samples[i]);
        }
        return bytes;
    }

    /// The bytes of the data chunk, found by walking the chunks.
    private static byte[] DataOf(byte[] wav)
    {
        var pos = 12;
        while (pos + 8 <= wav.Length)
        {
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(pos + 4));
            if (Encoding.ASCII.GetString(wav, pos, 4) == "data")
            {
                return wav[(pos + 8)..(pos + 8 + size)];
            }
            pos += 8 + size + (size & 1);
        }
        throw new InvalidOperationException("no data chunk");
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var result = new byte[parts.Sum(p => p.Length)];
        var at = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result, at);
            at += part.Length;
        }
        return result;
    }
}
