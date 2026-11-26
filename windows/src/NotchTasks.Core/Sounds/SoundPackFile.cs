using System.Text.Json;
using System.Text.Json.Nodes;

namespace NotchTasks.Core.Sounds;

/// The result of reading a `.notchsounds` file: the events' sounds ready for the sound map, the volume it sets (null when the
/// file has none, clamped to 0..1 otherwise), or the reason it could not be used (Error, with Map empty).
public sealed record SoundPackImport(IReadOnlyDictionary<string, string> Map, double? Volume, string? Error);

/// A `.notchsounds` file (Customize.swift SoundPackFile): JSON with each event's sound and the custom audio files themselves
/// (base64), so a pack works on another computer. Built-in sounds travel by name. The custom files are kept in `folder`,
/// which lives in the app's own data folder, so they survive the original being moved or deleted.
public sealed class SoundPackFile
{
    /// The format tag every pack carries, and the version this build writes.
    public const string Format = "notchtasks.sounds";
    public const int Version = 1;

    /// A notification sound is a few hundred KB; a larger file is refused.
    public const int MaxFile = 8_000_000;

    /// A pack file this big is refused before it is read (the Mac's 100 MB check).
    public const long MaxPackFile = 100_000_000;

    /// The audio types this PC plays (WAV through PlaySound, the rest through MediaPlayer). Other types are skipped on import.
    public static IReadOnlyList<string> AudioTypes { get; } = ["wav", "mp3", "m4a", "aac", "wma"];

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public SoundPackFile(string folder)
    {
        Folder = folder;
    }

    /// Where the custom sounds are copied: the Sounds folder in the app's data folder.
    public string Folder { get; }

    /// Copies a chosen audio file into Folder and returns its new path, or null when the file is too large or not an audio type.
    public string? Keep(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > MaxFile)
            {
                return null;
            }
            return Store(File.ReadAllBytes(path), Path.GetFileName(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// Writes the bytes into Folder under a clean version of `name`, numbered (" 2", " 3") when a different file has the name;
    /// the same bytes reuse the file already there. Returns its path, or null when the name's type is not an audio type
    /// or the bytes are too large.
    public string? Store(byte[] data, string name)
    {
        var clean = CleanName(name);
        var ext = Path.GetExtension(clean).TrimStart('.').ToLowerInvariant();
        if (clean.Length == 0 || data.Length > MaxFile || !AudioTypes.Contains(ext))
        {
            return null;
        }

        var baseName = Path.GetFileNameWithoutExtension(clean);
        Directory.CreateDirectory(Folder);
        var dest = Path.Combine(Folder, clean);
        for (var n = 2; File.Exists(dest) && !SameBytes(dest, data); n++)
        {
            dest = Path.Combine(Folder, $"{baseName} {n}.{ext}");
        }
        if (!File.Exists(dest))
        {
            File.WriteAllBytes(dest, data);
        }
        return dest;
    }

    /// The pack's JSON for a sound map and a volume. Built-in names and "" go by name; a file path goes as "file:<name>" with
    /// its bytes in `files`. Unknown events and a file that cannot be read (missing, too large) are left out.
    public string Export(IReadOnlyDictionary<string, string> map, double volume)
    {
        var sounds = new Dictionary<string, string>(StringComparer.Ordinal);
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (eventId, sound) in map)
        {
            if (!IsEvent(eventId))
            {
                continue;
            }
            if (!Path.IsPathRooted(sound))
            {
                sounds[eventId] = sound;
                continue;
            }
            var encoded = ReadBase64(sound);
            if (encoded is null)
            {
                continue;
            }
            var name = UniqueKey(files, Path.GetFileName(sound), encoded);
            files[name] = encoded;
            sounds[eventId] = "file:" + name;
        }

        var clamped = double.IsNaN(volume) ? 1 : Math.Clamp(volume, 0, 1);
        var pack = new JsonObject
        {
            ["format"] = Format,
            ["version"] = Version,
            ["volume"] = clamped,
            ["sounds"] = ToObject(sounds),
            ["files"] = ToObject(files),
        };
        return pack.ToJsonString(WriteOptions);
    }

    /// Reads a pack: its sounds that this PC can use, kept in Folder where they are files, and its volume. A text that is not a
    /// pack gives the error "This file isn't a NotchTasks sound pack."; a pack with nothing usable gives "No usable sounds in
    /// this file." `systemNames` are the built-in sound names this PC has.
    public SoundPackImport Import(string json, IReadOnlyCollection<string> systemNames)
    {
        var pack = ParseDocument(json);
        if (pack is null)
        {
            return Failure(L10n.L("This file isn't a NotchTasks sound pack."));
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (eventId, value) in pack.Sounds)
        {
            if (!IsEvent(eventId))
            {
                continue;
            }
            if (value.Length == 0 || systemNames.Contains(value))
            {
                map[eventId] = value;
                continue;
            }
            if (!value.StartsWith("file:", StringComparison.Ordinal) || !pack.Files.TryGetValue(value[5..], out var encoded))
            {
                continue;
            }
            var bytes = DecodeBase64(encoded);
            var path = bytes is null ? null : Store(bytes, value[5..]);
            if (path is not null)
            {
                map[eventId] = path;
            }
        }

        if (map.Count == 0)
        {
            return Failure(L10n.L("No usable sounds in this file."));
        }
        var volume = pack.Volume is null ? (double?)null : Math.Clamp(pack.Volume.Value, 0, 1);
        return new SoundPackImport(map, volume, null);
    }

    private static SoundPackImport Failure(string error) => new(new Dictionary<string, string>(), null, error);

    private static bool IsEvent(string eventId) => SoundPacks.Events.Any(e => e.Id == eventId);

    /// A file name with no folder part, no leading dot and no characters a path cannot have.
    private static string CleanName(string name)
    {
        var last = name.Split('/', '\\').Last();
        var invalid = Path.GetInvalidFileNameChars();
        return new string(last.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).TrimStart('.');
    }

    private static bool SameBytes(string path, byte[] data)
    {
        try
        {
            return File.ReadAllBytes(path).AsSpan().SequenceEqual(data);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string? ReadBase64(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > MaxFile)
            {
                return null;
            }
            return Convert.ToBase64String(File.ReadAllBytes(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static byte[]? DecodeBase64(string encoded)
    {
        try
        {
            var bytes = Convert.FromBase64String(encoded);
            return bytes.Length <= MaxFile ? bytes : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// The name, numbered when `files` holds a different file under it (the same content reuses the name).
    private static string UniqueKey(Dictionary<string, string> files, string name, string encoded)
    {
        var baseName = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        var candidate = name;
        for (var n = 2; files.TryGetValue(candidate, out var existing) && existing != encoded; n++)
        {
            candidate = $"{baseName} {n}{ext}";
        }
        return candidate;
    }

    private static PackParse? ParseDocument(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.String
                || format.GetString() != Format
                || !root.TryGetProperty("sounds", out var sounds) || sounds.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var soundMap = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in sounds.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String)
                {
                    return null;
                }
                soundMap[property.Name] = property.Value.GetString() ?? string.Empty;
            }

            var files = new Dictionary<string, string>(StringComparer.Ordinal);
            if (root.TryGetProperty("files", out var fileObject))
            {
                if (fileObject.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }
                foreach (var property in fileObject.EnumerateObject())
                {
                    if (property.Value.ValueKind != JsonValueKind.String)
                    {
                        return null;
                    }
                    files[property.Name] = property.Value.GetString() ?? string.Empty;
                }
            }

            double? volume = null;
            if (root.TryGetProperty("volume", out var volumeValue))
            {
                if (volumeValue.ValueKind != JsonValueKind.Number)
                {
                    return null;
                }
                volume = volumeValue.GetDouble();
            }
            return new PackParse(soundMap, files, volume);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonObject ToObject(Dictionary<string, string> strings)
    {
        var node = new JsonObject();
        foreach (var (key, value) in strings)
        {
            node[key] = value;
        }
        return node;
    }

    private sealed record PackParse(Dictionary<string, string> Sounds, Dictionary<string, string> Files, double? Volume);
}
