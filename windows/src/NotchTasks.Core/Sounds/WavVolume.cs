using System.Buffers.Binary;
using System.Text;

namespace NotchTasks.Core.Sounds;

/// Scales a WAV file's samples by a volume, so a sound plays at the set volume through PlaySound (which has no volume).
/// The RIFF chunks are walked, so LIST and other chunks may sit anywhere. Only PCM (format tag 1) with 8-bit (unsigned,
/// centred on 128) or 16-bit (signed) samples is scaled. Anything else, and malformed input, is returned unchanged.
public static class WavVolume
{
    private const ushort FormatPcm = 1;

    /// A copy of the WAV with its samples scaled by volume (clamped to 0..1), or the input itself when it is not scalable.
    public static byte[] Scale(byte[] wav, double volume)
    {
        ArgumentNullException.ThrowIfNull(wav);
        var data = FindPcmData(wav);
        if (data is null)
        {
            return wav;
        }

        var (start, length, bits) = data.Value;
        var gain = double.IsNaN(volume) ? 1 : Math.Clamp(volume, 0, 1);
        var result = (byte[])wav.Clone();
        if (bits == 16)
        {
            for (var i = start; i + 1 < start + length; i += 2)
            {
                var sample = BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(i));
                var scaled = (short)Math.Clamp(Math.Round(sample * gain), short.MinValue, short.MaxValue);
                BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(i), scaled);
            }
        }
        else
        {
            for (var i = start; i < start + length; i++)
            {
                var centred = wav[i] - 128;
                result[i] = (byte)Math.Clamp(Math.Round(centred * gain) + 128, 0, 255);
            }
        }
        return result;
    }

    /// Where the sample data is, how long it is and its bit depth; null when the file is not PCM 8 or 16-bit, or is malformed.
    private static (int Start, int Length, ushort Bits)? FindPcmData(byte[] wav)
    {
        if (wav.Length < 12 || Encoding.ASCII.GetString(wav, 0, 4) != "RIFF" || Encoding.ASCII.GetString(wav, 8, 4) != "WAVE")
        {
            return null;
        }

        ushort? format = null, bits = null;
        int start = 0, length = 0;
        var seenData = false;
        var pos = 12;
        while (pos + 8 <= wav.Length && (format is null || !seenData))
        {
            var id = Encoding.ASCII.GetString(wav, pos, 4);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(pos + 4));
            var body = pos + 8;
            if (body + (long)size > wav.Length)
            {
                return null;  // a chunk that runs past the end: malformed
            }

            if (id == "fmt ")
            {
                if (size < 16)
                {
                    return null;
                }
                format = BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(body));
                bits = BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(body + 14));
            }
            else if (id == "data" && !seenData)
            {
                seenData = true;
                start = body;
                length = (int)size;
            }
            pos = body + (int)size + (int)(size & 1);  // chunks are padded to an even length
        }

        if (format != FormatPcm || !seenData || bits is not (8 or 16))
        {
            return null;
        }
        return (start, length, bits.Value);
    }
}
