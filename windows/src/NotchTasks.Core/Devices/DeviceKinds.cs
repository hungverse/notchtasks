namespace NotchTasks.Core.Devices;

/// Maps an accessory to its symbol and decides which accessories the pill lists (the Mac's device kinds). The name is
/// checked first, then the first category with a known prefix, then a watch or a generic radio icon.
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
        "Audio.", "Input.", "Communication.Phone", "Storage.",
    ];

    /// The symbol for an accessory: AirPods by name first, then the category, then applewatch for a watch by name.
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

        return name.Contains("watch", StringComparison.OrdinalIgnoreCase)
            ? "applewatch"
            : "dot.radiowaves.left.and.right";
    }

    /// True when the pill lists the accessory: audio, input, phones and storage. Monitors, printers, network and media
    /// servers are left out.
    public static bool Shown(IReadOnlyList<string> categories) =>
        categories.Any(c => ShownPrefixes.Any(p => c.StartsWith(p, StringComparison.OrdinalIgnoreCase)));
}
