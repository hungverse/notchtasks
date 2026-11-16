using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NotchTasks.Core.Asks;

/// JSON helpers for the asks code. Reads mirror providers.js (isObj, str); writes match JSON.stringify
/// (compact, and no escaping of non-ASCII or of <, >, &, which the default encoder would escape).
/// Only JsonNode is used: no reflection-based serialization, so the Core stays AOT-safe.
public static class AsksJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// The string value of a JSON node, or null when it is not a string.
    public static string? Str(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    /// The boolean value of a JSON node, or null when it is not a boolean.
    public static bool? Bool(JsonNode? node) =>
        node is JsonValue v && (v.GetValueKind() == JsonValueKind.True || v.GetValueKind() == JsonValueKind.False)
            ? v.GetValue<bool>()
            : null;

    /// The compact JSON text of a node, the way JSON.stringify writes it.
    public static string ToText(JsonNode node) => node.ToJsonString(Options);

    /// A JSON file parsed, or null when it is missing, unreadable or not JSON (and for a JSON null).
    public static JsonNode? ReadJsonOrNull(string file)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(file));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// The same JSON with object keys sorted at every level, as sameInput in asks.js compares them.
    public static string Canonical(JsonNode? node) => node switch
    {
        null => "null",
        JsonObject o => "{" + string.Join(",", o
            .Select(kv => kv.Key)
            .OrderBy(k => k, StringComparer.Ordinal)
            .Select(k => ToText(JsonValue.Create(k)!) + ":" + Canonical(o[k]))) + "}",
        JsonArray a => "[" + string.Join(",", a.Select(x => Canonical(x))) + "]",
        _ => ToText(node),
    };
}
