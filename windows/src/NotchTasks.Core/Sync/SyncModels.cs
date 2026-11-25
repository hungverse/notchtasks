using System.Text.Json.Nodes;
using NotchTasks.Core.Asks;

namespace NotchTasks.Core.Sync;

/// The group the Mac or this PC shares with the phones: g names it on the server, k seals its boxes, uid and refresh are the server session.
public sealed record SyncGroup(string G, string K, string Uid, string Refresh);

/// The pairing JSON the QR code carries: v is the format (1), g and k as in SyncGroup, n the name of the computer that shows it.
public sealed record PairingCode(int V, string G, string K, string N);

/// A run or a build as the phone shows it. Agent is null when the run has no agent; Icon is a settings badge (JSON).
public sealed record SyncRun(string Id, string? Agent, string Title, string Detail, DateTimeOffset Since, JsonObject? Icon);

/// An ask as the phone shows it, with the Mac's icon for its agent.
public sealed record SyncAsk(AgentAsk Ask, JsonObject? Icon);

/// A usage limit: Used is a percentage, Resets when it starts again.
public sealed record SyncLimit(string Provider, string Label, int Used, DateTimeOffset Resets);

/// The computer's health: every reading is null when this PC cannot read it.
public sealed record SyncHealth(int? Battery, bool Charging, int Thermal, int? Temp, int? Fan, int? Cpu, int? Memory);

/// A notable event (CI failed, task done) for the phone's list.
public sealed record SyncEvent(string Id, string Kind, string Title, string Detail, DateTimeOffset At);

/// The job on screen: State is the phone's word for it, Ended is set once it has finished.
public sealed record SyncJob(string Title, string Detail, DateTimeOffset Since, string State, DateTimeOffset? Ended, JsonObject? Icon);

/// One agent in a crew; Agent is null when unknown.
public sealed record SyncMember(string? Agent, JsonObject? Icon);

/// A crew of agents working together: Count is how many, Members lists each one.
public sealed record SyncCrew(
    int Count,
    string Title,
    string Detail,
    DateTimeOffset Since,
    string? Agent,
    JsonObject? Icon,
    IReadOnlyList<SyncMember> Members);

/// The whole state the Mac sends the phones (sealed and sent every second when it changes).
public sealed record SyncSnapshot(
    string Mac,
    IReadOnlyList<SyncRun> Runs,
    IReadOnlyList<SyncRun> Builds,
    IReadOnlyList<SyncAsk> Asks,
    IReadOnlyList<SyncLimit> Limits,
    SyncHealth? Health,
    IReadOnlyList<SyncEvent> Events,
    SyncJob? Job,
    JsonObject? Notch,
    SyncCrew? Crew,
    DateTimeOffset At);

/// An answer the phone sends back: the ask it answers, the reply, and what else it carries (its name, the notch, a timer, a refresh).
public sealed record PhoneAnswer(
    string Ask,
    AskReply Reply,
    DateTimeOffset At,
    string? Phone,
    JsonObject? Notch,
    int? Timer,
    bool Refresh);

/// A usage limit as main gives it to the builder: Used is a percentage (fractions are cut), Resets is exact to the millisecond.
public sealed record SyncLimitInput(string Provider, string Label, double Used, DateTimeOffset Resets);

/// The computer's readings as main gives them: Cpu and Memory are fractions from 0 to 1, Temp in degrees.
public sealed record SyncHealthInput(int? Battery, bool Charging, double? Temp, double? Cpu, double? Memory);

/// What the snapshot is built from: the runs and builds on screen, the asks, the limits, the health and the notch. Run Since values are ignored (the builder keeps its own).
public sealed record SyncInput(
    IReadOnlyList<SyncRun> Runs,
    IReadOnlyList<SyncRun> Builds,
    IReadOnlyList<SyncAsk> Asks,
    IReadOnlyList<SyncLimitInput> Limits,
    SyncHealthInput? Health,
    JsonObject? Notch);

/// What the push function is told about the agents at work: Count, the oldest run's At, and Stamp (when the signal was last sent), all in ms since 1970.
public sealed record CrewSig(int Count, double At, double Stamp);

/// The Pro note the group last had (why: moved, refunded or expired) and when it was made (ms since 1970).
public sealed record ProNoteInfo(string Why, double At);

/// What the Pairing pane shows: whether the group exists, its phones by uid, the LAN phone count, the last problem, the Pro state
/// (ProUntil in ms, ProHolders the uids with a grant), the Pro note, and the two switches.
public sealed record SyncState(
    bool Paired,
    IReadOnlyDictionary<string, string> Phones,
    int LanPhones,
    string? Problem,
    bool Pro,
    double? ProUntil,
    IReadOnlyList<string> ProHolders,
    ProNoteInfo? ProNote,
    bool Lan,
    bool OnlyAway);

/// The details of a problem report that come from this PC (the app's version, build, Windows version and language).
public sealed record ReportInfo(string Version, string Build, string Os, string Lang);
