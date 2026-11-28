using NotchTasks.Core.Sensors;

namespace NotchTasksSensors;

/// The PawnIO step: the driver the hardware library reads the CPU through is installed system-wide, once. It runs only in the elevated
/// helper that the user's click started (the same UAC prompt as Turn on), before the helper serves. The order: a present service is
/// enough; else winget installs it (when winget is on PATH); else the signed setup from the PawnIO release is checked and run.
internal sealed class PawnIOInstaller
{
    public const string ServiceName = "PawnIO";
    /// winget as a file name on PATH, and the command the step runs.
    public const string WingetOnPath = "winget.exe";
    public const string WingetCommand = "winget";
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
        if (_system.HasProgram(WingetOnPath))
        {
            failure = _system.Run(WingetCommand, WingetArguments) == 0 ? PawnIOExit.Installed : PawnIOExit.WingetFailed;
        }
        else
        {
            var setup = await _system.DownloadSetupAsync(ct);
            if (setup is null)
            {
                return PawnIOExit.DownloadFailed;
            }
            try
            {
                if (!_system.HasValidSignature(setup))
                {
                    return PawnIOExit.BadSignature;
                }
                failure = _system.Run(setup, SetupArguments) == 0 ? PawnIOExit.Installed : PawnIOExit.InstallerFailed;
            }
            finally
            {
                _system.Discard(setup);
            }
        }

        if (_system.ServiceExists(ServiceName))
        {
            return PawnIOExit.Installed;
        }
        return failure == PawnIOExit.Installed ? PawnIOExit.ServiceMissing : failure;
    }
}

/// The machine's side of the PawnIO step: the service, the PATH, the processes, the download and the signature. The real one is
/// WindowsPawnIOSystem; the tests pass a fake.
internal interface IPawnIOSystem
{
    /// True when the service with this name is registered.
    bool ServiceExists(string name);

    /// True when a program of this file name is on PATH.
    bool HasProgram(string fileName);

    /// Runs a program with the arguments, waits for it, and returns its exit code. A program that does not start returns -1.
    int Run(string fileName, string arguments);

    /// Downloads the latest PawnIO setup into a new temporary file and returns its path. Null when the download fails.
    Task<string?> DownloadSetupAsync(CancellationToken ct);

    /// True when the file has a valid Authenticode signature (WinVerifyTrust).
    bool HasValidSignature(string path);

    /// Deletes a file this step downloaded. Errors are ignored.
    void Discard(string path);
}
