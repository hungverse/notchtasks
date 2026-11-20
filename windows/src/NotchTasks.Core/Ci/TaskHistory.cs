namespace NotchTasks.Core.Ci;

/// Past run times per task, for "≈ 2 min left" and a progress estimate. Port of electron/src/core/devscan.js TaskHistory
/// (Mac: Sources/DevTasks.swift). Runs are in seconds. The usual duration of a task is the median of its last runs.
/// Persisted through the injected save (state.json key taskHistory: up to 5 runs per key, at most 300 keys).
public sealed class TaskHistory
{
    private const int KeepRuns = 5;
    private const int MaxKeys = 300;
    private const int KeysAfterTrim = 250;
    private const double MinSeconds = 5;   // shorter runs say nothing about the usual duration

    private readonly Action<IReadOnlyDictionary<string, IReadOnlyList<double>>> save;
    private Dictionary<string, List<double>> runs = new(StringComparer.Ordinal);

    public TaskHistory(IReadOnlyDictionary<string, IReadOnlyList<double>>? loaded, Action<IReadOnlyDictionary<string, IReadOnlyList<double>>> save)
    {
        this.save = save;
        if (loaded is null)
        {
            return;
        }
        foreach (var (key, list) in loaded)
        {
            runs[key] = list.Where(x => x >= 0).ToList();   // NaN fails the test too
        }
    }

    /// Every key with its recorded runs, for the state file and for tests.
    public IReadOnlyDictionary<string, IReadOnlyList<double>> Runs =>
        runs.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<double>)kv.Value.ToArray(), StringComparer.Ordinal);

    /// The usual duration in seconds: the median of the runs (one odd run does not skew it). Null with no runs.
    public double? Expected(string key)
    {
        if (!runs.TryGetValue(key, out var r) || r.Count == 0)
        {
            return null;
        }
        var sorted = r.OrderBy(x => x).ToList();
        return sorted[sorted.Count / 2];
    }

    /// Adds a finished run. Runs under 5 s are ignored. Keeps the last 5 per key; over 300 keys, keeps 250 at random.
    public void Record(string key, double seconds)
    {
        if (!(seconds >= MinSeconds))
        {
            return;
        }
        var list = runs.TryGetValue(key, out var existing) ? existing : new List<double>();
        list.Add(seconds);
        runs[key] = list.Count > KeepRuns ? list.GetRange(list.Count - KeepRuns, KeepRuns) : list;

        if (runs.Count > MaxKeys)
        {
            var keys = runs.Keys.ToArray();
            Random.Shared.Shuffle(keys);
            runs = keys.Take(KeysAfterTrim).ToDictionary(k => k, k => runs[k], StringComparer.Ordinal);
        }
        save(Runs);
    }

    /// The progress (0 to 0.99, or null without an estimate) and the label for a task that ran for elapsed seconds,
    /// against its usual duration. Label: "m:ss", then "· ≈ 2 min left" or "· longer than usual".
    public static (double? Progress, string Label) Estimate(double elapsed, double? expected)
    {
        var time = DevRules.Mmss(elapsed);
        if (expected is null || expected < 10)
        {
            return (null, time);
        }
        var left = expected.Value - elapsed;
        if (left <= 0)
        {
            return (0.99, time + L10n.L(" · longer than usual"));
        }
        var rest = left < 60
            ? $"{(long)Math.Truncate(left)}s"
            : L10n.L("{0} min", (long)Math.Truncate(left / 60) + 1);
        return (Math.Min(elapsed / expected.Value, 0.99), $"{time} · ≈ {rest}{L10n.L(" left")}");
    }
}
