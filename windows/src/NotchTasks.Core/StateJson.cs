using System.Text.Json;
using System.Text.Json.Nodes;

namespace NotchTasks.Core;

/// Reads and writes single keys of state.json, the app's small window memory. Keys this build does not know are kept on
/// save. An unreadable file is moved aside (state.json.corrupt) before the next write, as settings.json is.
public static class StateJson
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// The value of a key, or null when the file or the key is missing. A corrupt file reads as empty. The read is retried
    /// when another process holds the file for a moment; it throws IOException when every attempt fails.
    public static JsonNode? Get(string file, string key)
    {
        var root = Parse(AtomicFile.ReadAllTextOrNullWithRetry(file), out _);
        return root[key]?.DeepClone();
    }

    /// Sets one key and writes the file atomically. Throws IOException when the file cannot be read or written.
    /// Call from the UI thread only: writes are not locked, so two concurrent Set calls would lose one key.
    public static void Set(string file, string key, JsonNode? value)
    {
        var root = Parse(AtomicFile.ReadAllTextOrNullWithRetry(file), out var corrupt);
        if (corrupt && File.Exists(file))
        {
            File.Move(file, file + ".corrupt", overwrite: true);
        }
        root[key] = value?.DeepClone();
        AtomicFile.WriteAllText(file, root.ToJsonString(WriteOptions));
    }

    private static JsonObject Parse(string? text, out bool corrupt)
    {
        corrupt = false;
        if (text is null)
        {
            return new JsonObject();
        }
        try
        {
            if (JsonNode.Parse(text) is JsonObject obj)
            {
                return obj;
            }
        }
        catch (JsonException)
        {
        }
        catch (ArgumentException)
        {
            // Duplicate keys in the object.
        }
        corrupt = true;
        return new JsonObject();
    }
}
