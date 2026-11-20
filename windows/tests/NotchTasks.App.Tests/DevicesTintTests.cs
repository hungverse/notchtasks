using System.Reflection;
using System.Text.Json.Nodes;
using NotchTasks.Services.Devices;
using Xunit;

namespace NotchTasks.AppTests;

/// The devices module's tint: a devices colour that is empty or blank is no colour (the toast falls back to the default blue). A watcher
/// update is a list change only for a container's connection or name.
public sealed class DevicesTintTests
{
    private static string? TintFor(AppState state)
    {
        var method = typeof(AppState).GetMethod("DevicesTint", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (string?)method.Invoke(state, null);
    }

    [Theory]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    public void BlankDevicesColour_IsNoTint(string value)
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            state.Settings.Set("moduleColors", JsonNode.Parse($"{{\"devices\": {value}}}"));

            Assert.Null(TintFor(state));
        });
    }

    [Fact]
    public void ChosenDevicesColour_IsTheTint()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            state.Settings.Set("moduleColors", JsonNode.Parse("{\"devices\": \"#30D158\"}"));

            Assert.Equal("#30D158", TintFor(state));
        });
    }

    [Fact]
    public void ListChanged_OnlyForConnectionOrName()
    {
        Assert.True(ContainerReader.ListChanged(new Dictionary<string, object> { ["System.Devices.Connected"] = true }));
        Assert.True(ContainerReader.ListChanged(new Dictionary<string, object> { ["System.ItemNameDisplay"] = "Mouse" }));
        Assert.False(ContainerReader.ListChanged(new Dictionary<string, object> { ["System.Devices.Something"] = 1 }));
    }
}
