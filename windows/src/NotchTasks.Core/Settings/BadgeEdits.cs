using System.Globalization;
using System.Text.Json.Nodes;

namespace NotchTasks.Core.Settings;

/// Edits to the "badges" array of settings.json: the icons on the notch's two sides. Every edit returns a new array and
/// leaves the one it was given as it is. A badge keeps the fields this build does not know, as the Electron build does.
/// The shape of a new badge is electron's badge() default (src/core/settings.js).
public static class BadgeEdits
{
    /// Most icons on one side of the notch (the Mac's Badge.perSide; the Electron build's free limit).
    public const int PerSide = 3;

    /// Most characters a badge's text shows (the Mac's editor and settings check both allow eight).
    public const int TextMax = 8;

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

    /// Makes a badge show letters or an emoji: up to eight characters (the Mac's editor limit, counted as the user sees them), and
    /// the picture and animation are cleared. Empty text removes it, so the badge shows its symbol again.
    public static void ApplyText(JsonObject badge, string text)
    {
        var info = new StringInfo(text);
        var clipped = info.LengthInTextElements > TextMax ? info.SubstringByTextElements(0, TextMax) : text;
        badge["text"] = clipped.Length == 0 ? null : JsonValue.Create(clipped);
        badge["image"] = null;
        badge["sprite"] = null;
        badge["frames"] = null;
        badge["fps"] = null;
    }

    /// Makes a badge show a picture (a copy in the Badges folder, from BadgeImages). The text and animation are cleared; the fill
    /// is kept, as the Mac keeps it.
    public static void ApplyPicture(JsonObject badge, string path)
    {
        badge["image"] = JsonValue.Create(path);
        badge["text"] = null;
        badge["sprite"] = null;
        badge["frames"] = null;
        badge["fps"] = null;
    }

    /// Makes a badge show an animated GIF: the picture is set and the text and sprite are cleared. With no fill chosen yet it
    /// turns the fill off, so the animation stands on the notch, as the Mac does (BadgesPane.useAnimation).
    public static void ApplyAnimation(JsonObject badge, string path)
    {
        if (!IsAnimated(badge) && badge["plain"] is null)
        {
            badge["plain"] = JsonValue.Create(true);
        }
        badge["sprite"] = null;
        badge["text"] = null;
        badge["frames"] = null;
        badge["fps"] = null;
        badge["image"] = JsonValue.Create(path);
    }

    /// Whether a badge animates: a built-in sprite, a spritesheet (it has a frame count), or a GIF picture (the Mac's Badge.animated).
    public static bool IsAnimated(JsonNode? node) =>
        node is JsonObject badge
        && (badge["sprite"] is JsonValue
            || badge["frames"] is JsonValue
            || (ImageOf(badge) is { } image && image.EndsWith(".gif", StringComparison.OrdinalIgnoreCase)));

    /// The picture path of a badge, or null when it has none.
    public static string? ImageOf(JsonObject badge) =>
        badge["image"] is JsonValue value && value.TryGetValue(out string? image) && image.Length > 0 ? image : null;

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
