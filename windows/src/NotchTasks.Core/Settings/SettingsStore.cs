using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

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
            next[key] = value?.DeepClone();
            var text = next.ToJsonString(WriteOptions);
            AtomicFile.WriteAllText(_file, text);
            _root = next;
            _lastSeenText = text;
            _corrupt = false;
        }
        Raise(reloaded.Append(key).Distinct(StringComparer.Ordinal).ToList());
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
        return changed;
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
