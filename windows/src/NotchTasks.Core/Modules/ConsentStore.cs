using System.IO;

namespace NotchTasks.Core.Modules;

/// Which apps are using the mic and the camera right now, from the consent store under HKCU. An app is in use when its entry
/// has a LastUsedTimeStart and a LastUsedTimeStop of 0. Packaged apps are the direct subkeys (named by their family name);
/// desktop apps are under NonPackaged, named by their exe path with '#' in place of '\'. Pure: the registry is passed in,
/// and a missing or unreadable key gives empty lists.
public static class ConsentStore
{
    public const string MicKey = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";
    public const string CameraKey = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam";

    private const string NonPackaged = "NonPackaged";

    /// The apps using the mic and the camera, as ids: the lower-case family name of a packaged app, or the lower-case exe
    /// file name of a desktop app. ownExe (this app's exe) is left out.
    public static (IReadOnlyList<string> MicApps, IReadOnlyList<string> CameraApps) InUse(IRegistry r, string ownExe)
    {
        var own = Path.GetFileName(ownExe);
        return (AppsInUse(r, MicKey, own), AppsInUse(r, CameraKey, own));
    }

    private static IReadOnlyList<string> AppsInUse(IRegistry r, string root, string own)
    {
        var apps = new List<string>();
        foreach (var name in r.SubKeyNames(root))
        {
            if (string.Equals(name, NonPackaged, StringComparison.OrdinalIgnoreCase))
            {
                AddDesktopApps(r, root + @"\" + name, own, apps);
            }
            else if (IsInUse(r, root + @"\" + name))
            {
                apps.Add(name.ToLowerInvariant());
            }
        }
        return apps.Distinct(StringComparer.Ordinal).ToList();
    }

    private static void AddDesktopApps(IRegistry r, string key, string own, List<string> apps)
    {
        foreach (var name in r.SubKeyNames(key))
        {
            var exe = Path.GetFileName(name.Replace('#', '\\')).ToLowerInvariant();
            if (exe.Length == 0 || string.Equals(exe, own, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (IsInUse(r, key + @"\" + name))
            {
                apps.Add(exe);
            }
        }
    }

    /// In use: a start time is there and the stop time is 0.
    private static bool IsInUse(IRegistry r, string key) =>
        r.GetLong(key, "LastUsedTimeStart") is not null && r.GetLong(key, "LastUsedTimeStop") == 0;
}
