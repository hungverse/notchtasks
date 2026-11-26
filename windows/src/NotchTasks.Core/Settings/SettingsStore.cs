using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NotchTasks.Core.Agents;

namespace NotchTasks.Core.Settings;

/// The settings file (settings.json), shared with the Electron and Mac builds. Keys this build does not know are kept on save.
/// Changed and PropertyChanged are raised on the thread that made the change: Set raises them on the caller's thread,
/// an edit made outside the app raises them on a thread-pool thread. The WPF app must marshal them to its dispatcher.
public sealed class SettingsStore : INotifyPropertyChanged, IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(100);

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly Regex LanguagePattern = new(@"^([a-zA-Z]{2,3}(-[A-Za-z]{2,4})?)?$");
    private static readonly Regex BadgeIdPattern = new(@"^[\w-]{1,64}$");

    /// Longest tool name in toolIcons and seenTools, and how many seen tools are kept (settings.js).
    private const int ToolNameMax = 60;
    private const int SeenToolsMax = 200;
    private const int BadgeSymbolMax = 80;
    private const int BadgeTextMax = 8;
    private static readonly string[] BadgeShapes = { "circle", "rounded" };

    private static readonly string[] ExpandModes = { "hover", "click" };
    private static readonly string[] Sizes = { "compact", "regular" };
    private static readonly string[] Backgrounds = { "black", "glass", "gradient", "glow" };
    private static readonly string[] ToastStyles = { "island", "compact", "card", "banner" };
    private static readonly string[] Motions = { "smooth", "bouncy", "snappy", "reduced" };
    private static readonly string[] DefaultModules =
        { "agents", "ci", "media", "timer", "shelf", "limits", "battery", "network", "downloads", "health" };
    private static readonly string[] DefaultDisabled = { "timer" };

    /// The Default sound pack of the Mac (SoundPack.all[0]).
    private static readonly IReadOnlyDictionary<string, string> DefaultSoundMap = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["taskDone"] = "Glass", ["agentAsk"] = "Ping", ["ciPassed"] = "Hero", ["ciFailed"] = "Basso", ["limitWarn"] = "Funk",
        ["meeting"] = "Ping", ["timerDone"] = "Glass", ["charging"] = "Pop", ["unplugged"] = "", ["lowBattery"] = "Funk",
        ["deviceOn"] = "Pop", ["deviceOff"] = "Bottle", ["callStart"] = "Tink", ["callEnd"] = "Bottle", ["sensorOn"] = "",
        ["authFail"] = "Basso", ["netDown"] = "Basso", ["netUp"] = "Tink", ["downloadDone"] = "Glass", ["warning"] = "Funk",
        ["open"] = "", ["close"] = "",
    };

    private readonly string _file;
    private readonly object _gate = new();
    private JsonObject _root = new();
    private string? _lastSeenText;  // text last written by this store or read from the file; a watcher event that reads back the same text is ignored
    private bool _corrupt;          // the file on disk is unreadable; the next Set moves it aside
    private FileSystemWatcher? _watcher;
    private Timer? _debounce;
    private bool _disposed;
    private IReadOnlyList<WatchItem>? _defaultItems;  // made on first read when the file has no valid list; guarded by _gate
    private IReadOnlyList<WatchItem>? _items;         // the parsed watch list, kept until _root changes; null = parse again; guarded by _gate

    private SettingsStore(string file)
    {
        _file = file;
    }

    /// Keys that changed, raised after each Set and after each reload of an edited file.
    public event Action<IReadOnlyCollection<string>>? Changed;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// Reads the file (a missing file or a corrupt one gives the defaults) and watches it for outside edits.
    public static SettingsStore Load(string file)
    {
        var store = new SettingsStore(Path.GetFullPath(file));
        store.LoadInitial();
        store.StartWatching();
        return store;
    }

    public T? Get<T>(string key) => TryGet(key, out T? value) ? value : default;

    /// Saves one key at once (atomic write) and raises Changed and PropertyChanged for it.
    /// The file is read first when it changed since this store last saw it (an outside edit still waiting for the
    /// watcher's debounce, or a file that was locked at load), so the keys in it are kept, not overwritten.
    /// Throws IOException, and changes nothing, while the file is still locked.
    public void Set(string key, JsonNode? value)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        SetMany(new Dictionary<string, JsonNode?>(StringComparer.Ordinal) { [key] = value });
    }

    /// Saves several keys in one atomic write and raises Changed once for them, so a theme or an imported profile reaches
    /// every open pane in one step. Otherwise as Set: a locked file changes nothing and throws IOException.
    public void SetMany(IReadOnlyDictionary<string, JsonNode?> values)
    {
        foreach (var key in values.Keys)
        {
            ArgumentException.ThrowIfNullOrEmpty(key);
        }
        if (values.Count == 0)
        {
            return;
        }

        IReadOnlyList<string> reloaded;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            reloaded = SyncFromDisk();
            var next = (JsonObject)_root.DeepClone();
            if (_corrupt)
            {
                MoveCorruptAside();
                next = new JsonObject();
            }
            foreach (var (key, value) in values)
            {
                next[key] = value?.DeepClone();
            }
            var text = next.ToJsonString(WriteOptions);
            AtomicFile.WriteAllText(_file, text);
            _root = next;
            _lastSeenText = text;
            _corrupt = false;
            SanitizeItems();
        }
        Raise(reloaded.Concat(values.Keys).Distinct(StringComparer.Ordinal).ToList());
    }

    /// Adopts the file's keys when its text is not the text this store last saw. Caller holds _gate. Returns the keys that
    /// changed; a missing file changes nothing. Throws IOException, and changes nothing, while the file is locked.
    private IReadOnlyList<string> SyncFromDisk()
    {
        string? text;
        try
        {
            text = AtomicFile.ReadAllTextOrNullWithRetry(_file);
        }
        catch (UnauthorizedAccessException e)
        {
            throw new IOException(e.Message, e);
        }

        if (text is null || text == _lastSeenText)
        {
            return Array.Empty<string>();
        }
        _lastSeenText = text;
        if (!TryParseObject(text, out var parsed))
        {
            _corrupt = true;
            return Array.Empty<string>();
        }
        var changed = DiffKeys(_root, parsed);
        _root = parsed;
        _corrupt = false;
        SanitizeItems();
        return changed;
    }

    /// Adopts the items of _root. When an item had no valid id and one was made for it, the list is saved once, so the id
    /// is kept from then on. The saved text becomes the last seen text, so the watcher does not read the save back as an
    /// outside edit (and an unchanged list is never saved again). If the file is locked, the ids stay in memory and the
    /// next save keeps them. Takes _gate itself (reentrant), so it may run inside Set or a load.
    private void SanitizeItems()
    {
        lock (_gate)
        {
            _items = null;
            if (_root["items"] is not JsonArray)
            {
                return;
            }
            var list = WatchItemsJson.Read(_root["items"], out var invented);
            _items = list;
            if (!invented)
            {
                return;
            }
            _root["items"] = WatchItemsJson.Write(list);
            var text = _root.ToJsonString(WriteOptions);
            try
            {
                AtomicFile.WriteAllText(_file, text);
                _lastSeenText = text;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Locked for now: the ids stay in memory and are saved with the next write.
            }
        }
    }

    public string ExpandMode => Choice("expandMode", "hover", ExpandModes);

    public double HoverDelay => Ranged("hoverDelay", 0.15, 0, 1);

    public bool Sounds => Flag("sounds", true);

    public string Size => Choice("size", "compact", Sizes);

    public string Accent => Choice("accent", "auto", Appearance.AccentNames);

    public string CustomAccent => Hex("customAccent", "#FF9F0A");

    public string Background => Choice("background", "black", Backgrounds);

    public string GradientA => Hex("gradientA", "#1E1B4B");

    public string GradientB => Hex("gradientB", "#000000");

    public string ToastStyle => Choice("toastStyle", "island", ToastStyles);

    public string Motion => Choice("motion", "bouncy", Motions);

    public double Volume => Ranged("volume", 0.8, 0, 1);

    public bool OnboardingDone => Flag("onboardingDone", false);

    public string Language => TryGet("language", out string? value) && value is not null && LanguagePattern.IsMatch(value) ? value : "";

    public IReadOnlyList<string> Modules => StringList("modules", DefaultModules);

    /// The watch list (settings "items", at most 64). A missing or invalid list gives the defaults, made once per store.
    /// The list is parsed once and kept until the settings change, so item ids stay the same between reads.
    public IReadOnlyList<WatchItem> Items
    {
        get
        {
            lock (_gate)
            {
                if (_items is null)
                {
                    _items = _root["items"] is JsonArray
                        ? WatchItemsJson.Read(_root["items"])
                        : (_defaultItems ??= WatchItemsJson.Read(null));
                }
                return _items;
            }
        }
    }

    /// Saves the watch list (at most 64 items; more are dropped). Raises Changed and PropertyChanged for "items". Returns null when
    /// it is saved. An item the reader would drop (a field past its limit) is refused: nothing is saved and the reason is returned.
    public string? SetItems(IReadOnlyList<WatchItem> items)
    {
        var kept = items.Take(WatchItemsJson.MaxItems).ToList();
        if (kept.Any(item => !WatchItemsJson.Keeps(item)))
        {
            return L10n.L("A field is too long for the watch list.");
        }
        Set("items", WatchItemsJson.Write(kept));
        return null;
    }

    /// Seconds a task must run before its completion is announced, 5 to 600 (Mac doneAfter).
    public double DoneAfter => Ranged("doneAfter", 30, 5, 600);

    public IReadOnlyList<string> Disabled => StringList("disabled", DefaultDisabled);

    /// CPU and RAM on the open notch (Mac systemLoad).
    public bool SystemLoad => Flag("systemLoad", true);

    /// Windows only: the temperature helper is on (it runs elevated, so it is off until the user turns it on). Off by default.
    public bool SensorHelper => Flag("sensorHelper", false);

    /// Windows only: the helper also starts at logon, through a scheduled task the user agreed to. Off by default.
    public bool SensorHelperAtSignIn => Flag("sensorHelperAtSignIn", false);

    /// System notifications for the alerts (Mac systemNotifications).
    public bool SystemNotifications => Flag("systemNotifications", true);

    /// The percentage of an AI limit that triggers a warning, 50 to 100 (Mac limitWarn).
    public double LimitWarn => Ranged("limitWarn", 80, 50, 100);

    /// The opt-in to read Claude's 5 h and 7 d limits from the Claude Code CLI (`claude -p /usage`), set by the user's Allow in the
    /// Watching pane. Off by default; never part of a profile, so importing one never starts the CLI.
    public bool ClaudeLimits => Flag("claudeLimits", false);

    /// The tray icon is shown (Mac menuBarIcon).
    public bool MenuBarIcon => Flag("menuBarIcon", true);

    /// The analytics opt-out, stored as the Mac and Electron store it ("analytics.off"): analytics are on unless this is true.
    public bool AnalyticsOff => Flag("analytics.off", false);

    /// Event id to sound name (or file path); "" is silent. Events missing from the file keep the Default pack's sound.
    public IReadOnlyDictionary<string, string> SoundMap
    {
        get
        {
            var map = new Dictionary<string, string>(DefaultSoundMap, StringComparer.Ordinal);
            lock (_gate)
            {
                if (_root["soundMap"] is JsonObject saved)
                {
                    foreach (var (key, node) in saved)
                    {
                        if (node is JsonValue v && v.TryGetValue(out string? sound) && sound is not null)
                        {
                            map[key] = sound;
                        }
                    }
                }
            }
            return map;
        }
    }

    /// Module id to where it sits when the notch is collapsed: "left" or "right" (Mac placements). Missing means auto.
    public IReadOnlyDictionary<string, string> Placements =>
        ModuleDictionary("placements", value => value is "left" or "right" or "auto");

    /// Module id to its colour, "#RRGGBB" (Mac moduleColors). A module without one keeps the default colour.
    public IReadOnlyDictionary<string, string> ModuleColors => ModuleDictionary("moduleColors", IsHex);

    /// The string values of a dictionary keyed by module id, keeping the entries whose value passes `valid`.
    private IReadOnlyDictionary<string, string> ModuleDictionary(string key, Func<string, bool> valid)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        lock (_gate)
        {
            if (_root[key] is JsonObject saved)
            {
                foreach (var (id, node) in saved)
                {
                    if (ModuleCatalog.All.Contains(id) && node is JsonValue v && v.TryGetValue(out string? text)
                        && text is not null && valid(text))
                    {
                        map[id] = text;
                    }
                }
            }
        }
        return map;
    }

    /// A copy of the "badges" array (empty when absent).
    public JsonArray Badges
    {
        get
        {
            lock (_gate)
            {
                return _root["badges"] is JsonArray array ? (JsonArray)array.DeepClone() : new JsonArray();
            }
        }
    }

    /// The tool icons (settings "toolIcons"): tool name to its badge. An entry that fails the badge check is left out.
    public IReadOnlyDictionary<string, JsonObject> ToolIcons
    {
        get
        {
            var map = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            lock (_gate)
            {
                if (_root["toolIcons"] is JsonObject saved)
                {
                    foreach (var (tool, node) in saved)
                    {
                        if (TextOf(tool, ToolNameMax) && node is JsonObject badge && CheckBadge(badge, out var clean) is null)
                        {
                            map[tool] = clean;
                        }
                    }
                }
            }
            return map;
        }
    }

    /// The tools the Build & CI module has seen, newest first (settings "seenTools"): unique, at most 200.
    public IReadOnlyList<string> SeenTools
    {
        get
        {
            lock (_gate)
            {
                var seen = new List<string>();
                if (_root["seenTools"] is JsonArray array)
                {
                    foreach (var node in array)
                    {
                        if (TryText(node, out var tool) && TextOf(tool, ToolNameMax) && !seen.Contains(tool))
                        {
                            seen.Add(tool);
                        }
                    }
                }
                return seen.Take(SeenToolsMax).ToList();
            }
        }
    }

    /// Adds tools the Build & CI module saw. New ones go first, in the order given, unique, at most 200 in all. Writes
    /// only when something is new.
    public void AddSeenTools(IEnumerable<string> tools)
    {
        var current = SeenTools;
        var fresh = tools
            .Where(tool => TextOf(tool, ToolNameMax) && !current.Contains(tool))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (fresh.Count == 0)
        {
            return;
        }

        var array = new JsonArray();
        foreach (var tool in fresh.Concat(current).Distinct(StringComparer.Ordinal).Take(SeenToolsMax))
        {
            array.Add((JsonNode?)JsonValue.Create(tool));
        }
        Set("seenTools", array);
    }

    /// Saves the badge of a tool, or removes the tool's badge when badge is null. Returns null when saved; otherwise the
    /// reason the badge was refused, and nothing is saved.
    public string? SetToolIcon(string tool, JsonObject? badge)
    {
        if (!TextOf(tool, ToolNameMax))
        {
            return L10n.L("A tool name is up to 60 characters.");
        }

        JsonObject icons;
        lock (_gate)
        {
            icons = _root["toolIcons"] is JsonObject saved ? (JsonObject)saved.DeepClone() : new JsonObject();
        }

        if (badge is null)
        {
            icons.Remove(tool);
        }
        else
        {
            var refusal = CheckBadge(badge, out var clean);
            if (refusal is not null)
            {
                return refusal;
            }
            icons[tool] = clean;
        }
        Set("toolIcons", icons);
        return null;
    }

    /// Saves the notch's badges (settings "badges") from a list that came from outside, such as the phone: each badge goes through
    /// the same check SetToolIcon uses, and a badge that fails it is dropped. Returns how many were saved.
    public int SetBadges(IEnumerable<JsonObject> badges)
    {
        var clean = new JsonArray();
        foreach (var badge in badges)
        {
            if (CheckBadge(badge, out var checkedBadge) is null)
            {
                clean.Add((JsonNode?)checkedBadge);
            }
        }
        Set("badges", clean);
        return clean.Count;
    }

    /// The badge check of electron/src/core/settings.js (badge): every field is kept with its default. Returns null when the
    /// badge passes, else the field that does not; clean is the checked badge.
    private static string? CheckBadge(JsonObject badge, out JsonObject clean)
    {
        clean = new JsonObject();

        clean["id"] = TryText(badge["id"], out var id) && BadgeIdPattern.IsMatch(id) ? id : Guid.NewGuid().ToString();

        if (badge["symbol"] is null)
        {
            clean["symbol"] = "star";
        }
        else if (TryText(badge["symbol"], out var symbol) && TextOf(symbol, BadgeSymbolMax))
        {
            clean["symbol"] = symbol;
        }
        else
        {
            return BadRefusal("symbol");
        }

        clean["image"] = null;
        if (badge["image"] is not null)
        {
            if (!TryText(badge["image"], out var image) || image.Length > 500 || !Path.IsPathFullyQualified(image))
            {
                return BadRefusal("image");
            }
            clean["image"] = image;
        }

        string? refusedField = null;
        clean["text"] = OptionalText(badge, "text", BadgeTextMax, ref refusedField);
        clean["color"] = OptionalHex(badge, "color", ref refusedField);
        clean["fg"] = OptionalHex(badge, "fg", ref refusedField);
        clean["sprite"] = OptionalText(badge, "sprite", BadgeSymbolMax, ref refusedField);
        if (refusedField is not null)
        {
            return BadRefusal(refusedField);
        }

        foreach (var flag in new[] { "plain", "border", "right" })
        {
            if (badge[flag] is not null && !TryFlag(badge[flag], out _))
            {
                return BadRefusal(flag);
            }
        }
        clean["plain"] = badge["plain"] is null ? null : JsonValue.Create(TryFlag(badge["plain"], out var plain) && plain);
        clean["border"] = JsonValue.Create(TryFlag(badge["border"], out var border) && border);
        clean["right"] = JsonValue.Create(TryFlag(badge["right"], out var right) && right);

        if (badge["shape"] is null)
        {
            clean["shape"] = "circle";
        }
        else if (TryText(badge["shape"], out var shape) && BadgeShapes.Contains(shape))
        {
            clean["shape"] = shape;
        }
        else
        {
            return BadRefusal("shape");
        }

        clean["frames"] = null;
        if (badge["frames"] is not null)
        {
            if (!TryNumber(badge["frames"], 1, 120, out var frames))
            {
                return BadRefusal("frames");
            }
            clean["frames"] = JsonValue.Create(Math.Round(frames));
        }

        clean["fps"] = null;
        if (badge["fps"] is not null)
        {
            if (!TryNumber(badge["fps"], 1, 30, out var fps))
            {
                return BadRefusal("fps");
            }
            clean["fps"] = JsonValue.Create(fps);
        }
        return null;
    }

    private static string BadRefusal(string field) => L10n.L("A badge field is not valid: {0}", field);

    /// A text field that may be absent or null. A text that is present but too long, or not a string, sets refusedField
    /// (the first refused field is kept).
    private static string? OptionalText(JsonObject badge, string key, int max, ref string? refusedField)
    {
        if (badge[key] is null)
        {
            return null;
        }
        if (TryText(badge[key], out var text) && TextOf(text, max))
        {
            return text;
        }
        refusedField ??= key;
        return null;
    }

    /// A #RRGGBB colour field that may be absent or null, kept upper case. A bad value sets refusedField.
    private static string? OptionalHex(JsonObject badge, string key, ref string? refusedField)
    {
        if (badge[key] is null)
        {
            return null;
        }
        if (TryText(badge[key], out var hex) && hex.Length == 7 && hex[0] == '#' && hex.Skip(1).All(Uri.IsHexDigit))
        {
            return hex.ToUpperInvariant();
        }
        refusedField ??= key;
        return null;
    }

    private static bool TryFlag(JsonNode? node, out bool flag)
    {
        flag = false;
        return node is JsonValue v && v.TryGetValue(out flag);
    }

    private static bool TryText(JsonNode? node, out string text)
    {
        if (node is JsonValue v && v.TryGetValue(out string? s) && s is not null)
        {
            text = s;
            return true;
        }
        text = string.Empty;
        return false;
    }

    private static bool TryNumber(JsonNode? node, double min, double max, out double number)
    {
        if (node is JsonValue v && v.TryGetValue(out double n) && double.IsFinite(n) && n >= min && n <= max)
        {
            number = n;
            return true;
        }
        number = 0;
        return false;
    }

    /// Length in characters, counted as code points (an emoji is one), as electron's text check does.
    private static bool TextOf(string text, int max) => text.EnumerateRunes().Count() <= max;

    private string Choice(string key, string fallback, IReadOnlyCollection<string> allowed) =>
        TryGet(key, out string? value) && value is not null && allowed.Contains(value) ? value : fallback;

    private string Hex(string key, string fallback) =>
        TryGet(key, out string? value) && value is not null && IsHex(value) ? value : fallback;

    private double Ranged(string key, double fallback, double min, double max) =>
        TryGet(key, out double value) && value >= min && value <= max ? value : fallback;

    private bool Flag(string key, bool fallback) => TryGet(key, out bool value) ? value : fallback;

    /// The whole list, or the fallback when the key is absent or any entry is not a string.
    private IReadOnlyList<string> StringList(string key, string[] fallback)
    {
        lock (_gate)
        {
            if (_root[key] is not JsonArray array)
            {
                return fallback;
            }
            var items = new List<string>(array.Count);
            foreach (var node in array)
            {
                if (node is JsonValue v && v.TryGetValue(out string? item) && item is not null)
                {
                    items.Add(item);
                }
                else
                {
                    return fallback;
                }
            }
            return items;
        }
    }

    /// False when the key is absent or holds a value of another type. A read never writes to the file.
    private bool TryGet<T>(string key, [MaybeNullWhen(false)] out T value)
    {
        lock (_gate)
        {
            if (_root[key] is JsonValue node)
            {
                if (typeof(T) == typeof(string) && node.TryGetValue(out string? text) && text is not null)
                {
                    value = (T)(object)text;
                    return true;
                }
                if (typeof(T) == typeof(bool) && node.TryGetValue(out bool flag))
                {
                    value = (T)(object)flag;
                    return true;
                }
                if (typeof(T) == typeof(double) && node.TryGetValue(out double number))
                {
                    value = (T)(object)number;
                    return true;
                }
            }
        }
        value = default!;
        return false;
    }

    private static bool IsHex(string? value) =>
        value is { Length: 7 } && value[0] == '#' && value.Skip(1).All(Uri.IsHexDigit);

    private void LoadInitial()
    {
        string? text;
        try
        {
            text = AtomicFile.ReadAllTextOrNullWithRetry(_file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Locked or unreadable for now: defaults in memory, and the file is not treated as corrupt.
            // The next Set reads it again (SyncFromDisk).
            return;
        }

        if (text is null)
        {
            return;
        }
        _lastSeenText = text;
        if (TryParseObject(text, out var parsed))
        {
            _root = parsed;
            SanitizeItems();
        }
        else
        {
            _corrupt = true;
        }
    }

    private void StartWatching()
    {
        var dir = Path.GetDirectoryName(_file)!;
        Directory.CreateDirectory(dir);
        _debounce = new Timer(_ => OnDebounced(), null, Timeout.Infinite, Timeout.Infinite);
        _watcher = new FileSystemWatcher(dir, Path.GetFileName(_file))
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        _watcher.Changed += (_, _) => Kick();
        _watcher.Created += (_, _) => Kick();
        _watcher.Renamed += (_, _) => Kick();
        _watcher.Deleted += (_, _) => Kick();
        _watcher.EnableRaisingEvents = true;
    }

    private void Kick()
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                _debounce?.Change(Debounce, Timeout.InfiniteTimeSpan);
            }
        }
    }

    /// Reloads after an outside edit. A file that is missing, unreadable or corrupt keeps the values in memory.
    /// Runs on a timer thread, so a throwing subscriber is caught here instead of ending the process.
    private void OnDebounced()
    {
        IReadOnlyList<string> changed;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            try
            {
                changed = SyncFromDisk();
            }
            catch (IOException)
            {
                return;  // still locked: the next edit raises the event again
            }
        }
        if (changed.Count == 0)
        {
            return;
        }
        try
        {
            Raise(changed);
        }
        catch (Exception e)
        {
            Debug.WriteLine($"settings: a subscriber threw: {e}");
        }
    }

    /// The corrupt file is renamed to settings.json.corrupt, replacing an older one.
    private void MoveCorruptAside()
    {
        if (File.Exists(_file))
        {
            File.Move(_file, _file + ".corrupt", overwrite: true);
        }
    }

    private void Raise(IReadOnlyCollection<string> keys)
    {
        Changed?.Invoke(keys);
        foreach (var key in keys)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(PropertyNameFor(key)));
        }
    }

    /// The typed property of a key: "soundMap" is "SoundMap".
    private static string PropertyNameFor(string key) => char.ToUpperInvariant(key[0]) + key[1..];

    private static IReadOnlyList<string> DiffKeys(JsonObject before, JsonObject after)
    {
        return before.Select(pair => pair.Key)
            .Union(after.Select(pair => pair.Key), StringComparer.Ordinal)
            .Where(key => !JsonNode.DeepEquals(before[key], after[key]))
            .ToList();
    }

    private static bool TryParseObject(string text, [NotNullWhen(true)] out JsonObject? obj)
    {
        try
        {
            obj = JsonNode.Parse(text) as JsonObject;
            return obj is not null;
        }
        catch (JsonException)
        {
            obj = null;
            return false;
        }
        catch (ArgumentException)
        {
            // Duplicate keys in the object.
            obj = null;
            return false;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _watcher?.Dispose();
            _debounce?.Dispose();
        }
    }
}
