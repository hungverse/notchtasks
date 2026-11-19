namespace NotchTasks.Core.Agents;

/// CPU % per pid between two samples (100 = one full core). A pid whose StartTicks changed (a reused pid) starts at 0.
/// The first sample has no earlier one to compare with, so every pid reads 0 until the second.
public sealed class CpuMeter
{
    // 100 ns ticks per millisecond
    private const double TicksPerMs = 10_000.0;

    private Dictionary<int, (long Cpu, long Start)> prev = new();
    private Dictionary<int, double> pct = new();
    private long? at;

    /// Records the CPU times of s, taken at nowMs (milliseconds, any fixed clock).
    public void Sample(ProcSnapshot s, long nowMs)
    {
        var dt = at is null ? 0 : nowMs - at.Value;
        var next = new Dictionary<int, (long Cpu, long Start)>();
        var nextPct = new Dictionary<int, double>();
        foreach (var p in s.All)
        {
            next[p.Pid] = (p.CpuTicks, p.StartTicks);
            if (dt > 0 && prev.TryGetValue(p.Pid, out var before) && before.Start == p.StartTicks)
            {
                var deltaTicks = (double)(p.CpuTicks - before.Cpu);
                nextPct[p.Pid] = Math.Max(0, deltaTicks / (dt * TicksPerMs) * 100);
            }
        }
        prev = next;
        pct = nextPct;
        at = nowMs;
    }

    /// The CPU % of pid at the last sample (0 when unknown).
    public double Percent(int pid) => pct.TryGetValue(pid, out var value) ? value : 0;
}
