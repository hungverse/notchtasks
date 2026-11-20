namespace NotchTasks.Core.Ci;

/// A failed GitLab job, shown as an alert until it is dismissed (Mac: JobAlert in Sources/Runner.swift).
/// Url is the job page, which is its terminal log.
public sealed record JobAlert(string Id, string Name, string Project, string Reason, string? Url);
