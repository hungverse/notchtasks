namespace NotchTasksSensors;

/// The side effects of the sign-in install: the folders, the tasks, the stop event, the files and the marker. The real one is the machine
/// (SensorInstaller's nested RealInstallSystem); the tests pass a fake that records the steps.
internal interface IInstallSystem
{
    /// The shared folder the logon task runs from.
    string TargetFolder { get; }

    /// True when the folder existed before the install.
    bool FolderExisted(string target);

    /// Creates the folder with its inherited permissions, or throws when it has its own or writable rules.
    void EnsureFolder(string target);

    /// True when a scheduled task of this name exists.
    bool TaskExists(string taskName);

    /// Ends a running task (a "not running" answer is fine).
    void EndTask(string taskName);

    /// Asks this user's helper to stop and waits until it has released its files.
    void SignalStopAndWait(string sid);

    /// True when another user's helper runs from the folder.
    bool OtherHelpersRunning(string target, string sid);

    /// Copies the helper's files into the folder.
    void CopyFiles(string target);

    /// Registers this user's logon task (as SYSTEM) from its definition.
    void RegisterTask(string sid, string target, int? parent);

    /// Writes this user's marker: the install is recorded.
    void WriteMarker(string sid);

    /// Starts this user's task once, the way logon will.
    void RunTask(string sid);

    /// Undoes what a failed install created.
    void Rollback(string sid, string target, bool folderExisted, bool taskRegistered, bool markerWritten);
}
