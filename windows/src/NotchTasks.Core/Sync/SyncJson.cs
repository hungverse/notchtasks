using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NotchTasks.Core.Sync;

public static class SyncJson
{
    /// One shared set of options so every sync JSON text is written the way JSON.stringify writes it (no escaping of non-ASCII, no indentation).
    public static JsonSerializerOptions Options { get; } = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    /// Compact JSON text of the node, the bytes a sealed box and a REST body carry.
    public static string Write(JsonNode node) => node.ToJsonString(Options);
}
