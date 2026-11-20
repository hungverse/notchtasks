namespace NotchTasks.Core.Motion;

/// Frame interval statistics: the frame count, the 50th and 95th percentile and the largest interval (ms), and how many
/// intervals were over 1.5 frame periods (dropped frames).
public sealed record FrameSummary(int Frames, double P50, double P95, double Max, int Long);

public static class FrameStats
{
    /// Summarises the frame intervals (ms) against the display's frame period (ms). Percentiles are nearest-rank: the
    /// rank is p * n rounded to the nearest whole rank. An empty list gives all zeros.
    public static FrameSummary Summarize(IReadOnlyList<double> intervalsMs, double periodMs)
    {
        if (intervalsMs.Count == 0)
        {
            return new FrameSummary(0, 0, 0, 0, 0);
        }

        var sorted = intervalsMs.OrderBy(x => x).ToArray();
        var longLimit = 1.5 * periodMs;
        var longCount = 0;
        foreach (var interval in sorted)
        {
            if (interval > longLimit)
            {
                longCount++;
            }
        }

        return new FrameSummary(
            Frames: sorted.Length,
            P50: Percentile(sorted, 0.50),
            P95: Percentile(sorted, 0.95),
            Max: sorted[^1],
            Long: longCount);
    }

    /// The value at nearest rank for p in (0, 1] over a sorted, non-empty array.
    private static double Percentile(double[] sorted, double p)
    {
        var rank = (int)Math.Round(p * sorted.Length, MidpointRounding.AwayFromZero);
        rank = Math.Clamp(rank, 1, sorted.Length);
        return sorted[rank - 1];
    }
}
