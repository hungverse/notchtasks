namespace NotchTasks.Core.Devices;

/// Maps an accessory to its symbol and decides which accessories the pill lists (the Mac's device kinds). The name is
/// checked first, then the first category with a known prefix, then a USB cable, a watch or a generic radio icon.
public static class DeviceKinds
{
    private static readonly (string Prefix, string Symbol)[] CategorySymbols =
    [
        ("Audio.Headphone", "headphones"),
        ("Audio.Headset", "headphones"),
        ("Input.Keyboard", "keyboard"),
        ("Input.Mouse", "computermouse"),
        ("Input.Touchpad", "rectangle.and.hand.point.up.left"),
        ("Audio.Speaker", "hifispeaker"),
        ("Input.Gaming", "gamecontroller"),
        ("Communication.Phone", "iphone"),
        ("Storage", "externaldrive"),
    ];

    private static readonly string[] ShownPrefixes =
    [
        "Audio.", "Input.", "Communication.Phone", "Storage.", "Bus.",
    ];

    /// The instance-id prefixes of an accessory's bus: USB, and the Bluetooth stacks (classic and low energy).
    private static readonly string[] BusPrefixes = [@"USB\", @"BTHENUM\", @"BTHLE\", @"BTHLEDEVICE\"];

    /// The Bluetooth instance-id prefixes: a node on one of them makes the container Bus.Bluetooth, else Bus.Usb.
    private static readonly string[] BluetoothPrefixes = [@"BTHENUM\", @"BTHLE\", @"BTHLEDEVICE\"];

    private const string KeyboardClass = "4d36e96b-e325-11ce-bfc1-08002be10318";
    private const string MouseClass = "4d36e96f-e325-11ce-bfc1-08002be10318";
    private const string AudioEndpointClass = "c166523c-fe0c-4a94-a586-f1a80cfbbf3e";
    private const string MediaClass = "4d36e96c-e325-11ce-bfc1-08002be10318";
    private const string DiskDriveClass = "4d36e967-e325-11ce-bfc1-08002be10318";
    private const string WpdClass = "eec5ad98-8080-425f-922a-dabf3de3f69a";

    /// The symbol for an accessory: AirPods by name first, then the category, then a USB cable, then applewatch for a watch by name.
    public static string Symbol(string name, IReadOnlyList<string> categories)
    {
        if (name.Contains("airpods max", StringComparison.OrdinalIgnoreCase))
        {
            return "airpodsmax";
        }
        if (name.Contains("airpods pro", StringComparison.OrdinalIgnoreCase))
        {
            return "airpodspro";
        }
        if (name.Contains("airpod", StringComparison.OrdinalIgnoreCase))
        {
            return "airpods";
        }

        foreach (var category in categories)
        {
            foreach (var (prefix, symbol) in CategorySymbols)
            {
                if (category.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return symbol;
                }
            }
        }

        // A USB accessory the name and kinds do not know: the Mac's USB symbol.
        if (categories.Any(c => c == "Bus.Usb"))
        {
            return "cable.connector";
        }

        return name.Contains("watch", StringComparison.OrdinalIgnoreCase)
            ? "applewatch"
            : "dot.radiowaves.left.and.right";
    }

    /// True when the pill lists the accessory: audio, input, phones, storage and a bus (USB or Bluetooth). Monitors, printers,
    /// network and media servers are left out.
    public static bool Shown(IReadOnlyList<string> categories) =>
        categories.Any(c => ShownPrefixes.Any(p => c.StartsWith(p, StringComparison.OrdinalIgnoreCase)));

    /// Classifies a container from its device nodes (P3d-R3, amended). The nodes are (instance id, class GUID). A container with no
    /// node on USB or Bluetooth, or whose name says hub, is not an accessory: the result is empty. Otherwise the kinds come from the
    /// nodes' class GUIDs, in a fixed order and once each, and then the bus: Bus.Bluetooth for a Bluetooth node, else Bus.Usb.
    public static IReadOnlyList<string> Categories(string name, IReadOnlyList<(string InstanceId, string? ClassGuid)> nodes)
    {
        var onBus = nodes.Any(n => StartsWithAny(n.InstanceId, BusPrefixes));
        if (!onBus || name.Contains("hub", StringComparison.OrdinalIgnoreCase))
        {
            return Array.Empty<string>();
        }

        var classes = new HashSet<string>(
            nodes.Select(n => NormalizeGuid(n.ClassGuid)).Where(g => g is not null)!,
            StringComparer.Ordinal);
        bool Has(string guid) => classes.Contains(guid);

        var result = new List<string>();
        if (Has(KeyboardClass))
        {
            result.Add("Input.Keyboard");
        }
        if (Has(MouseClass))
        {
            result.Add("Input.Mouse");
        }
        if (Has(AudioEndpointClass) || Has(MediaClass))
        {
            result.Add(name.Contains("speaker", StringComparison.OrdinalIgnoreCase) ? "Audio.Speaker" : "Audio.Headphone");
        }
        if (Has(DiskDriveClass))
        {
            result.Add("Storage.Removable");
        }
        if (Has(WpdClass))
        {
            result.Add("Communication.Phone");
        }

        result.Add(nodes.Any(n => StartsWithAny(n.InstanceId, BluetoothPrefixes)) ? "Bus.Bluetooth" : "Bus.Usb");
        return result;
    }

    private static bool StartsWithAny(string instanceId, string[] prefixes) =>
        prefixes.Any(p => instanceId.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    /// A class GUID without its braces, in lower case, or null when there is none.
    private static string? NormalizeGuid(string? guid) =>
        string.IsNullOrWhiteSpace(guid) ? null : guid.Trim().Trim('{', '}').ToLowerInvariant();
}
