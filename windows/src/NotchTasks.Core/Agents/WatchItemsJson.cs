using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NotchTasks.Core.Agents;

/// The watch list as settings.json stores it ("items"). Each item is validated as electron/src/core/settings.js watchItem
/// does: the same field limits, and an item that fails is dropped while the rest are kept. Unknown fields are kept.
public static class WatchItemsJson
{
    /// The most items the list holds.
    public const int MaxItems = 64;

    private static readonly Regex IdPattern = new(@"^[A-Za-z0-9_-]{1,64}\z");

    private static readonly string[] KnownFields =
    {
        "id", "name", "kind", "process", "args", "childTasks", "enabled", "threshold", "bundleID", "exe", "cli", "build",
        "symbolName", "icon",
    };

    /// The list from a stored node. A missing node or one that is not a list gives the defaults. The first 64 entries are
    /// read; an entry that is not an object, or has a field that fails validation, is dropped.
    public static IReadOnlyList<WatchItem> Read(JsonNode? node) => Read(node, out _);

    /// As Read, and idsInvented is true when a kept item had no valid id, so a new one was made for it. The caller saves
    /// the list once so the ids stay.
    public static IReadOnlyList<WatchItem> Read(JsonNode? node, out bool idsInvented)
    {
        idsInvented = false;
        if (node is not JsonArray array)
        {
            return WatchItems.Defaults();
        }
        var items = new List<WatchItem>();
        foreach (var entry in array.Take(MaxItems))
        {
            var item = ParseItem(entry, out var invented);
            if (item is not null)
            {
                items.Add(item);
                idsInvented |= invented;
            }
        }
        return items;
    }

    /// The list as a JSON array. Unknown fields kept on each item are written back.
    public static JsonArray Write(IEnumerable<WatchItem> items)
    {
        // The params constructor, not Add<T>, which is not AOT-safe.
        return new JsonArray(items.Select(item => (JsonNode?)ToNode(item)).ToArray());
    }

    /// One item from a stored node, or null when it is not an object or a field fails validation.
    /// idInvented is true when the id was missing or invalid and a new one was made.
    private static WatchItem? ParseItem(JsonNode? node, out bool idInvented)
    {
        idInvented = false;
        if (node is not JsonObject o)
        {
            return null;
        }
        try
        {
            var id = IdOf(o, out idInvented);
            var kind = KindOf(o);
            var extra = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
            foreach (var (key, value) in o)
            {
                if (!KnownFields.Contains(key, StringComparer.Ordinal))
                {
                    extra[key] = value?.DeepClone();
                }
            }

            return new WatchItem(
                Id: id,
                Name: TextOr(o, "name", 80, string.Empty),
                Kind: kind,
                Process: TextOr(o, "process", 200, string.Empty),
                Args: OptText(o, "args", 200),
                BundleID: OptText(o, "bundleID", 200),
                Exe: OptAbsolutePath(o, "exe"),
                Threshold: OptNumber(o, "threshold", 0.5, 100),
                ChildTasks: BoolOr(o, "childTasks", false),
                Build: BoolOr(o, "build", false),
                Enabled: BoolOr(o, "enabled", true),
                Cli: OptText(o, "cli", 80),
                SymbolName: OptText(o, "symbolName", 80),
                Icon: OptIcon(o, "icon"))
            {
                Extra = extra,
            };
        }
        catch (FieldException)
        {
            idInvented = false;
            return null;
        }
    }

    private static JsonObject ToNode(WatchItem it)
    {
        var o = new JsonObject
        {
            ["id"] = it.Id,
            ["name"] = it.Name,
            ["kind"] = KindName(it.Kind),
            ["process"] = it.Process,
            ["args"] = it.Args,
            ["childTasks"] = it.ChildTasks,
            ["enabled"] = it.Enabled,
            ["threshold"] = it.Threshold,
            ["bundleID"] = it.BundleID,
            ["exe"] = it.Exe,
            ["cli"] = it.Cli,
            ["build"] = it.Build,
            ["symbolName"] = it.SymbolName,
            ["icon"] = it.Icon is null ? null : JsonNode.Parse(it.Icon),
        };
        foreach (var (key, value) in it.Extra)
        {
            if (!KnownFields.Contains(key, StringComparer.Ordinal))
            {
                o[key] = value?.DeepClone();
            }
        }
        return o;
    }

    /// The stored id when it is valid; otherwise a new one, with invented set.
    private static string IdOf(JsonObject o, out bool invented)
    {
        if (o["id"] is JsonValue v && v.TryGetValue(out string? id) && id is not null && IdPattern.IsMatch(id))
        {
            invented = false;
            return id;
        }
        invented = true;
        return Guid.NewGuid().ToString();
    }

    private static WatchKind KindOf(JsonObject o)
    {
        var raw = o["kind"] is JsonValue v && v.TryGetValue(out string? s) ? s : null;
        return raw switch
        {
            "process" => WatchKind.Process,
            "app" => WatchKind.App,
            "claude" => WatchKind.Claude,
            "codex" => WatchKind.Codex,
            "dev" => WatchKind.Dev,
            _ => throw new FieldException("kind"),
        };
    }

    private static string KindName(WatchKind kind) => kind switch
    {
        WatchKind.Process => "process",
        WatchKind.App => "app",
        WatchKind.Claude => "claude",
        WatchKind.Codex => "codex",
        _ => "dev",
    };

    /// A text field: absent or null gives the fallback; otherwise a string of at most max characters (code points).
    private static string TextOr(JsonObject o, string key, int max, string fallback)
    {
        var node = o[key];
        if (node is null)
        {
            return fallback;
        }
        if (node is JsonValue v && v.TryGetValue(out string? s) && s is not null && s.EnumerateRunes().Count() <= max)
        {
            return s;
        }
        throw new FieldException(key);
    }

    private static string? OptText(JsonObject o, string key, int max) =>
        o[key] is null ? null : TextOr(o, key, max, string.Empty);

    private static bool BoolOr(JsonObject o, string key, bool fallback)
    {
        var node = o[key];
        if (node is null)
        {
            return fallback;
        }
        if (node is JsonValue v && v.TryGetValue(out bool b))
        {
            return b;
        }
        throw new FieldException(key);
    }

    private static double? OptNumber(JsonObject o, string key, double min, double max)
    {
        var node = o[key];
        if (node is null)
        {
            return null;
        }
        if (node is JsonValue v && v.TryGetValue(out double d) && d >= min && d <= max)
        {
            return d;
        }
        throw new FieldException(key);
    }

    /// An absolute path up to 500 characters, or null when absent.
    private static string? OptAbsolutePath(JsonObject o, string key)
    {
        var node = o[key];
        if (node is null)
        {
            return null;
        }
        if (node is JsonValue v && v.TryGetValue(out string? s) && s is not null && s.Length <= 500 && Path.IsPathRooted(s))
        {
            return s;
        }
        throw new FieldException(key);
    }

    /// The badge object, kept as its compact JSON text. Absent or null gives null.
    private static string? OptIcon(JsonObject o, string key)
    {
        var node = o[key];
        if (node is null)
        {
            return null;
        }
        if (node is JsonObject obj)
        {
            return obj.ToJsonString();
        }
        throw new FieldException(key);
    }

    /// Thrown inside ParseItem for a field that fails validation; the item is then dropped.
    private sealed class FieldException(string field) : Exception($"watch item field '{field}' is invalid");
}
