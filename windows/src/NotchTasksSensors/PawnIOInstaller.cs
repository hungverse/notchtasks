using NotchTasks.Core.Sensors;

namespace NotchTasksSensors;

/// The PawnIO step: the driver the hardware library reads the CPU through is installed system-wide, once. It runs only in the elevated
/// helper that the user's click started (the same UAC prompt as Turn on), before the helper serves. The order: a present service is
/// enough; else winget installs it, when the winget that ships with Windows is in its admin-only folder and Microsoft signed it; else the
/// signed setup from the PawnIO release is downloaded, held locked from its check to the end of its run, and run.
internal sealed class PawnIOInstaller
{
    public const string ServiceName = "PawnIO";
    public const string WingetArguments =
        "install --id namazso.PawnIO --exact --silent --scope machine --accept-package-agreements --accept-source-agreements";
    public const string SetupArguments = "-install -silent";

    private readonly IPawnIOSystem _system;

    public PawnIOInstaller(IPawnIOSystem system)
    {
        _system = system;
    }

    /// The exit code: PawnIOExit.Installed when the service exists afterwards, else the code of the failure.
    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        if (_system.ServiceExists(ServiceName))
        {
            return PawnIOExit.Installed;
        }

        int failure;
        var winget = _system.FindWinget();
        if (winget is not null && _system.IsMicrosoftSigned(winget))
        {
            failure = _system.Run(winget, WingetArguments) == 0 ? PawnIOExit.Installed : PawnIOExit.WingetFailed;
        }
        else
        {
            // The setup stays open, read-only and shared for reading only, from here until its run has exited: nothing can change,
            // rename or delete it in between, and the check below reads the same file that runs.
            using var setup = await _system.DownloadSetupAsync(ct);
            if (setup is null)
            {
                return PawnIOExit.DownloadFailed;
            }
            if (!_system.HasValidSignature(setup.Path))
            {
                return PawnIOExit.BadSignature;
            }
            failure = _system.Run(setup.Path, SetupArguments) == 0 ? PawnIOExit.Installed : PawnIOExit.InstallerFailed;
        }

        if (_system.ServiceExists(ServiceName))
        {
            return PawnIOExit.Installed;
        }
        return failure == PawnIOExit.Installed ? PawnIOExit.ServiceMissing : failure;
    }

    /// The winget folder of the Windows package: the admin-only %ProgramFiles%\WindowsApps, the newest version of
    /// Microsoft.DesktopAppInstaller. Pure: it picks from the folder names, so the choice is tested without the folder.
    public static string? NewestWingetFolder(IEnumerable<string> folderNames)
    {
        const string Prefix = "Microsoft.DesktopAppInstaller_";
        const string Suffix = "_x64__8wekyb3d8bbwe";
        string? best = null;
        Version? bestVersion = null;
        foreach (var name in folderNames)
        {
            if (!name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) || !name.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var text = name[Prefix.Length..^Suffix.Length];
            if (!Version.TryParse(text, out var version))
            {
                continue;
            }
            if (bestVersion is null || version > bestVersion)
            {
                best = name;
                bestVersion = version;
            }
        }
        return best;
    }
}

/// The machine's side of the PawnIO step: the service, the winget package, the processes, the download and the signatures. The real one
/// is WindowsPawnIOSystem; the tests pass a fake.
internal interface IPawnIOSystem
{
    /// True when the service with this name is registered.
    bool ServiceExists(string name);

    /// The full path of the winget that ships with Windows (its admin-only folder, the newest version), or null when there is none. A
    /// winget found anywhere else (PATH, the user's alias folder) is never returned.
    string? FindWinget();

    /// True when the file has a valid Authenticode signature and its signer is Microsoft Corporation.
    bool IsMicrosoftSigned(string path);

    /// Runs a program with the arguments, waits for it, and returns its exit code. A program that does not start returns -1.
    int Run(string fileName, string arguments);

    /// Downloads the latest PawnIO setup and returns it held open: the file cannot be written, renamed or deleted until the returned
    /// object is disposed, and disposing it deletes the file. Null when the download fails.
    Task<IPawnIOSetupFile?> DownloadSetupAsync(CancellationToken ct);

    /// True when the file has a valid Authenticode signature (WinVerifyTrust).
    bool HasValidSignature(string path);
}

/// A downloaded setup, held locked. Path is the file to check and run; Dispose releases the lock and deletes the file.
internal interface IPawnIOSetupFile : IDisposable
{
    string Path { get; }
}
