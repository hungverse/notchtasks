namespace NotchTasks.Core.Limits;

/// One usage window of a provider: Used is 0-100 and Resets is when it starts over (Mac: LimitWindow).
public sealed record LimitWindow(string Label, double Used, DateTimeOffset Resets);

/// A provider's usage limits (Mac: ProviderLimits). Kind is "claude", "codex" or "process"; Windows are shortest first.
public sealed record ProviderLimits(string Kind, string Name, string? BundleID, IReadOnlyList<LimitWindow> Windows, DateTimeOffset Updated);
