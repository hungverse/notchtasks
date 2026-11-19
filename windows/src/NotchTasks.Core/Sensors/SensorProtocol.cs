using System.Text;
using System.Text.Json;
using NotchTasks.Core.Modules;

namespace NotchTasks.Core.Sensors;

/// Reads the thermal zones. The helper's WMI reader implements it; tests pass a fake.
public interface IThermalReader
{
    /// The zones now. Throws when the read fails (the helper then answers nothing).
    IReadOnlyList<ThermalReading> Read();
}

/// The helper's pipe protocol. The app sends one line, "read", and the helper answers one JSON line,
/// [{"n":"zone","c":61.5}], or nothing when the read failed. Anything else closes the connection, and nothing is executed or
/// opened from the request. Pure: it works on any stream, so the pipe is only the helper's own wiring.
public static class SensorProtocol
{
    /// The longest request line, in bytes, without its newline.
    public const int MaxRequestBytes = 64;

    /// The longest reply the app reads, in bytes.
    public const int MaxReplyBytes = 64 * 1024;

    /// The one command the helper accepts.
    public const string ReadCommand = "read";

    private static readonly TimeSpan DefaultReadTimeout = TimeSpan.FromSeconds(2);

    /// Handles one connection: reads one request line (at most MaxRequestBytes), and answers "read" with one reply line. Any
    /// other request, an over-long line, a line without its newline, a request that does not arrive in time, or a failed read
    /// ends the connection with no answer. The caller owns the stream and closes it.
    public static async Task ServeAsync(Stream stream, IThermalReader reader, CancellationToken ct, TimeSpan? readTimeout = null)
    {
        string? line;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(readTimeout ?? DefaultReadTimeout);
            line = await ReadLineAsync(stream, MaxRequestBytes, deadline.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (IOException)
        {
            return;
        }

        if (line != ReadCommand)
        {
            return;
        }

        IReadOnlyList<ThermalReading> readings;
        try
        {
            readings = reader.Read();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return;
        }

        var reply = Encoding.UTF8.GetBytes(ThermalWire.Write(readings) + "\n");
        await stream.WriteAsync(reply, ct);
        await stream.FlushAsync(ct);
    }

    /// Reads one line as UTF-8 without its newline. Null when the stream ends first, or when the line is longer than maxBytes
    /// (the over-long line is not read to its end). A line that is exactly maxBytes long is accepted.
    public static async Task<string?> ReadLineAsync(Stream stream, int maxBytes, CancellationToken ct)
    {
        var bytes = new List<byte>(Math.Min(maxBytes, 256));
        var one = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(one, ct);
            if (read == 0)
            {
                return null;
            }
            if (one[0] == (byte)'\n')
            {
                return Encoding.UTF8.GetString(bytes.ToArray());
            }
            if (bytes.Count >= maxBytes)
            {
                return null;
            }
            bytes.Add(one[0]);
        }
    }
}

/// The reply's JSON: an array of {"n": name, "c": celsius}. Written and read with System.Text.Json's reader and writer, which
/// need no reflection.
public static class ThermalWire
{
    /// The JSON array for the readings (no newline).
    public static string Write(IEnumerable<ThermalReading> readings)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var reading in readings)
            {
                writer.WriteStartObject();
                writer.WriteString("n", reading.Name);
                writer.WriteNumber("c", reading.Celsius);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// The readings in a reply, or null when the text is not that shape. An empty array is an answer with no zones.
    public static IReadOnlyList<ThermalReading>? Parse(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }
            var readings = new List<ThermalReading>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("n", out var name) || name.ValueKind != JsonValueKind.String
                    || !item.TryGetProperty("c", out var celsius) || celsius.ValueKind != JsonValueKind.Number
                    || !celsius.TryGetDouble(out var value))
                {
                    return null;
                }
                readings.Add(new ThermalReading(name.GetString() ?? string.Empty, value));
            }
            return readings;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
