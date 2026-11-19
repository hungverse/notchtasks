namespace NotchTasks.Core.Modules;

/// The apps that take calls, by the id the consent store gives them (a desktop app's exe name, lower-case, or a packaged
/// app's family name). Their display names are the Mac's Sensors.name. A browser counts as a call app only when no dedicated
/// one is using the mic (CallApp).
public static class CallApps
{
    /// The browsers: a call in one of them is only a call when nothing else is.
    private static readonly string[] Browsers =
    {
        "chrome.exe", "msedge.exe", "firefox.exe", "brave.exe", "arc.exe", "opera.exe",
    };

    /// The known call apps: each id and its display name.
    public static IReadOnlyDictionary<string, string> Names { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["ms-teams.exe"] = "Teams",
        ["msteams_8wekyb3d8bbwe"] = "Teams",
        ["teams.exe"] = "Teams",
        ["zoom.exe"] = "Zoom",
        ["slack.exe"] = "Slack",
        ["discord.exe"] = "Discord",
        ["skype.exe"] = "Skype",
        ["whatsapp.exe"] = "WhatsApp",
        ["5319275a.whatsappdesktop_cv1g1gvanyjgm"] = "WhatsApp",
        ["telegram.exe"] = "Telegram",
        ["messenger.exe"] = "Messenger",
        ["zalo.exe"] = "Zalo",
        ["ciscocollabhost.exe"] = "Webex",
        ["webex.exe"] = "Webex",
        ["chrome.exe"] = "Chrome",
        ["msedge.exe"] = "Edge",
        ["firefox.exe"] = "Firefox",
        ["brave.exe"] = "Brave",
        ["arc.exe"] = "Arc",
        ["opera.exe"] = "Opera",
    };

    /// The call app among the apps using the mic: the first known one that is not a browser, else the first known one, else
    /// null (no call app is using the mic).
    public static string? CallApp(IReadOnlyList<string> micApps)
    {
        var known = micApps.Where(Names.ContainsKey).ToList();
        if (known.Count == 0)
        {
            return null;
        }
        return known.FirstOrDefault(id => !Browsers.Contains(id, StringComparer.OrdinalIgnoreCase)) ?? known[0];
    }

    /// The display name of an app: its name when it is known, else its exe name without ".exe".
    public static string Name(string id)
    {
        if (Names.TryGetValue(id, out var name))
        {
            return name;
        }
        return id.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? id[..^4] : id;
    }
}
