using NotchTasks.Core.Devices;
using Xunit;

namespace NotchTasks.Core.Tests.Devices;

/// Categories classifies a container from its device nodes (P3d-R3, amended): the node's class GUID gives the input or audio
/// kind, the bus of the node's instance id gives Bus.Usb or Bus.Bluetooth, and a hub or a non-bus container is not an accessory.
public class DeviceCategoriesTests
{
    private const string KeyboardClass = "{4d36e96b-e325-11ce-bfc1-08002be10318}";
    private const string MouseClass = "{4d36e96f-e325-11ce-bfc1-08002be10318}";
    private const string AudioEndpointClass = "{c166523c-fe0c-4a94-a586-f1a80cfbbf3e}";
    private const string UsbClass = "{36fc9e60-c465-11cf-8056-444553540000}";
    private const string HidClass = "{745a17a0-74d3-11d0-b6fe-00a0c90f57da}";

    [Fact]
    public void Categories_UsbReceiver_KeyboardMouseUsb()
    {
        // The Logitech receiver of the probe: a HID keyboard, a HID mouse and the USB composite node.
        var nodes = new (string InstanceId, string? ClassGuid)[]
        {
            (@"HID\VID_046D&PID_C52B&MI_00\7&1A2B3C4D&0&0000", HidClass),
            (@"HID\VID_046D&PID_C52B&MI_00\7&1A2B3C4D&0&0000", KeyboardClass),
            (@"HID\VID_046D&PID_C52B&MI_01\7&1A2B3C4D&0&0001", MouseClass),
            (@"USB\VID_046D&PID_C52B\5&2A3B4C5D&0&2", UsbClass),
        };

        var categories = DeviceKinds.Categories("USB Receiver", nodes);

        Assert.Equal(new[] { "Input.Keyboard", "Input.Mouse", "Bus.Usb" }, categories);
    }

    [Fact]
    public void Categories_HdAudioSpeakers_Empty()
    {
        // Built-in speakers sit on HDAUDIO, not on a bus that is an accessory: they are not listed.
        var nodes = new (string InstanceId, string? ClassGuid)[]
        {
            (@"HDAUDIO\FUNC_01&VEN_10EC&DEV_0295\4&1F2E3D4C&0&0001", AudioEndpointClass),
        };

        Assert.Empty(DeviceKinds.Categories("Speakers (High Definition Audio Device)", nodes));
    }

    [Fact]
    public void Categories_Hub_Empty()
    {
        // A USB root hub is on a USB bus but is a hub, which the Mac does not list.
        var nodes = new (string InstanceId, string? ClassGuid)[]
        {
            (@"USB\ROOT_HUB30\4&2A3B4C5D&0&0", UsbClass),
        };

        Assert.Empty(DeviceKinds.Categories("USB Root Hub (USB 3.0)", nodes));
        Assert.Empty(DeviceKinds.Categories("Generic USB HUB", nodes));
    }

    [Fact]
    public void Categories_Bluetooth_Headphones()
    {
        // A paired headset: its Bluetooth node is an audio endpoint, and the name does not say speaker.
        var nodes = new (string InstanceId, string? ClassGuid)[]
        {
            (@"BTHENUM\{0000110B-0000-1000-8000-00805F9B34FB}_VID&0002054C_PID&0BBE\7&1A2B3C4D&0&000000000000", AudioEndpointClass),
        };

        Assert.Equal(new[] { "Audio.Headphone", "Bus.Bluetooth" }, DeviceKinds.Categories("WH-1000XM4", nodes));
    }

    [Fact]
    public void Categories_BluetoothLowEnergySpeaker_Speaker()
    {
        var nodes = new (string InstanceId, string? ClassGuid)[]
        {
            (@"BTHLE\DEV_C8A1B2C3D4E5\7&1A2B3C4D&0&C8A1B2C3D4E5", AudioEndpointClass),
        };

        Assert.Equal(new[] { "Audio.Speaker", "Bus.Bluetooth" }, DeviceKinds.Categories("JBL Speaker", nodes));
    }

    [Fact]
    public void Symbol_UsbUnknown_CableConnector()
    {
        Assert.Equal("cable.connector", DeviceKinds.Symbol("USB Gadget", ["Bus.Usb"]));
    }

    [Fact]
    public void Shown_BusOnly_True()
    {
        Assert.True(DeviceKinds.Shown(["Bus.Usb"]));
        Assert.True(DeviceKinds.Shown(["Bus.Bluetooth"]));
    }
}
