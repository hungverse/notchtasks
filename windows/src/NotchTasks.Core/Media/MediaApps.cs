namespace NotchTasks.Core.Media;

/// Display names and exe names for the apps that publish to SMTC (Mac: the app names the Now Playing card shows).
/// Name and Exe go by the SourceAppUserModelId: a known id from the table, then a fallback from the id's shape.
public static class MediaApps
{
    private sealed record Known(string Name, string? Exe);

    /// The table is matched ordinal-ignore-case. An id that is not here falls back to its shape (see Name and Exe).
    private static readonly Dictionary<string, Known> KnownApps = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Spotify.exe"] = new("Spotify", "spotify.exe"),
        ["SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify"] = new("Spotify", "spotify.exe"),
        ["Chrome"] = new("Google Chrome", "chrome.exe"),
        ["MSEdge"] = new("Microsoft Edge", "msedge.exe"),
        ["308046B0AF4A39CB"] = new("Firefox", "firefox.exe"),
        ["Brave"] = new("Brave", "brave.exe"),
        ["Opera"] = new("Opera", "opera.exe"),
        ["vlc.exe"] = new("VLC", "vlc.exe"),
        ["Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic"] = new("Media Player", null),
        ["AppleInc.AppleMusicWin_nzyj5cx40ttqa!App"] = new("Apple Music", null),
    };

    /// The display name. A known id gives its table name. A .exe gives its file name without the extension. A package id
    /// ("Family_publisher!App") gives the family name before "_" with dots turned into spaces. Anything else is the id itself.
    public static string Name(string aumid)
    {
        if (KnownApps.TryGetValue(aumid, out var known))
        {
            return known.Name;
        }

        if (aumid.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFileNameWithoutExtension(aumid);
        }

        var bang = aumid.IndexOf('!');
        if (bang >= 0)
        {
            var family = aumid[..bang];
            var underscore = family.IndexOf('_');
            var name = underscore >= 0 ? family[..underscore] : family;
            return name.Replace('.', ' ');
        }

        return aumid;
    }

    /// The exe name, lower-cased, that the app's processes run as, or null when there is none (packaged apps).
    /// A known id gives its table exe. An unknown .exe id is its own exe, lower-cased.
    public static string? Exe(string aumid)
    {
        if (KnownApps.TryGetValue(aumid, out var known))
        {
            return known.Exe;
        }

        return aumid.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? aumid.ToLowerInvariant() : null;
    }
}
