using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using NotchTasks.Core.Sounds;

namespace NotchTasks.Core.Settings;

/// The result of reading a profile: the values to save, keyed by the settings' keys, and why the file was refused (null when it
/// was read). Keys the file lacks, or whose value is not valid here, are left out.
public sealed record ProfileRead(IReadOnlyDictionary<string, JsonNode?> Values, string? Error);

/// The profile file (Customize.swift Profile): the customization in one .json file, in the Mac's format, so a profile moves
/// between the Mac and Windows. Export writes the Mac's 22 keys under the Mac's names and values; Read keeps the keys Windows
/// has, checked as the settings check them, and ignores the rest (haptics, the lock screen, a later version's keys).
public static class SettingsProfile
{
    /// The largest profile file read: a profile is a few kilobytes, so a bigger file is not one.
    public const long MaxFileBytes = 1_000_000;

    /// The Mac's Profile keys, in the Mac's spelling. Windows reads and writes each under the same name.
    private static readonly string[] Keys =
    {
        "modules", "disabled", "expandMode", "hoverDelay", "haptics", "sounds", "systemNotifications", "size", "accent",
        "customAccent", "menuBarIcon", "doneAfter", "limitWarn", "background", "gradientA", "gradientB", "toastStyle", "motion",
        "moduleColors", "placements", "soundMap", "volume",
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// The profile text of the store's customization, with sorted keys as the Mac writes it. The Mac's haptics key is written on,
    /// its default, because Windows has no haptics and the Mac's Profile needs the key to read the file.
    public static string Export(SettingsStore store)
    {
        var values = new SortedDictionary<string, JsonNode?>(StringComparer.Ordinal)
        {
            ["modules"] = Strings(store.Modules),
            ["disabled"] = Strings(store.Disabled),
            ["expandMode"] = JsonValue.Create(store.ExpandMode),
            ["hoverDelay"] = JsonValue.Create(store.HoverDelay),
            ["haptics"] = JsonValue.Create(true),
            ["sounds"] = JsonValue.Create(store.Sounds),
            ["systemNotifications"] = JsonValue.Create(store.SystemNotifications),
            ["size"] = JsonValue.Create(store.Size),
            ["accent"] = JsonValue.Create(store.Accent),
            ["customAccent"] = JsonValue.Create(store.CustomAccent),
            ["menuBarIcon"] = JsonValue.Create(store.MenuBarIcon),
            ["doneAfter"] = JsonValue.Create(store.DoneAfter),
            ["limitWarn"] = JsonValue.Create(store.LimitWarn),
            ["background"] = JsonValue.Create(store.Background),
            ["gradientA"] = JsonValue.Create(store.GradientA),
            ["gradientB"] = JsonValue.Create(store.GradientB),
            ["toastStyle"] = JsonValue.Create(store.ToastStyle),
            ["motion"] = JsonValue.Create(store.Motion),
            ["moduleColors"] = Dict(store.ModuleColors),
            ["placements"] = Dict(store.Placements),
            ["soundMap"] = Dict(store.SoundMap),
            ["volume"] = JsonValue.Create(store.Volume),
        };
        return new JsonObject(values).ToJsonString(WriteOptions);
    }

    /// Reads a profile's text. A text that is not a JSON object, or has none of the profile's keys, is refused with the
    /// profile error. Otherwise each key Windows has is kept when its value is valid, and the rest are left out.
    public static ProfileRead Read(string text)
    {
        JsonObject? json;
        try
        {
            json = JsonNode.Parse(text) as JsonObject;
        }
        catch (JsonException)
        {
            json = null;
        }
        if (json is null)
        {
            return NotAProfile();
        }

        var values = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var key in Keys)
        {
            if (json[key] is { } node && Clean(key, node) is { } clean)
            {
                values[key] = clean;
            }
        }
        return values.Count == 0 ? NotAProfile() : new ProfileRead(values, null);
    }

    /// The refusal of a file that is not a profile: the profile error and no values.
    public static ProfileRead NotAProfile() =>
        new(new Dictionary<string, JsonNode?>(StringComparer.Ordinal), L10n.L("This file isn't a NotchTasks profile."));

    /// The value of one key as the settings keep it, as a new node; null when the value is not valid for the key.
    private static JsonNode? Clean(string key, JsonNode node) => key switch
    {
        "modules" or "disabled" => IdList(node),
        "expandMode" => Choice(node, Choices.ExpandModes.Select(o => o.Value)),
        "size" => Choice(node, Choices.Sizes.Select(o => o.Value)),
        "background" => Choice(node, Choices.Backgrounds.Select(o => o.Value)),
        "toastStyle" => Choice(node, Choices.ToastStyles.Select(o => o.Value)),
        "motion" => Choice(node, Choices.Motions.Select(o => o.Value)),
        "accent" => Choice(node, Appearance.AccentNames),
        "customAccent" or "gradientA" or "gradientB" => Hex(node),
        "hoverDelay" => Ranged(node, 0, 1),
        "volume" => Ranged(node, 0, 1),
        "doneAfter" => Ranged(node, 5, 600),
        "limitWarn" => Ranged(node, 50, 100),
        "sounds" or "systemNotifications" or "menuBarIcon" => Flag(node),
        "moduleColors" => Map(node, (id, value) => IsModule(id) ? Hex(value) : null),
        "placements" => Map(node, (id, value) => IsModule(id) ? Placement(value) : null),
        "soundMap" => Map(node, (id, value) => IsSoundEvent(id) ? Sound(value) : null),
        _ => null,
    };

    /// A list of module ids, the ones Windows has (the Mac's extra modules are dropped), each once.
    private static JsonArray? IdList(JsonNode node)
    {
        if (node is not JsonArray array)
        {
            return null;
        }
        var ids = new List<string>();
        foreach (var item in array)
        {
            if (Text(item) is { } id && IsModule(id) && !ids.Contains(id))
            {
                ids.Add(id);
            }
        }
        return Strings(ids);
    }

    /// A value from a list of allowed values.
    private static JsonValue? Choice(JsonNode node, IEnumerable<string> allowed) =>
        Text(node) is { } text && allowed.Contains(text) ? JsonValue.Create(text) : null;

    /// A #RRGGBB colour.
    private static JsonValue? Hex(JsonNode node) =>
        Text(node) is { } hex && hex.Length == 7 && hex[0] == '#' && hex.Skip(1).All(Uri.IsHexDigit) ? JsonValue.Create(hex) : null;

    /// A number from `min` to `max`.
    private static JsonValue? Ranged(JsonNode node, double min, double max) =>
        node is JsonValue v && v.TryGetValue(out double number) && double.IsFinite(number) && number >= min && number <= max
            ? JsonValue.Create(number)
            : null;

    /// A switch: true or false.
    private static JsonValue? Flag(JsonNode node) =>
        node is JsonValue v && v.TryGetValue(out bool flag) ? JsonValue.Create(flag) : null;

    /// A JSON object with each entry that `keep` gives a new value for, as a new object; null when the node is not an object.
    /// `keep` gets the key and the value and returns the value to keep, or null to drop the entry.
    private static JsonObject? Map(JsonNode node, Func<string, JsonNode, JsonNode?> keep)
    {
        if (node is not JsonObject obj)
        {
            return null;
        }
        var map = new JsonObject();
        foreach (var (key, value) in obj)
        {
            if (value is not null && keep(key, value) is { } clean)
            {
                map[key] = clean;
            }
        }
        return map;
    }

    /// Whether a module id is one this PC has: the Mac's levels module is not on Windows, so a profile's entries for it are dropped.
    private static bool IsModule(string id) => ModuleCatalog.OnWindows(id);

    /// A placement: "auto", "left" or "right" (Mac placements).
    private static JsonValue? Placement(JsonNode node) =>
        Text(node) is { } place && place is "auto" or "left" or "right" ? JsonValue.Create(place) : null;

    /// A sound: "" (silent), one of the Mac's sound names this PC has, or a sound file that exists here. A Mac file path is
    /// not on this PC, so its event keeps the default sound.
    private static JsonValue? Sound(JsonNode node)
    {
        var sound = Text(node);
        if (sound is null)
        {
            return null;
        }
        var ok = sound.Length == 0 || SoundFiles.Names.Contains(sound)
            || (Path.IsPathFullyQualified(sound) && File.Exists(sound));
        return ok ? JsonValue.Create(sound) : null;
    }

    private static bool IsSoundEvent(string id) => SoundPacks.Events.Any(e => e.Id == id);

    /// A string node's text, or null when the node is not a string.
    private static string? Text(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue(out string? text) ? text : null;

    private static JsonArray Strings(IEnumerable<string> items) =>
        new(items.Select(item => (JsonNode?)JsonValue.Create(item)).ToArray());

    /// A map of settings to a JSON object, for the export.
    private static JsonObject Dict(IEnumerable<KeyValuePair<string, string>> map) =>
        new(map.Select(pair => new KeyValuePair<string, JsonNode?>(pair.Key, JsonValue.Create(pair.Value))));
}
