using System.Text.Json.Nodes;

namespace NotchTasks.Core.Agents;

/// The kinds of watch item (watchitems.js kind, the Mac WatchItem.Kind). Dev is the built-in Build & CI row.
public enum WatchKind { Process, App, Claude, Codex, Dev }

/// One row of the watch list (settings "items"). Fields this build does not know are kept in Extra, so a save from
/// this build does not drop them. Icon is the badge object of settings.js as compact JSON text, kept until the badge
/// model is ported to Core.
public sealed record WatchItem(
    string Id,
    string Name,
    WatchKind Kind,
    string Process = "",
    string? Args = null,
    string? BundleID = null,
    string? Exe = null,
    double? Threshold = null,
    bool ChildTasks = false,
    bool Build = false,
    bool Enabled = true,
    string? Cli = null,
    string? SymbolName = null,
    string? Icon = null)
{
    private static readonly IReadOnlyDictionary<string, JsonNode?> NoExtra = new Dictionary<string, JsonNode?>();

    /// Fields found in the stored JSON that this build does not model. Written back unchanged.
    public IReadOnlyDictionary<string, JsonNode?> Extra { get; init; } = NoExtra;
}
