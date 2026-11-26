using System.Text.Json.Nodes;
using NotchTasks.Core.Settings;
using Xunit;

namespace NotchTasks.Core.Tests;

public sealed class ThemesTests : IDisposable
{
    private readonly string _dir;
    private readonly string _file;

    public ThemesTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "nt-themes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "settings.json");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A watcher still holding the folder; the OS temp cleaner takes it later.
        }
        catch (UnauthorizedAccessException)
        {
            // As above.
        }
    }

    [Fact]
    public void All_AreTheSixMacThemesInOrder()
    {
        Assert.Equal(new[] { "classic", "neon", "glass", "minimal", "sunset", "ocean" }, Themes.All.Select(t => t.Id));
    }

    [Fact]
    public void Values_AreTheMacValuesOfEachTheme()
    {
        // Customize.swift:342-358, field by field: background, gradient A and B, accent, custom accent, toast style, motion.
        var expected = new Dictionary<string, (string Background, string A, string B, string Accent, string Custom, string Toast, string Motion)>
        {
            ["classic"] = ("black", "#1E1B4B", "#000000", "auto", "#FF9F0A", "island", "bouncy"),
            ["neon"] = ("glow", "#0F172A", "#000000", "custom", "#00F5D4", "card", "bouncy"),
            ["glass"] = ("glass", "#334155", "#0F172A", "white", "#FFFFFF", "banner", "smooth"),
            ["minimal"] = ("black", "#111111", "#000000", "white", "#FFFFFF", "compact", "snappy"),
            ["sunset"] = ("gradient", "#FF5F6D", "#5B247A", "custom", "#FFC371", "card", "bouncy"),
            ["ocean"] = ("gradient", "#0EA5E9", "#1E1B4B", "custom", "#7DD3FC", "island", "smooth"),
        };

        foreach (var theme in Themes.All)
        {
            var (background, a, b, accent, custom, toast, motion) = expected[theme.Id];
            Assert.Equal(background, theme.Background);
            Assert.Equal(a, theme.GradientA);
            Assert.Equal(b, theme.GradientB);
            Assert.Equal(accent, theme.Accent);
            Assert.Equal(custom, theme.CustomAccent);
            Assert.Equal(toast, theme.ToastStyle);
            Assert.Equal(motion, theme.Motion);
        }
    }

    [Fact]
    public void Values_NameTheSevenSettingsAThemeSets()
    {
        var glass = Themes.All.First(t => t.Id == "glass");

        Assert.Equal(
            new[] { "accent", "background", "customAccent", "gradientA", "gradientB", "motion", "toastStyle" },
            glass.Values.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal("glass", (string?)glass.Values["background"]);
        Assert.Equal("white", (string?)glass.Values["accent"]);
    }

    [Fact]
    public void Apply_WritesTheThemeInOneSaveAndRaisesOnce()
    {
        using var store = SettingsStore.Load(_file);
        var raised = new List<IReadOnlyCollection<string>>();
        store.Changed += keys => raised.Add(keys);
        var sunset = Themes.All.First(t => t.Id == "sunset");

        store.SetMany(sunset.Values);

        Assert.Single(raised);
        Assert.Equal("gradient", store.Background);
        Assert.Equal("#FF5F6D", store.GradientA);
        Assert.Equal("#5B247A", store.GradientB);
        Assert.Equal("custom", store.Accent);
        Assert.Equal("#FFC371", store.CustomAccent);
        Assert.Equal("card", store.ToastStyle);
        Assert.Equal("bouncy", store.Motion);
    }

    [Fact]
    public void Apply_KeepsTheSoundsAndTheOtherSettings()
    {
        using var store = SettingsStore.Load(_file);
        store.Set("sounds", JsonValue.Create(false));
        store.Set("volume", JsonValue.Create(0.3));
        store.Set("soundMap", JsonNode.Parse("{\"taskDone\":\"Pop\"}"));

        store.SetMany(Themes.All.First(t => t.Id == "neon").Values);

        Assert.False(store.Sounds);
        Assert.Equal(0.3, store.Volume, 2);
        Assert.Equal("Pop", store.SoundMap["taskDone"]);
    }

    [Fact]
    public void IsSelected_MatchesTheLookOfTheStore()
    {
        using var store = SettingsStore.Load(_file);
        var glass = Themes.All.First(t => t.Id == "glass");
        var minimal = Themes.All.First(t => t.Id == "minimal");

        store.SetMany(glass.Values);

        Assert.True(Themes.IsSelected(glass, store));
        Assert.False(Themes.IsSelected(minimal, store));
    }

    [Fact]
    public void IsSelected_IgnoresTheCustomColourUnlessTheAccentIsCustom()
    {
        using var store = SettingsStore.Load(_file);
        store.SetMany(Themes.All.First(t => t.Id == "classic").Values);

        // Classic's accent is "auto": a different custom colour in the store still selects it.
        store.Set("customAccent", JsonValue.Create("#123456"));
        Assert.True(Themes.IsSelected(Themes.All.First(t => t.Id == "classic"), store));

        // Neon's accent is custom: its own colour is part of the match.
        store.SetMany(Themes.All.First(t => t.Id == "neon").Values);
        store.Set("customAccent", JsonValue.Create("#123456"));
        Assert.False(Themes.IsSelected(Themes.All.First(t => t.Id == "neon"), store));
    }
}
