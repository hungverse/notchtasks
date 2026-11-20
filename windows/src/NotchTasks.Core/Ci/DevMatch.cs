namespace NotchTasks.Core.Ci;

/// What a process is doing, as read from its name and arguments: the tool, the action, the symbol for the row,
/// the subject (project or file name, when known) and whether only its busy state counts (a render that
/// shows once it works hard).
public sealed record DevMatch(string Tool, string Action, string Symbol, string? Subject = null, bool BusyOnly = false);
