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
