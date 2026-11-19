using NotchTasks.Core;
using NotchTasks.Core.Agents;
using NotchTasks.Core.Limits;
using NotchTasks.Core.Modules;
using NotchTasks.Services;
using NotchTasks.Services.Modules;

namespace NotchTasks;

/// The OS readers the app state runs on. Every field is optional: null takes this PC's reader, so production passes nothing.
/// A test passes a fake for each reader its state can reach, so no test reads the real Downloads folder, the adapters, the power
/// status, the disk, the registry, the microphone, Program Files, a real helper, the process table, sqlite, a local server or
/// the claude CLI.
/// DownloadsFolder is the folder the downloads module watches (null: the Downloads folder of this user).
/// Home is the user's profile folder (null: this user's); the agents and limits sources read the agent folders under it.
/// LmsPsJson runs `lms ps --json` for an exe path (null: LmsRunner.PsJson). Codex answers pill questions (null: the codex CLI).
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
    ICodexCli? Codex = null)
{
    /// This PC's readers: the options the app runs with (the same as passing none).
    public static AppStateOptions Production { get; } = new();
}
