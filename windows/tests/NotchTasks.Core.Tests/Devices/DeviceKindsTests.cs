using NotchTasks.Core.Devices;
using Xunit;

namespace NotchTasks.Core.Tests.Devices;

/// DeviceKinds ports the Mac's device symbols: the name wins over the category, and the category wins over the fallback.
public class DeviceKindsTests
{
    [Theory]
    [InlineData("AirPods Max", "airpodsmax")]
    [InlineData("AirPods Pro", "airpodspro")]
    [InlineData("AirPods", "airpods")]
    public void Symbol_NameBeatsCategory(string name, string expected)
    {
        // The category would say headphones, but the name is checked first.
        Assert.Equal(expected, DeviceKinds.Symbol(name, ["Audio.Headphone"]));
    }

    [Theory]
    [InlineData("Headset", "Audio.Headset", "headphones")]
    [InlineData("Keyboard", "Input.Keyboard", "keyboard")]
    [InlineData("Mouse", "Input.Mouse", "computermouse")]
    [InlineData("Trackpad", "Input.Touchpad", "rectangle.and.hand.point.up.left")]
    [InlineData("Speaker", "Audio.Speaker", "hifispeaker")]
    [InlineData("Controller", "Input.Gaming", "gamecontroller")]
    [InlineData("Phone", "Communication.Phone", "iphone")]
    [InlineData("Disk", "Storage", "externaldrive")]
    public void Symbol_Categories(string name, string category, string expected)
    {
        Assert.Equal(expected, DeviceKinds.Symbol(name, [category]));
    }

    [Fact]
    public void Symbol_UnknownCategory_WatchOrFallback()
    {
        Assert.Equal("applewatch", DeviceKinds.Symbol("Galaxy Watch", ["Other"]));
        Assert.Equal("dot.radiowaves.left.and.right", DeviceKinds.Symbol("Gadget", ["Other"]));
    }

    [Fact]
    public void Shown_FiltersMonitorsAndPrinters()
    {
        Assert.True(DeviceKinds.Shown(["Audio.Headphone"]));
        Assert.True(DeviceKinds.Shown(["Input.Keyboard"]));
        Assert.True(DeviceKinds.Shown(["Communication.Phone"]));
        Assert.True(DeviceKinds.Shown(["Storage.Portable"]));

        Assert.False(DeviceKinds.Shown(["Display.Monitor"]));
        Assert.False(DeviceKinds.Shown(["Printer"]));
        Assert.False(DeviceKinds.Shown(["Network.Adapter"]));
        Assert.False(DeviceKinds.Shown(["MediaServer"]));
        Assert.False(DeviceKinds.Shown([]));
    }
}
