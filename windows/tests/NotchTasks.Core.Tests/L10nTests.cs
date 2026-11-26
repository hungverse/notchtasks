using System.Globalization;
using System.Text.Json;
using NotchTasks.Core;
using Xunit;

namespace NotchTasks.Core.Tests;

// All tests share L10n's static state (Init), so they live in one class and run one at a time.
[Collection("L10n")]
public sealed class L10nTests
{
    private static string MacSources()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "mac", "Sources")))
        {
            dir = Path.GetDirectoryName(dir);
        }
        Assert.NotNull(dir);
        return Path.Combine(dir!, "mac", "Sources");
    }

    private static string RepoRoot() => Path.GetDirectoryName(Path.GetDirectoryName(MacSources()))!;

    [Fact]
    public void En_ReturnsKey()
    {
        L10n.Init(MacSources(), "en");

        Assert.Equal("en", L10n.Current);
        Assert.Equal("Run command", L10n.L("Run command"));
    }

    [Fact]
    public void Ja_Translates()
    {
        L10n.Init(MacSources(), "ja");

        Assert.Equal("ja", L10n.Current);
        Assert.Equal("コマンドを実行", L10n.L("Run command"));
    }

    [Fact]
    public void Legacy_ViKeyMapsToEnglish()
    {
        L10n.Init(MacSources(), "fr");

        Assert.Equal(L10n.L("Waiting for you"), L10n.L("Đang chờ bạn"));
    }

    [Fact]
    public void Missing_ReturnsKey()
    {
        L10n.Init(MacSources(), "ja");

        Assert.Equal("A text nobody translated", L10n.L("A text nobody translated"));
    }

    [Fact]
    public void Languages_AreTheSixteenOfTheMacInOrder()
    {
        Assert.Equal(16, L10n.Languages.Count);
        Assert.Equal(("en", "English"), L10n.Languages[0]);
        Assert.Equal(("pt-BR", "Português (Brasil)"), L10n.Languages[9]);
        Assert.Equal(("hi", "हिन्दी"), L10n.Languages[15]);
    }

    [Fact]
    public void DeviceLanguage_IsALanguageTheAppSpeaks()
    {
        Assert.Contains(L10n.DeviceLanguage, L10n.Languages.Select(l => l.Code));
    }

    [Fact]
    public void Languages_EachIsSupported()
    {
        foreach (var (code, _) in L10n.Languages)
        {
            L10n.Init(MacSources(), code);

            Assert.Equal(code, L10n.Current);
        }
    }

    [Fact]
    public void Placeholders_AreFilledInOrder()
    {
        Assert.Equal("2 và a", L10n.Fill("{1} và {0}", "a", 2));
        Assert.Equal("3 of 5", L10n.L("{0} of {1}", 3, 5));
    }

    [Fact]
    public void Placeholders_NumbersUseInvariantCulture()
    {
        var old = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("fr-FR");
            Assert.Equal("1.5", L10n.Fill("{0}", 1.5));
        }
        finally
        {
            CultureInfo.CurrentCulture = old;
        }
    }

    [Theory]
    [InlineData("fr-CA", "fr")]
    [InlineData("zh-Hant-TW", "zh-Hant")]
    [InlineData("zh-CN", "zh-Hans")]
    [InlineData("zh-HK", "zh-Hant")]
    [InlineData("pt-PT", "pt-BR")]
    [InlineData("sw", "en")]
    [InlineData("", "en")]
    public void Match_PicksSupportedLanguage(string preferred, string expected)
    {
        Assert.Equal(expected, L10n.Match(new[] { preferred }));
    }

    [Fact]
    public void Match_SkipsUnsupportedAndTakesNextPreference()
    {
        Assert.Equal("vi", L10n.Match(new[] { "sw", "vi-VN" }));
    }

    [Fact]
    public void Init_EmptyLanguageFollowsCurrentUiCulture()
    {
        L10n.Init(MacSources(), "");

        Assert.Equal(L10n.Match(new[] { CultureInfo.CurrentUICulture.Name }), L10n.Current);
    }

    [Fact]
    public void Changed_FiresOnInit()
    {
        var fired = 0;
        Action handler = () => fired++;
        L10n.Changed += handler;
        try
        {
            L10n.Init(MacSources(), "ja");
        }
        finally
        {
            L10n.Changed -= handler;
        }

        Assert.Equal(1, fired);
    }

    [Fact]
    public void WindowsOverlay_FillsKeyMissingFromMacTable()
    {
        // "Nothing here yet." is in electron/l10n/windows.json (vi) but not in mac/Sources/l10n-vi.json.
        var folder = Path.Combine(Path.GetTempPath(), "nt-l10n-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            foreach (var file in Directory.GetFiles(MacSources(), "l10n-*.json"))
            {
                File.Copy(file, Path.Combine(folder, Path.GetFileName(file)));
            }
            File.Copy(Path.Combine(RepoRoot(), "electron", "l10n", "windows.json"), Path.Combine(folder, "windows.json"));

            L10n.Init(folder, "vi");

            Assert.Equal("Chưa có mục nào.", L10n.L("Nothing here yet."));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void WindowsOverlay_TranslatesNoneAndChooseAColour_InEveryLanguage()
    {
        // The Windows-only keys: the sound picker's "None" and the colour field's "Choose a colour" tooltip.
        var folder = Path.Combine(Path.GetTempPath(), "nt-l10n-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            foreach (var file in Directory.GetFiles(MacSources(), "l10n-*.json"))
            {
                File.Copy(file, Path.Combine(folder, Path.GetFileName(file)));
            }
            File.Copy(Path.Combine(RepoRoot(), "electron", "l10n", "windows.json"), Path.Combine(folder, "windows.json"));

            foreach (var (code, _) in L10n.Languages.Where(language => language.Code != "en"))
            {
                L10n.Init(folder, code);

                Assert.NotEqual("None", L10n.L("None"));
                Assert.NotEqual("Choose a colour", L10n.L("Choose a colour"));
            }
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    // Every key the Windows build passes to L10n.L since 6eb2a5a (Tasks 1-12), plus the module titles the pill and the
    // Modules pane show. Each one needs a text in every language: in the Mac table, or in electron/l10n/windows.json.
    private static readonly string[] Phase3aKeys =
    [
        " · Mic muted", "%.1f GB free", "Allow", "Back online", "Camera on", "Charging",
        "Check Wi‑Fi or the network cable", "Couldn't mute", "Deny", "Disk almost full", "Download finished", "Ended",
        "Free up space", "Hide alert", "Lets the notch show when an app uses the mic, and calls.", "Low battery",
        "Memory in use: ", "Micro: ", "Microphone on", "Microphone", "Mute", "Network disconnected",
        "Not turned on: administrator was not allowed", "Nothing running", "On a call · {0}", "On a call", "On", "Open",
        "Optional. Reading temperatures on Windows needs administrator, so a small helper runs with administrator rights only if you turn this on.",
        "Reply", "Show in folder", "Start with Windows without asking again", "Temperature", "Temperatures",
        "This PC does not report temperatures", "Time's up", "Try again", "Turn off",
        "Turn on again to read temperatures this session", "Turn on", "Unmute", "Unplugged", "Use the button in {0}",
        "Video call · {0}", "Waiting for you", "Whole-computer CPU: ", "{0} min", "{0} running", "{0} waiting", "{0}% left",
        "{0}% · click to open Task Manager", "{0}-minute timer",
        // The ternaries and titles: the Wi‑Fi and VPN chips, the connection toasts, and the module names.
        "Wi‑Fi connected", "Connected", "VPN on", "VPN off",
        "Battery", "Timer", "Network", "Downloads", "Computer health", "Mic & camera", "Calls",
    ];

    [Fact]
    public void Phase3aKeys_ExistInEveryLanguage()
    {
        var overlayFile = Path.Combine(RepoRoot(), "electron", "l10n", "windows.json");
        var missing = new List<string>();
        var placeholders = new List<string>();
        foreach (var (code, _) in L10n.Languages.Where(language => language.Code != "en"))
        {
            var mac = ReadStrings(Path.Combine(MacSources(), $"l10n-{code}.json"));
            var windows = ReadOverlay(overlayFile, code);
            foreach (var key in Phase3aKeys)
            {
                if (!mac.ContainsKey(key) && !windows.ContainsKey(key))
                {
                    missing.Add($"{code}: {key}");
                }
                // A Windows text keeps the placeholders of its key, so the arguments still fill in.
                if (windows.TryGetValue(key, out var text) && Placeholders(key).Any(p => !text.Contains(p, StringComparison.Ordinal)))
                {
                    placeholders.Add($"{code}: {key}");
                }
            }
        }
        Assert.Empty(missing);
        Assert.Empty(placeholders);
    }

    // Phase 9's calendar card: the Join button, and the hint when the calendar store cannot be opened. Each one needs a text in every
    // language, in the Mac table or in electron/l10n/windows.json.
    private static readonly string[] Phase9Keys =
    [
        "Join", "Calendar can't be opened. Check Settings > Privacy & security > Calendar.",
    ];

    // The Windows-only texts of the profile and badge dialogs, the picture and GIF file filters, and the Overview's report note. They
    // have no Mac text, so each one needs its text in electron/l10n/windows.json, in every language.
    private static readonly string[] FinalReviewKeys =
    [
        "Couldn't export the profile", "Couldn't import the profile", "This file isn't a NotchTasks profile.",
        "Choose a GIF.", "That picture is over 8 MB.", "That file is not a picture.", "That file can't be read.",
        "Picture files", "GIF files", "Reports go through sync, which is off. Turn sync on in Pairing to send one.",
    ];

    [Fact]
    public void FinalReviewKeys_ExistInEveryLanguage()
    {
        var overlayFile = Path.Combine(RepoRoot(), "electron", "l10n", "windows.json");
        var missing = new List<string>();
        var placeholders = new List<string>();
        foreach (var (code, _) in L10n.Languages.Where(language => language.Code != "en"))
        {
            var mac = ReadStrings(Path.Combine(MacSources(), $"l10n-{code}.json"));
            var windows = ReadOverlay(overlayFile, code);
            foreach (var key in FinalReviewKeys)
            {
                if (!mac.ContainsKey(key) && !windows.ContainsKey(key))
                {
                    missing.Add($"{code}: {key}");
                }
                if (windows.TryGetValue(key, out var text) && Placeholders(key).Any(p => !text.Contains(p, StringComparison.Ordinal)))
                {
                    placeholders.Add($"{code}: {key}");
                }
            }
        }
        Assert.Empty(missing);
        Assert.Empty(placeholders);
    }

    [Fact]
    public void Phase9Keys_ExistInEveryLanguage()
    {
        var overlayFile = Path.Combine(RepoRoot(), "electron", "l10n", "windows.json");
        var missing = new List<string>();
        foreach (var (code, _) in L10n.Languages.Where(language => language.Code != "en"))
        {
            var mac = ReadStrings(Path.Combine(MacSources(), $"l10n-{code}.json"));
            var windows = ReadOverlay(overlayFile, code);
            foreach (var key in Phase9Keys)
            {
                if (!mac.ContainsKey(key) && !windows.ContainsKey(key))
                {
                    missing.Add($"{code}: {key}");
                }
            }
        }
        Assert.Empty(missing);
    }

    // Every key the Windows build passes to L10n.L since 7663b8c (Phase 3b: agents, limits, the watch list editor),
    // plus the two Icon Library hints that had no Windows text. Each one needs a text in every language.
    private static readonly string[] Phase3bKeys =
    [
        " (added)", " · command contains “{0}”", "+{0} more tasks", "AI apps", "Add item to watch", "App", "App · {0}",
        "Build & CI", "Cancel", "Choose an agent, an open app, or type a process name.",
        "Click an animation to add it to the notch.", "Click an animation to use it for the selected icon.",
        "Command contains “{0}”", "Command line contains", "Couldn't save the watch list: {0}", "Display name",
        "Done · {0} ago", "Each subprocess (runner, worker)", "Edit",
        "Enter a number from 0.5 to 100, or leave it empty.", "Enter a process…", "Kind", "Loaded ",
        "Model running locally", "No model loaded", "Open apps", "Open log", "Process", "Process name",
        "Process name, as in Task Manager.", "Process “{0}”", "Program name", "Program name, as in Task Manager.",
        "Raise if it shows while idle, lower if it misses work.", "Remove", "Resets at {0}", "Running", "Running · {0}",
        "Say done when it ran {0} sec or more", "Shows while running", "The watch list holds at most {0} items.",
        "Tracks each session", "Wait until {0} ({1} left)", "Waiting", "Watch list", "Watched item",
        "What the notch watches. Only what's working shows on the notch.", "Working when CPU is above", "Working · {0}",
        "one line per subprocess", "{0} done", "{0} hours", "{0} is tracked per work session, nothing else to set.",
        "{0} reached its {1} limit", "{0} sec", "{0} tasks running", "{0} used {1}% of its {2} limit", "{0}h {1}m",
    ];

    [Fact]
    public void Phase3bKeys_ExistInEveryLanguage()
    {
        var overlayFile = Path.Combine(RepoRoot(), "electron", "l10n", "windows.json");
        var missing = new List<string>();
        var placeholders = new List<string>();
        foreach (var (code, _) in L10n.Languages.Where(language => language.Code != "en"))
        {
            var mac = ReadStrings(Path.Combine(MacSources(), $"l10n-{code}.json"));
            var windows = ReadOverlay(overlayFile, code);
            foreach (var key in Phase3bKeys)
            {
                if (!mac.ContainsKey(key) && !windows.ContainsKey(key))
                {
                    missing.Add($"{code}: {key}");
                }
                // A Windows text keeps the placeholders of its key, so the arguments still fill in.
                if (windows.TryGetValue(key, out var text) && Placeholders(key).Any(p => !text.Contains(p, StringComparison.Ordinal)))
                {
                    placeholders.Add($"{code}: {key}");
                }
            }
        }
        Assert.Empty(missing);
        Assert.Empty(placeholders);
    }

    // Every key the Windows build passes to L10n.L in Phase 3d (the media, shelf, devices and mirror modules, and the
    // CI module's leave of "Coming soon"), including the constants passed to it. Each one needs a text in every language.
    private static readonly string[] Phase3dKeys =
    [
        "Add files…", "Add to shelf", "Close mirror", "Deselect", "Drag and drop or choose files",
        "Drag files onto the notch, or click to choose files for the shelf", "Drag files onto the notch, or click + to choose",
        "Click to select · double-click to open · drag out to drop into another app", "Drop here",
        "Drag one selected file to bring all {0} files", "Drag to seek", "File not found anymore: {0}", "File shelf", "Mirror",
        "Camera mirror", "Now Playing", "Devices", "No camera found", "NotchTasks can't use the camera yet", "Open Camera settings",
        "Open {0}", "Open", "Playing audio", "Quick camera check", "Ready", "Remove all from the shelf (originals stay)",
        "Remove from shelf", "Remove from the shelf (original stays)", "Select all", "Share all", "Share", "Show in folder",
        "{0} battery low", "{0} connected", "{0} disconnected", "{0}/{1} selected",
    ];

    [Fact]
    public void Phase3dKeys_ExistInEveryLanguage()
    {
        var overlayFile = Path.Combine(RepoRoot(), "electron", "l10n", "windows.json");
        var missing = new List<string>();
        var placeholders = new List<string>();
        foreach (var (code, _) in L10n.Languages.Where(language => language.Code != "en"))
        {
            var mac = ReadStrings(Path.Combine(MacSources(), $"l10n-{code}.json"));
            var windows = ReadOverlay(overlayFile, code);
            foreach (var key in Phase3dKeys)
            {
                if (!mac.ContainsKey(key) && !windows.ContainsKey(key))
                {
                    missing.Add($"{code}: {key}");
                }
                if (windows.TryGetValue(key, out var text) && Placeholders(key).Any(p => !text.Contains(p, StringComparison.Ordinal)))
                {
                    placeholders.Add($"{code}: {key}");
                }
            }
        }
        Assert.Empty(missing);
        Assert.Empty(placeholders);
    }

    private static IEnumerable<string> Placeholders(string key) =>
        System.Text.RegularExpressions.Regex.Matches(key, @"\{\d+\}").Select(m => m.Value).Distinct();

    private static Dictionary<string, string> ReadStrings(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.EnumerateObject()
            .Where(p => p.Value.ValueKind == JsonValueKind.String)
            .ToDictionary(p => p.Name, p => p.Value.GetString() ?? string.Empty);
    }

    /// The Windows-only texts of one language in windows.json: { "<code>": { key: text } }.
    private static Dictionary<string, string> ReadOverlay(string path, string code)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.TryGetProperty(code, out var language) && language.ValueKind == JsonValueKind.Object
            ? language.EnumerateObject()
                .Where(p => p.Value.ValueKind == JsonValueKind.String)
                .ToDictionary(p => p.Name, p => p.Value.GetString() ?? string.Empty)
            : new Dictionary<string, string>();
    }

    // Task 11's heat toast and Temperatures pane: the Mac's texts, and the Windows texts of the helper's rows and the pane.
    // Each one needs a text in every language, in the Mac table or in electron/l10n/windows.json.
    private static readonly string[] Phase11Keys =
    [
        "Computer is warm", "Computer is very hot", "{0} is working hard", "Consider closing heavy apps", "See apps",
        "Temperatures", "Sensors", "Could not read temperature sensors.", "Checking…", "Turn on", "Try again",
        "Start with Windows without asking again", "Not turned on: administrator was not allowed",
        "Turn on again to read temperatures this session", "This PC does not report temperatures",
        "Optional. Reading temperatures on Windows needs administrator, so a small helper runs with administrator rights only if you turn this on.",
    ];

    [Fact]
    public void Phase11Keys_ExistInEveryLanguage()
    {
        var overlayFile = Path.Combine(RepoRoot(), "electron", "l10n", "windows.json");
        var missing = new List<string>();
        foreach (var (code, _) in L10n.Languages.Where(language => language.Code != "en"))
        {
            var mac = ReadStrings(Path.Combine(MacSources(), $"l10n-{code}.json"));
            var windows = ReadOverlay(overlayFile, code);
            foreach (var key in Phase11Keys)
            {
                if (!mac.ContainsKey(key) && !windows.ContainsKey(key))
                {
                    missing.Add($"{code}: {key}");
                }
            }
        }
        Assert.Empty(missing);
    }

    [Fact]
    public void WindowsOverlay_TranslatesTheBuildToolMessages_InEveryLanguage()
    {
        // The two messages of the Build & CI tool icons (SettingsStore.SetToolIcon) that the Mac tables lack.
        var folder = Path.Combine(Path.GetTempPath(), "nt-l10n-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            foreach (var file in Directory.GetFiles(MacSources(), "l10n-*.json"))
            {
                File.Copy(file, Path.Combine(folder, Path.GetFileName(file)));
            }
            File.Copy(Path.Combine(RepoRoot(), "electron", "l10n", "windows.json"), Path.Combine(folder, "windows.json"));

            foreach (var (code, _) in L10n.Languages.Where(language => language.Code != "en"))
            {
                L10n.Init(folder, code);

                Assert.NotEqual("A badge field is not valid: {0}", L10n.L("A badge field is not valid: {0}", "symbol"));
                Assert.NotEqual("A tool name is up to 60 characters.", L10n.L("A tool name is up to 60 characters."));
            }
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    // The Icons pane's title and its sidebar label (PaneIds.TitleKey). The Mac has no badges-only title (its pane is "Icons & lock"
    // with the lock screen), so the key is "Icons": it needs a text in every language, in the Mac table or in windows.json.
    [Fact]
    public void IconsPaneTitle_IsTranslatedInEveryLanguage()
    {
        var folder = Path.Combine(Path.GetTempPath(), "nt-l10n-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            foreach (var file in Directory.GetFiles(MacSources(), "l10n-*.json"))
            {
                File.Copy(file, Path.Combine(folder, Path.GetFileName(file)));
            }
            File.Copy(Path.Combine(RepoRoot(), "electron", "l10n", "windows.json"), Path.Combine(folder, "windows.json"));

            var overlayFile = Path.Combine(RepoRoot(), "electron", "l10n", "windows.json");
            foreach (var (code, _) in L10n.Languages.Where(language => language.Code != "en"))
            {
                var mac = ReadStrings(Path.Combine(MacSources(), $"l10n-{code}.json"));
                var windows = ReadOverlay(overlayFile, code);
                Assert.True(mac.ContainsKey("Icons") || windows.ContainsKey("Icons"), $"No text for Icons in {code}");

                L10n.Init(folder, code);
                Assert.NotEqual("Icons", L10n.L("Icons"));
            }
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
