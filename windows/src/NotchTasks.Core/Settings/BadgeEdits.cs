using System.Text.Json.Nodes;

namespace NotchTasks.Core.Settings;

/// Edits to the "badges" array of settings.json: the icons on the notch's two sides. Every edit returns a new array and
/// leaves the one it was given as it is. A badge keeps the fields this build does not know, as the Electron build does.
/// The shape of a new badge is electron's badge() default (src/core/settings.js).
public static class BadgeEdits
{
    /// Most icons on one side of the notch (the Mac's Badge.perSide; the Electron build's free limit).
    public const int PerSide = 3;

    /// The badge symbols offered, as the Segoe code each was stored as, four hex digits (stored as "seg:" + code). They draw as
    /// the Fluent icon ModuleGlyphs.Legacy names for the code, so a badge saved before the Fluent icons keeps its look.
    public static IReadOnlyList<string> SymbolCodes { get; } =
    [
        "E734", "E735", "E006", "E80F", "E713", "E721", "E710", "E74D", "E70F", "E72C",
        "E72E", "E77B", "E722", "E767", "E7E8", "E774", "E718", "E7C1", "E895",
    ];

    /// The stored symbol of a badge icon: "seg:" plus its code, so the Mac's SF Symbol names stay distinct.
    public static string SymbolFor(string code) => "seg:" + code;

    /// How many badges sit on one side.
    public static int CountOnSide(JsonArray badges, bool right) => badges.Count(node => RightOf(node) == right);

    /// A copy with a new badge at the end of its side, or an unchanged copy when that side is full. A sprite badge is drawn
    /// without a fill, as the Mac does (plain).
    public static JsonArray Add(JsonArray badges, bool right, string? sprite)
    {
        var result = Copy(badges);
        if (CountOnSide(badges, right) >= PerSide)
        {
            return result;
        }
        JsonNode badge = NewBadge(right, sprite);
        result.Add(badge);
        return result;
    }

    /// A copy without the badge that has this id.
    public static JsonArray Remove(JsonArray badges, string id)
    {
        var result = new JsonArray();
        foreach (var node in badges)
        {
            if (IdOf(node) != id)
            {
                result.Add(node?.DeepClone());
            }
        }
        return result;
    }

    /// A copy in which the badge with this id is changed by `change`. Fields the change does not touch are kept.
    public static JsonArray Update(JsonArray badges, string id, Action<JsonObject> change)
    {
        var result = Copy(badges);
        foreach (var node in result)
        {
            if (node is JsonObject badge && IdOf(badge) == id)
            {
                change(badge);
                break;
            }
        }
        return result;
    }

    /// A copy in which the badge with this id is moved to position `to` among the icons on its own side (clamped to the side).
    /// The other side keeps its order and its places in the array.
    public static JsonArray Move(JsonArray badges, string id, int to)
    {
        var nodes = badges.Select(node => node?.DeepClone()).ToList();
        var slots = Enumerable.Range(0, nodes.Count).Where(i => RightOf(nodes[i]) == RightOf(FindById(nodes, id))).ToList();
        var side = slots.Select(i => nodes[i]).ToList();
        var from = side.FindIndex(node => IdOf(node) == id);
        if (from < 0)
        {
            return Copy(badges);
        }

        var moved = side[from];
        side.RemoveAt(from);
        side.Insert(Math.Clamp(to, 0, side.Count), moved);
        for (var k = 0; k < slots.Count; k++)
        {
            nodes[slots[k]] = side[k];
        }

        var result = new JsonArray();
        foreach (var node in nodes)
        {
            result.Add(node);
        }
        return result;
    }

    /// Makes a badge show an animated icon: the sprite is set, the text and picture are cleared, and the fill is turned off
    /// (plain) when none was chosen, as the Mac does. The symbol is kept.
    public static void ApplySprite(JsonObject badge, string sprite)
    {
        if (badge["plain"] is null)
        {
            badge["plain"] = JsonValue.Create(true);
        }
        badge["sprite"] = JsonValue.Create(sprite);
        badge["frames"] = null;
        badge["fps"] = null;
        badge["image"] = null;
        badge["text"] = null;
    }

    /// Makes a badge show a symbol: the symbol is set and the animation, text and picture are cleared.
    public static void ApplySymbol(JsonObject badge, string symbol)
    {
        badge["symbol"] = JsonValue.Create(symbol);
        badge["sprite"] = null;
        badge["frames"] = null;
        badge["fps"] = null;
        badge["image"] = null;
        badge["text"] = null;
    }

    private static JsonObject NewBadge(bool right, string? sprite) => new()
    {
        ["id"] = Guid.NewGuid().ToString(),
        ["symbol"] = "star",
        ["image"] = null,
        ["text"] = null,
        ["color"] = null,
        ["fg"] = null,
        ["plain"] = sprite is null ? null : JsonValue.Create(true),
        ["shape"] = "circle",
        ["border"] = false,
        ["right"] = right,
        ["sprite"] = sprite is null ? null : JsonValue.Create(sprite),
        ["frames"] = null,
        ["fps"] = null,
    };

    private static JsonArray Copy(JsonArray badges) => (JsonArray)badges.DeepClone();

    private static JsonNode? FindById(IEnumerable<JsonNode?> nodes, string id) => nodes.FirstOrDefault(node => IdOf(node) == id);

    /// The id of a badge node, or null.
    public static string? IdOf(JsonNode? node) =>
        node is JsonObject badge && badge["id"] is JsonValue value && value.TryGetValue(out string? id) ? id : null;

    /// Whether a badge node sits on the right side of the notch.
    public static bool RightOf(JsonNode? node) =>
        node is JsonObject badge && badge["right"] is JsonValue value && value.TryGetValue(out bool right) && right;
}
