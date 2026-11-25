using NotchTasks.Core;
using NotchTasks.Core.Agents;
using NotchTasks.Core.Limits;
using NotchTasks.Core.Modules;
using NotchTasks.Core.Sync;
using NotchTasks.Core.Updates;
using NotchTasks.Services;
using NotchTasks.Services.Devices;
using NotchTasks.Services.Media;
using NotchTasks.Services.Modules;

namespace NotchTasks;

/// The OS readers the app state runs on. Every field is optional: null takes this PC's reader, so production passes nothing.
/// A test passes a fake for each reader its state can reach, so no test reads the real Downloads folder, the adapters, the power
/// status, the disk, the registry, the microphone, Program Files, a real helper, the process table, sqlite, a local server or
/// the claude CLI.
/// DownloadsFolder is the folder the downloads module watches (null: the Downloads folder of this user).
/// Home is the user's profile folder (null: this user's); the agents and limits sources read the agent folders under it.
/// LmsPsJson runs `lms ps --json` for an exe path (null: LmsRunner.PsJson). Codex answers pill questions (null: the codex CLI).
/// Ci are the Build & CI feed's readers: the process table, GitHub, the ProgramData and runner folders and the clock (null: this PC's;
/// a test passes inert ones, so no test reads a runner folder or the network).
/// Media is the Windows media sessions reader (null: SmtcReader; a test passes a fake, so no test touches SMTC).
/// Devices is the device container reader (null: ContainerReader; a test passes a fake, so no test touches device enumeration).
/// Camera makes the mirror's camera session (null: this PC's CameraSession; a test passes a fake, so no test touches the camera).
/// Sync makes the phone sync's hub from the SyncDeps the bridge builds (null: no sync at all, so no hub, no listener and no stream;
/// the app passes new SyncHub, a test passes one built on fakes).
/// Update reads the update feed (null: the real one over the network; a test passes a client over a fake handler, so no test reaches
/// the feed. Nothing is read at construction: the service checks only when the app starts it, or when the Control Panel opens).
public sealed record AppStateOptions(
    string? DownloadsFolder = null,
    NetworkReaders? Network = null,
    Func<PowerFacts>? Power = null,
    HealthReaders? Health = null,
    SensorReaders? Sensors = null,
    SensorHelper? Helper = null,
    IProcessReader? Processes = null,
    ISqliteReader? Sqlite = null,
    IHttpPost? Http = null,
    IHttpGet? HttpGet = null,
    IClaudeCli? ClaudeCli = null,
    string? Home = null,
    Func<string, string?>? LmsPsJson = null,
    ICodexCli? Codex = null,
    CiReaders? Ci = null,
    IMediaReader? Media = null,
    IDeviceReader? Devices = null,
    Func<ICameraSession>? Camera = null,
    Func<SyncDeps, SyncHub>? Sync = null,
    UpdateClient? Update = null)
{
    /// This PC's readers: the options the app runs with (the same as passing none).
    public static AppStateOptions Production { get; } = new();
}
