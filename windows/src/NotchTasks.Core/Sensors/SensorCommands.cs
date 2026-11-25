namespace NotchTasks.Core.Sensors;

/// What the helper exe is asked to do.
public enum SensorMode
{
    /// Serve the pipe. --parent: exit when that process ends. Without it (the logon task): stay until logoff.
    Serve,

    /// Copy the helper into Program Files, register the logon task, then serve. Elevated.
    InstallLogon,

    /// Stop the user's helper, remove the logon task and the Program Files copy. Elevated.
    UninstallLogon,

    /// Not elevated, from the installer: when this user has a sign-in helper, one UAC prompt runs UninstallLogon for them.
    UninstallOwn,
}

/// A parsed command line of the helper.
public sealed record SensorCommand(SensorMode Mode, string? Sid, int? Parent);

/// The helper's command lines: the app and the task build them with these, and the helper parses them with Parse.
public static class SensorArgs
{
    /// --serve [--parent pid] --user sid
    public static string Serve(string sid, int? parent) =>
        "--serve" + (parent is { } pid ? $" --parent {pid}" : string.Empty) + $" --user {sid}";

    /// --install-logon sid [--parent pid]
    public static string InstallLogon(string sid, int? parent) =>
        $"--install-logon {sid}" + (parent is { } pid ? $" --parent {pid}" : string.Empty);

    /// --uninstall-logon [sid]: the sid names the user whose helper is stopped.
    public static string UninstallLogonArgs(string? sid) =>
        "--uninstall-logon" + (sid is null ? string.Empty : " " + sid);

    /// The command for the arguments, or null when they are not one of the three forms, a SID is not a SID, or a pid is not a
    /// positive number.
    public static SensorCommand? Parse(string[] args)
    {
        if (args.Length == 0)
        {
            return null;
        }
        switch (args[0])
        {
            case "--uninstall-logon":
                if (args.Length == 1)
                {
                    return new SensorCommand(SensorMode.UninstallLogon, null, null);
                }
                return args.Length == 2 && SensorNames.IsSid(args[1]) ? new SensorCommand(SensorMode.UninstallLogon, args[1], null) : null;

            case "--uninstall-own":
                return args.Length == 1 ? new SensorCommand(SensorMode.UninstallOwn, null, null) : null;

            case "--install-logon":
                if (args.Length < 2 || !SensorNames.IsSid(args[1]))
                {
                    return null;
                }
                var installParent = ParseParent(args, 2);
                return installParent.Valid ? new SensorCommand(SensorMode.InstallLogon, args[1], installParent.Pid) : null;

            case "--serve":
                string? sid = null;
                int? parent = null;
                for (var i = 1; i < args.Length; i += 2)
                {
                    if (i + 1 >= args.Length)
                    {
                        return null;
                    }
                    if (args[i] == "--user")
                    {
                        sid = args[i + 1];
                    }
                    else if (args[i] == "--parent" && int.TryParse(args[i + 1], out var pid) && pid > 0)
                    {
                        parent = pid;
                    }
                    else
                    {
                        return null;
                    }
                }
                return SensorNames.IsSid(sid) ? new SensorCommand(SensorMode.Serve, sid, parent) : null;

            default:
                return null;
        }
    }

    /// The optional "--parent pid" pair from `start` on.
    private static (bool Valid, int? Pid) ParseParent(string[] args, int start)
    {
        if (args.Length == start)
        {
            return (true, null);
        }
        if (args.Length == start + 2 && args[start] == "--parent" && int.TryParse(args[start + 1], out var pid) && pid > 0)
        {
            return (true, pid);
        }
        return (false, null);
    }
}

/// The names the app and the helper agree on. The pipe and the stop event are per user, so one user's helper is not another's.
public static class SensorNames
{
    /// The scheduled task that runs this user's helper at logon: "NotchTasks Sensors <SID>", one per user.
    public static string TaskName(string sid) => "NotchTasks Sensors " + sid;

    /// The marker file, in the shared folder, that says this user has a sign-in helper. Install writes it, uninstall deletes it.
    public static string MarkerName(string sid) => sid + ".signin";

    /// The shared Program Files folder: the copy the logon tasks run (as SYSTEM), and the markers.
    public static string ProgramFilesFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), FolderName);

    /// The full path of this user's marker.
    public static string MarkerPath(string sid) => Path.Combine(ProgramFilesFolder, MarkerName(sid));

    /// The Program Files folder the helper runs from when it is started at logon.
    public const string FolderName = "NotchTasks Sensors";

    /// The helper's exe name.
    public const string ExeName = "NotchTasksSensors.exe";

    /// The pipe the app reads from: NotchTasks.Sensors.<user SID>.
    public static string PipeName(string sid) => "NotchTasks.Sensors." + sid;

    /// The manual-reset event the app sets to stop the helper. It is a Global\ name, so the app (user session), the elevated installer
    /// and the helper (session 0, as SYSTEM) all reach the same event. Its ACL grants Modify and Synchronize to SYSTEM, Administrators
    /// and the user. Accepted Minor: a non-elevated program may create this name first, and then this user's helper exits quietly
    /// (a display denial of the temperatures only; no privilege is gained).
    public static string StopEventName(string sid) => @"Global\NotchTasks.Sensors.Stop." + sid;

    /// True for a Windows SID in its text form (S-1-5-21-...). Anything else is not a SID.
    public static bool IsSid(string? text)
    {
        if (text is null || text.Length <= 4 || text.Length > 128 || !text.StartsWith("S-1-", StringComparison.Ordinal))
        {
            return false;
        }
        foreach (var c in text.AsSpan(4))
        {
            if (!char.IsAsciiDigit(c) && c != '-')
            {
                return false;
            }
        }
        return !text.Contains("--", StringComparison.Ordinal) && !text.EndsWith('-');
    }
}

/// The files the helper needs to run from its own folder: its own build, the Core assembly it references, and the System.Management
/// package (with its dependency and its runtime-specific copy, which the deps file points at under runtimes\).
public static class SensorFiles
{
    /// Always copied. A missing one fails the copy.
    public static readonly IReadOnlyList<string> Required = new[]
    {
        SensorNames.ExeName,
        "NotchTasksSensors.dll",
        "NotchTasksSensors.runtimeconfig.json",
        "NotchTasksSensors.deps.json",
        "NotchTasks.Core.dll",
    };

    /// Copied when the build produced them.
    public static readonly IReadOnlyList<string> Optional = new[]
    {
        "System.Management.dll",
        "System.CodeDom.dll",
    };

    /// The runtime-specific assemblies folder (copied whole, with its paths).
    public const string RuntimesFolder = "runtimes";

    /// Copies the helper's files from sourceDir to targetDir (created when missing), with the runtimes folder, and returns the
    /// names copied. Throws FileNotFoundException when a required file is missing from sourceDir.
    public static IReadOnlyList<string> CopyFrom(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        var copied = new List<string>();
        foreach (var name in Required)
        {
            var from = Path.Combine(sourceDir, name);
            if (!File.Exists(from))
            {
                throw new FileNotFoundException("The sensor helper file is missing: " + from, from);
            }
            File.Copy(from, Path.Combine(targetDir, name), overwrite: true);
            copied.Add(name);
        }
        foreach (var name in Optional)
        {
            var from = Path.Combine(sourceDir, name);
            if (File.Exists(from))
            {
                File.Copy(from, Path.Combine(targetDir, name), overwrite: true);
                copied.Add(name);
            }
        }
        var runtimes = Path.Combine(sourceDir, RuntimesFolder);
        if (Directory.Exists(runtimes))
        {
            foreach (var file in Directory.EnumerateFiles(runtimes, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(sourceDir, file);
                var to = Path.Combine(targetDir, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(file, to, overwrite: true);
                copied.Add(relative);
            }
        }
        return copied;
    }
}

/// The logon task's definition (Task Scheduler XML). It runs as SYSTEM, never as the user: a user-level program can write the
/// user's environment and so the environment of anything the user starts, but not SYSTEM's. It starts at that user's logon and
/// never stops for time or power.
public static class SensorTask
{
    /// The task XML for the user's SID, the absolute exe path and its arguments. Values are escaped for XML. Throws when the path
    /// is not absolute: the action must name the exact copy in Program Files.
    public static string Xml(string sid, string exePath, string arguments)
    {
        if (!Path.IsPathFullyQualified(exePath))
        {
            throw new ArgumentException("The task action must be an absolute path.", nameof(exePath));
        }
        return Build(sid, exePath, arguments);
    }

    private static string Build(string sid, string exePath, string arguments) => $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo>
            <Description>Reads the temperatures for the NotchTasks notch while you are signed in.</Description>
          </RegistrationInfo>
          <Triggers>
            <LogonTrigger>
              <Enabled>true</Enabled>
              <UserId>{Escape(sid)}</UserId>
            </LogonTrigger>
          </Triggers>
          <Principals>
            <Principal id="Author">
              <UserId>S-1-5-18</UserId>
              <LogonType>ServiceAccount</LogonType>
              <RunLevel>HighestAvailable</RunLevel>
            </Principal>
          </Principals>
          <Settings>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <AllowHardTerminate>true</AllowHardTerminate>
            <StartWhenAvailable>true</StartWhenAvailable>
            <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
            <AllowStartOnDemand>true</AllowStartOnDemand>
            <Enabled>true</Enabled>
            <Hidden>true</Hidden>
            <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
            <Priority>7</Priority>
          </Settings>
          <Actions Context="Author">
            <Exec>
              <Command>{Escape(exePath)}</Command>
              <Arguments>{Escape(arguments)}</Arguments>
            </Exec>
          </Actions>
        </Task>
        """;

    private static string Escape(string text) => text
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal)
        .Replace("'", "&apos;", StringComparison.Ordinal);
}
