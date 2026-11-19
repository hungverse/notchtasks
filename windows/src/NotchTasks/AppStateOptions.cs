using NotchTasks.Core.Modules;
using NotchTasks.Services;
using NotchTasks.Services.Modules;

namespace NotchTasks;

/// The OS readers the app state runs on. Every field is optional: null takes this PC's reader, so production passes nothing.
/// A test passes a fake for each reader its state can reach, so no test reads the real Downloads folder, the adapters, the power
/// status, the disk, the registry, the microphone, Program Files or a real helper.
/// DownloadsFolder is the folder the downloads module watches (null: the Downloads folder of this user).
public sealed record AppStateOptions(
    string? DownloadsFolder = null,
    NetworkReaders? Network = null,
    Func<PowerFacts>? Power = null,
    HealthReaders? Health = null,
    SensorReaders? Sensors = null,
    SensorHelper? Helper = null)
{
    /// This PC's readers: the options the app runs with (the same as passing none).
    public static AppStateOptions Production { get; } = new();
}
