using System.Text.Json.Nodes;
using NotchTasks.Core.Settings;
using Xunit;

namespace NotchTasks.Core.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _file;

    public SettingsStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "nt-settings-" + Guid.NewGuid().ToString("N"));
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
        }
    }

    [Fact]
    public void Load_MissingFile_GivesDefaultsOfTheBehaviorKeys()
    {
        using var store = SettingsStore.Load(_file);

        Assert.True(store.SystemLoad);
        Assert.True(store.SystemNotifications);
        Assert.Equal(80, store.LimitWarn);
        Assert.True(store.MenuBarIcon);
        Assert.False(store.AnalyticsOff);
    }

    [Fact]
    public void Load_MissingFile_GivesMacDefaults()
    {
        using var store = SettingsStore.Load(_file);

        Assert.Equal("hover", store.ExpandMode);
        Assert.Equal(0.15, store.HoverDelay);
        Assert.Equal("compact", store.Size);
        Assert.Equal("auto", store.Accent);
        Assert.Equal("bouncy", store.Motion);
        Assert.Equal("black", store.Background);
        Assert.Equal("island", store.ToastStyle);
        Assert.True(store.Sounds);
        Assert.Equal(0.8, store.Volume);
        Assert.False(store.OnboardingDone);
        Assert.Equal(new[] { "agents", "ci", "media", "timer", "shelf", "limits", "battery", "network", "downloads", "health" }, store.Modules);
        Assert.Equal(new[] { "timer" }, store.Disabled);
        Assert.Equal("Glass", store.SoundMap["taskDone"]);
        Assert.Empty(store.Badges);
    }

    [Fact]
    public void Save_KeepsUnknownKeys()
    {
        File.WriteAllText(_file, "{\"futureKey\":{\"a\":1},\"motion\":\"smooth\"}");
        using var store = SettingsStore.Load(_file);

        store.Set("motion", JsonValue.Create("snappy"));

        var root = JsonNode.Parse(File.ReadAllText(_file))!.AsObject();
        Assert.Equal(1, root["futureKey"]!["a"]!.GetValue<int>());
        Assert.Equal("snappy", root["motion"]!.GetValue<string>());
        Assert.Equal("snappy", store.Motion);
    }

    [Fact]
    public void Load_CorruptFile_UsesDefaults_AndDoesNotOverwrite()
    {
        const string corrupt = "{\"motion\":";
        File.WriteAllText(_file, corrupt);
        using var store = SettingsStore.Load(_file);

        Assert.Equal("bouncy", store.Motion);
        Assert.Equal(corrupt, File.ReadAllText(_file));

        store.Set("accent", JsonValue.Create("blue"));

        Assert.Equal(corrupt, File.ReadAllText(_file + ".corrupt"));
        var root = JsonNode.Parse(File.ReadAllText(_file))!.AsObject();
        Assert.Equal("blue", root["accent"]!.GetValue<string>());
        Assert.Equal("blue", store.Accent);
    }

    [Fact]
    public void InvalidKnownValue_ReadsDefault_AndFileIsUntouched()
    {
        const string text = "{\"motion\":\"wobbly\",\"hoverDelay\":5}";
        File.WriteAllText(_file, text);
        using var store = SettingsStore.Load(_file);

        Assert.Equal("bouncy", store.Motion);
        Assert.Equal(0.15, store.HoverDelay);
        Assert.Equal(text, File.ReadAllText(_file));
    }

    [Fact]
    public void Set_RaisesChangedWithKey()
    {
        using var store = SettingsStore.Load(_file);
        IReadOnlyCollection<string>? keys = null;
        var properties = new List<string>();
        store.Changed += changed => keys = changed;
        store.PropertyChanged += (_, e) => properties.Add(e.PropertyName ?? string.Empty);

        store.Set("accent", JsonValue.Create("blue"));

        Assert.Equal(new[] { "accent" }, keys);
        Assert.Contains("Accent", properties);
        Assert.Equal("blue", store.Accent);
    }

    [Fact]
    public async Task ExternalEdit_Reloads()
    {
        using var store = SettingsStore.Load(_file);
        var changed = new TaskCompletionSource<IReadOnlyCollection<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        store.Changed += keys => changed.TrySetResult(keys);

        File.WriteAllText(_file, "{\"motion\":\"snappy\"}");

        var keys = await changed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Contains("motion", keys);
        Assert.Equal("snappy", store.Motion);
    }

    [Fact]
    public async Task ExternalRevertToOwnText_Reloads()
    {
        using var store = SettingsStore.Load(_file);
        store.Set("motion", JsonValue.Create("snappy"));
        var ownText = File.ReadAllText(_file);

        var edited = NextChange(store, "motion");
        File.WriteAllText(_file, "{\"motion\":\"smooth\"}");
        await edited.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("smooth", store.Motion);

        var reverted = NextChange(store, "motion");
        File.WriteAllText(_file, ownText);
        await reverted.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("snappy", store.Motion);
    }

    [Fact]
    public async Task ThrowingSubscriber_DoesNotCrashWatcher()
    {
        using var store = SettingsStore.Load(_file);
        store.Changed += _ => throw new InvalidOperationException("subscriber bug");

        File.WriteAllText(_file, "{\"motion\":\"snappy\"}");

        await WaitUntil(() => store.Motion == "snappy", TimeSpan.FromSeconds(3));
        Assert.Equal("snappy", store.Motion);
    }

    [Fact]
    public void LockedFile_IsNotRenamedCorrupt()
    {
        File.WriteAllText(_file, "{\"accent\":\"green\"}");
        using var locked = new FileStream(_file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var store = SettingsStore.Load(_file);
        Assert.Equal("auto", store.Accent);

        locked.Dispose();
        store.Set("motion", JsonValue.Create("snappy"));

        Assert.False(File.Exists(_file + ".corrupt"));
        var root = JsonNode.Parse(File.ReadAllText(_file))!.AsObject();
        Assert.Equal("snappy", root["motion"]!.GetValue<string>());
    }

    [Fact]
    public void LockedAtLoad_SetKeepsExistingKeys()
    {
        File.WriteAllText(_file, "{\"futureKey\":1,\"motion\":\"smooth\"}");
        using var locked = new FileStream(_file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var store = SettingsStore.Load(_file);
        locked.Dispose();

        store.Set("accent", JsonValue.Create("blue"));

        var root = JsonNode.Parse(File.ReadAllText(_file))!.AsObject();
        Assert.Equal(1, root["futureKey"]!.GetValue<int>());
        Assert.Equal("smooth", root["motion"]!.GetValue<string>());
        Assert.Equal("blue", root["accent"]!.GetValue<string>());
    }

    [Fact]
    public async Task Load_LockReleasedWithinTheRetry_ReadsTheFile()
    {
        File.WriteAllText(_file, "{\"accent\":\"green\"}");
        var locked = new FileStream(_file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var release = Task.Run(async () =>
        {
            await Task.Delay(5);
            locked.Dispose();
        });

        using var store = SettingsStore.Load(_file);
        await release;

        Assert.Equal("green", store.Accent);
    }

    [Fact]
    public void Set_ReadsAnEditMadeJustBefore_KeepsIt()
    {
        using var store = SettingsStore.Load(_file);
        // An outside edit, made before the watcher's 100 ms reload runs.
        File.WriteAllText(_file, "{\"volume\":0.5}");

        store.Set("sounds", JsonValue.Create(false));

        var root = JsonNode.Parse(File.ReadAllText(_file))!.AsObject();
        Assert.Equal(0.5, root["volume"]!.GetValue<double>());
        Assert.False(root["sounds"]!.GetValue<bool>());
        Assert.Equal(0.5, store.Volume);
    }

    [Fact]
    public void StillLocked_SetThrows_FileUnchanged()
    {
        const string text = "{\"futureKey\":1,\"motion\":\"smooth\"}";
        File.WriteAllText(_file, text);
        using var locked = new FileStream(_file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var store = SettingsStore.Load(_file);

        Assert.ThrowsAny<IOException>(() => store.Set("accent", JsonValue.Create("blue")));
        Assert.Equal("auto", store.Accent);

        locked.Position = 0;
        using var reader = new StreamReader(locked);
        Assert.Equal(text, reader.ReadToEnd());
    }

    [Fact]
    public async Task OwnWrite_IsIgnoredByWatcher()
    {
        using var store = SettingsStore.Load(_file);
        int count = 0;
        store.Changed += _ => Interlocked.Increment(ref count);

        store.Set("motion", JsonValue.Create("snappy"));
        await Task.Delay(400);

        Assert.Equal(1, Volatile.Read(ref count));
    }

    /// The next Changed that names the key. The subscription stays, which is harmless in a test.
    private static Task<IReadOnlyCollection<string>> NextChange(SettingsStore store, string key)
    {
        var changed = new TaskCompletionSource<IReadOnlyCollection<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        store.Changed += keys =>
        {
            if (keys.Contains(key))
            {
                changed.TrySetResult(keys);
            }
        };
        return changed.Task;
    }

    private static async Task WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }
    }

    [Theory]
    [InlineData("auto", "#00F5D4", "#123456", "#123456")]
    [InlineData("auto", "#00F5D4", null, "#0A84FF")]
    [InlineData("blue", "#00F5D4", "#123456", "#0A84FF")]
    [InlineData("custom", "#00F5D4", "#123456", "#00F5D4")]
    public void ResolveAccent_MapsNameToHex(string accent, string custom, string? system, string expected)
    {
        Assert.Equal(expected, Appearance.ResolveAccent(accent, custom, system));
    }
}
