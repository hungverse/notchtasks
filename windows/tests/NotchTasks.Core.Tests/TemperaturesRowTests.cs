using NotchTasks.Core.Asks;
using NotchTasks.Core.Permissions;
using NotchTasks.Core.Settings;
using Xunit;

namespace NotchTasks.Core.Tests;

/// The optional Temperatures row: it is in the pane, and it is never counted as needed.
[Collection("L10n")]
public sealed class TemperaturesRowTests
{
    private static PermissionFacts Facts(PermState temperatures) =>
        new(null, false, null, null, null,
            new Dictionary<AgentKind, bool?>(),
            Enum.GetValues<AgentKind>().ToDictionary(kind => kind, _ => true),
            temperatures);

    private static PermItem Row(PermState temperatures) =>
        PermissionItems.Build(Facts(temperatures)).Single(item => item.Id == PermissionItems.TemperaturesId);

    [Fact]
    public void Build_HasTheTemperaturesRow_ThatIsOptional()
    {
        var row = Row(PermState.NotAsked);

        Assert.True(row.Applies);
        Assert.Equal("", row.Glyph);
        Assert.Equal(PermissionItems.TemperaturesId, row.Id);
    }

    [Fact]
    public void Temperatures_IsNeverNeeded_WhateverModulesAreOn()
    {
        var row = Row(PermState.Granted);
        var everything = ModuleCatalog.All.ToHashSet(StringComparer.Ordinal);

        Assert.False(PermissionItems.Needed(row, everything, notifications: true));
        Assert.Equal(PermGroup.Granted, PermissionItems.GroupOf(row, everything, notifications: true));
    }

    [Fact]
    public void Temperatures_NotGranted_IsNotInUse_NotNeeded()
    {
        var row = Row(PermState.NotAsked);
        var everything = ModuleCatalog.All.ToHashSet(StringComparer.Ordinal);

        Assert.False(PermissionItems.Needed(row, everything, notifications: true));
        Assert.Equal(PermGroup.NotInUse, PermissionItems.GroupOf(row, everything, notifications: true));
    }
}

public sealed class SensorSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nt-sensor-settings-" + Guid.NewGuid().ToString("N"));

    public SensorSettingsTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public void SensorHelperKeys_DefaultToOff()
    {
        using var store = SettingsStore.Load(Path.Combine(_dir, "settings.json"));

        Assert.False(store.SensorHelper);
        Assert.False(store.SensorHelperAtSignIn);
    }
}
