using System.Globalization;
using System.Text;
using System.Text.Json;

namespace NotchTasks.Core;

/// Port of mac/Sources/L10n.swift. The English text is the key; a missing text shows the key.
/// Tables: l10n-legacy.json (old Vietnamese keys -> English keys), l10n-<code>.json (the Mac translations),
/// and windows.json (PC-only overlay: { "<code>": { key: text } }), which fills keys the Mac table lacks.
public static class L10n
{
    private static readonly string[] Supported =
    [
        "en", "vi", "zh-Hans", "zh-Hant", "ja", "ko", "es", "fr", "de", "pt-BR", "it", "ru", "id", "th", "tr", "hi",
    ];

    private static readonly object Lock = new();
    private static string _folder = string.Empty;
    private static string _current = "en";
    private static Dictionary<string, string>? _legacy;
    private static Dictionary<string, Dictionary<string, string>>? _overlay;
    private static readonly Dictionary<string, Dictionary<string, string>> Tables = new();

    /// The language in use: a supported code, "en" by default.
    public static string Current
    {
        get { lock (Lock) return _current; }
    }

    /// Raised after each Init, on the thread that called it. The UI re-reads its texts when it fires.
    public static event Action? Changed;

    /// Reads the tables from `folder`, and uses `language`; "" follows the UI culture of this user.
    public static void Init(string folder, string language)
    {
        var code = language.Length == 0 ? Match([CultureInfo.CurrentUICulture.Name]) : language;
        if (Array.IndexOf(Supported, code) < 0)
        {
            code = "en";
        }
        lock (Lock)
        {
            _folder = folder;
            _current = code;
            _legacy = null;
            _overlay = null;
            Tables.Clear();
        }
        Changed?.Invoke();
    }

    /// The first of the preferred languages that the app speaks; English when none.
    public static string Match(IEnumerable<string> preferred)
    {
        foreach (var p in preferred)
        {
            var parts = p.Split('-');
            var lang = parts[0].ToLowerInvariant();
            if (lang == "zh")
            {
                var hant = parts.Skip(1).Any(x => x.Equals("Hant", StringComparison.OrdinalIgnoreCase)
                    || x.Equals("TW", StringComparison.OrdinalIgnoreCase)
                    || x.Equals("HK", StringComparison.OrdinalIgnoreCase)
                    || x.Equals("MO", StringComparison.OrdinalIgnoreCase));
                return hant ? "zh-Hant" : "zh-Hans";
            }
            if (lang == "pt")
            {
                return "pt-BR";
            }
            var hit = Array.Find(Supported, s => s == lang);
            if (hit is not null)
            {
                return hit;
            }
        }
        return "en";
    }

    /// A text in the current language. Placeholders {0}, {1}… take the arguments in order.
    public static string L(string key, params object[] args)
    {
        string code;
        lock (Lock) code = _current;
        return Fill(Text(key, code), args);
    }

    /// Replaces {0}, {1}… with the arguments (numbers in the invariant culture). Each placeholder is read once.
    public static string Fill(string s, params object[] args)
    {
        var sb = new StringBuilder(s.Length);
        var i = 0;
        while (i < s.Length)
        {
            if (s[i] == '{')
            {
                var close = s.IndexOf('}', i + 1);
                if (close > i + 1 && int.TryParse(s.AsSpan(i + 1, close - i - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                    && index < args.Length)
                {
                    sb.Append(ToText(args[index]));
                    i = close + 1;
                    continue;
                }
            }
            sb.Append(s[i]);
            i++;
        }
        return sb.ToString();
    }

    private static string ToText(object? value) => value switch
    {
        null => string.Empty,
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private static string Text(string key, string code)
    {
        lock (Lock)
        {
            var k = Legacy(key);
            if (code == "en")
            {
                return k;
            }
            if (Table(code).TryGetValue(k, out var mac))
            {
                return mac;
            }
            if (Overlay().TryGetValue(code, out var extra) && extra.TryGetValue(k, out var win))
            {
                return win;
            }
            return k;
        }
    }

    // Callers hold Lock.
    private static string Legacy(string key)
    {
        _legacy ??= ReadStrings(Path.Combine(_folder, "l10n-legacy.json"));
        return _legacy.TryGetValue(key, out var k) ? k : key;
    }

    private static Dictionary<string, string> Table(string code)
    {
        if (!Tables.TryGetValue(code, out var t))
        {
            t = ReadStrings(Path.Combine(_folder, $"l10n-{code}.json"));
            Tables[code] = t;
        }
        return t;
    }

    private static Dictionary<string, Dictionary<string, string>> Overlay()
    {
        if (_overlay is null)
        {
            _overlay = new Dictionary<string, Dictionary<string, string>>();
            using var doc = ReadDocument(Path.Combine(_folder, "windows.json"));
            if (doc is not null && doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var lang in doc.RootElement.EnumerateObject())
                {
                    if (lang.Value.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }
                    var strings = new Dictionary<string, string>();
                    foreach (var p in lang.Value.EnumerateObject())
                    {
                        if (p.Value.ValueKind == JsonValueKind.String)
                        {
                            strings[p.Name] = p.Value.GetString() ?? string.Empty;
                        }
                    }
                    _overlay[lang.Name] = strings;
                }
            }
        }
        return _overlay;
    }

    /// A table of strings; a missing or unreadable file is an empty table (the text then shows its key).
    private static Dictionary<string, string> ReadStrings(string path)
    {
        var strings = new Dictionary<string, string>();
        using var doc = ReadDocument(path);
        if (doc is not null && doc.RootElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.String)
                {
                    strings[p.Name] = p.Value.GetString() ?? string.Empty;
                }
            }
        }
        return strings;
    }

    private static JsonDocument? ReadDocument(string path)
    {
        try
        {
            return JsonDocument.Parse(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
