namespace NotchTasks.Core.Sensors;

/// The exit codes of the helper's PawnIO step (--install-pawnio, or the step a serve or install-logon runs first). 0 when the PawnIO
/// service exists afterwards; otherwise the code of the failure. The app logs each code with its message (ErrorLog).
public static class PawnIOExit
{
    /// The PawnIO service exists (it was there already, or the install made it).
    public const int Installed = 0;

    /// winget is on PATH and its install returned an error.
    public const int WingetFailed = 40;

    /// winget is not on PATH, and the PawnIO setup could not be downloaded from its release.
    public const int DownloadFailed = 41;

    /// The downloaded setup does not have a valid Authenticode signature. It is not run.
    public const int BadSignature = 42;

    /// The PawnIO setup ran and returned an error.
    public const int InstallerFailed = 43;

    /// The install returned no error, but the PawnIO service is not there.
    public const int ServiceMissing = 44;

    /// The readable line for an exit code.
    public static string Message(int code) => code switch
    {
        Installed => "The PawnIO driver is installed.",
        WingetFailed => "The PawnIO install with winget failed.",
        DownloadFailed => "PawnIO is not installed: winget is not on PATH and the PawnIO setup could not be downloaded.",
        BadSignature => "The downloaded PawnIO setup does not have a valid signature, so it was not run.",
        InstallerFailed => "The PawnIO setup failed.",
        ServiceMissing => "The PawnIO install finished, but the PawnIO service is not there.",
        _ => $"The PawnIO step failed with code {code}.",
    };
}
