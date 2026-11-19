namespace NotchTasks.Core.Agents;

/// Project folder names from a command's own paths, for when the working folder is unknown (Windows). Ported from
/// electron/src/core/devscan.js: folderFromArgv and project. 3c reuses them.
public static class Folders
{
    // Folders under a project that name a platform, not the project: ".../mobile-app/ios" is "mobile-app".
    private static readonly string[] PlatformDirs = { "ios", "macos", "android", "app", "web", "linux", "windows" };

    /// The project folder from the command's paths: the folder that holds node_modules for a package script, else the
    /// folder of the first path argument. Null when there is none.
    public static string? FolderFromArgv(IReadOnlyList<string> argv)
    {
        for (var i = 1; i < argv.Count; i++)
        {
            var x = argv[i];
            // flags and /t:Target switches are not paths
            if (x.StartsWith('-') || IsSwitch(x) || (x.IndexOf('\\') < 0 && x.IndexOf('/') < 0))
            {
                continue;
            }
            var n = x.Replace('\\', '/');
            var nm = n.LastIndexOf("/node_modules/", StringComparison.Ordinal);
            var dir = nm >= 0 ? n[..nm] : n[..n.LastIndexOf('/')];
            var name = Project(dir);
            return name.Length > 0 ? name : null;
        }
        return null;
    }

    /// The project folder name from a path, skipping platform subfolders. Both separators work.
    public static string Project(string p)
    {
        var parts = p.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries).ToList();
        while (parts.Count > 1 && PlatformDirs.Contains(parts[^1].ToLowerInvariant()))
        {
            parts.RemoveAt(parts.Count - 1);
        }
        return parts.Count > 0 ? parts[^1] : string.Empty;
    }

    /// A switch such as "/t:Build" or "-p:Config": a slash or dash, then text with no slash before a colon.
    private static bool IsSwitch(string x)
    {
        if (x.Length == 0 || (x[0] != '/' && x[0] != '-'))
        {
            return false;
        }
        var colon = x.IndexOf(':');
        if (colon < 0)
        {
            return false;
        }
        for (var i = 1; i < colon; i++)
        {
            if (x[i] == '\\' || x[i] == '/')
            {
                return false;
            }
        }
        return true;
    }
}
