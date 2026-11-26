namespace NotchTasks.Core.Levels;

/// The output's volume as the menu bar slider shows it (one value for all channels), and whether it is muted.
public readonly record struct VolumeReading(double Value, bool Muted);

/// The default output device's volume and mute (the Mac's Levels.volume): the Core Audio endpoint in the app, a fake in the tests.
/// Changed is raised when the volume or the mute changes, on any thread: the source marshals it to the UI thread.
public interface IOutputEndpoint : IDisposable
{
    /// The endpoint's ID. The default device changing gives a new one.
    string Id { get; }

    /// The volume and mute now, or null when the device cannot report them.
    VolumeReading? Read();

    event Action? Changed;
}

/// The system's default output device (the Mac's default output): it binds the one that is default now, and says when the default
/// changes. The app's implementation registers an endpoint notification client, so nothing polls; Dispose gives the device back.
public interface IOutputDevices : IDisposable
{
    /// The default output, bound now; null when there is none. The caller disposes the endpoint it gets.
    IOutputEndpoint? OpenDefault();

    /// Raised when the default output changes, on any thread.
    event Action? DefaultChanged;
}

/// The laptop panel's brightness (the Mac's Levels.brightness): WMI in the app, a fake in the tests. A desktop has no panel, so Read
/// is null there and the bar has no brightness at all.
public interface IBrightness : IDisposable
{
    /// The panel's brightness from 0 to 1, or null when this PC has none.
    double? Read();

    /// Raised with the new brightness (0 to 1) on any thread, while Watch is on.
    event Action<double>? Changed;

    /// Starts the change feed (on) or stops it (off). Off releases it; Dispose stops it too.
    void Watch(bool on);
}
