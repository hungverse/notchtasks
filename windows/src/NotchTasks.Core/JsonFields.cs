using System.Text.Json;

namespace NotchTasks.Core;

/// Reads of parsed JSON that never throw: a missing or wrong-typed field gives the default, as the Electron helpers do
/// (providers.js isObj, str, num, objArr). Used by the provider parsers that take JSON from other programs.
internal static class JsonFields
{
    /// The parsed document, or null when the text is not JSON. The caller disposes it.
    public static JsonDocument? TryParse(string text)
    {
        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// The named property of an object; the default (ValueKind Undefined) when e is not an object or has no such property.
    public static JsonElement Field(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : default;

    /// The text of a string value, else null.
    public static string? Str(JsonElement e) => e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    /// The value of a number, else null.
    public static double? Num(JsonElement e) => e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out var d) ? d : (double?)null;

    /// The value of an integer number, else null.
    public static int? Int(JsonElement e) => e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var i) ? i : (int?)null;

    /// True when e is an array and every entry is an object (an empty array is one).
    public static bool IsObjectList(JsonElement e) =>
        e.ValueKind == JsonValueKind.Array && e.EnumerateArray().All(x => x.ValueKind == JsonValueKind.Object);
}
