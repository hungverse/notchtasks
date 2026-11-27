namespace NotchTasks.Core.Motion;

/// The Mac's spring (SwiftUI spring(response:dampingFraction:)) as a normalised step response x(t), x(0)=0, x(inf)=1.
/// EaseInOut gives the Mac's easeInOut(duration: Response) instead, used by "reduced" and when animations are off.
public sealed record SpringCurve(double Response, double Damping)
{
    private const double SettleTolerance = 0.001;
    private const double ScanStep = 0.001;
    private const double ScanLimit = 10;

    private double? _settle;
    private double? _peak;

    /// True for the easeInOut curve (Response is its duration).
    public bool EaseInOut { get; init; }

    /// Seconds until the curve stays within 0.1 % of 1. For the ease-in-out curve this is the duration. Scanned once, then cached.
    public double Settle => _settle ??= EaseInOut ? Response : ScanSettle();

    /// The largest value the curve reaches before it settles: 1 for a curve that does not overshoot, above 1 for a spring. Scanned once.
    public double Peak => _peak ??= ScanPeak();

    /// The curve value at time t seconds: a spring overshoots above 1; the ease-in-out curve stays in [0, 1].
    /// Returns exactly 1 from Settle on.
    public double At(double t)
    {
        if (t <= 0)
        {
            return 0;
        }
        if (t >= Settle)
        {
            return 1;
        }
        return Raw(t);
    }

    /// Eases progress p in [0, 1] (p = 1 is the end of the animation): x(p * Settle), exactly 1 from p = 1 on.
    public double Ease(double p)
    {
        if (p <= 0)
        {
            return 0;
        }
        return p >= 1 ? 1 : At(p * Settle);
    }

    /// The formula itself, without the clamp at Settle (so Settle can be scanned without recursing into it).
    private double Raw(double t)
    {
        if (EaseInOut)
        {
            var u = Math.Clamp(t / Response, 0, 1);
            return u < 0.5 ? 4 * u * u * u : 1 - Math.Pow(-2 * u + 2, 3) / 2;
        }

        var omega = 2 * Math.PI / Response;
        if (Damping < 1)
        {
            var omegaD = omega * Math.Sqrt(1 - Damping * Damping);
            var decay = Math.Exp(-Damping * omega * t);
            return 1 - decay * (Math.Cos(omegaD * t) + Damping * omega / omegaD * Math.Sin(omegaD * t));
        }
        return 1 - Math.Exp(-omega * t) * (1 + omega * t);
    }

    private double ScanPeak()
    {
        var peak = 1.0;
        var end = Settle;
        for (var t = 0.0; t <= end; t += ScanStep)
        {
            peak = Math.Max(peak, Raw(t));
        }
        return peak;
    }

    private double ScanSettle()
    {
        var last = 0.0;
        for (var t = 0.0; t <= ScanLimit; t += ScanStep)
        {
            if (Math.Abs(Raw(t) - 1) >= SettleTolerance)
            {
                last = t;
            }
        }
        return last + ScanStep;
    }

    /// The Mac's curves for a motion setting. "reduced", or animations switched off in Windows, give a 0.15 s ease-in-out.
    public static SpringCurve For(string motion, bool opening, bool animationsOn)
    {
        if (!animationsOn || motion == "reduced")
        {
            return new SpringCurve(0.15, 1) { EaseInOut = true };
        }

        // Opening overshoots a little (liquid); closing never does, so the shape cannot dip inside the top edge.
        var (response, damping) = (motion, opening) switch
        {
            ("smooth", true) => (0.5, 0.78),
            ("snappy", true) => (0.3, 0.86),
            ("bouncy", true) => (0.55, 0.64),
            ("smooth", false) => (0.45, 1.0),
            ("snappy", false) => (0.3, 1.0),
            (_, false) => (0.5, 1.0),
            _ => (0.55, 0.64),
        };
        return new SpringCurve(response, damping);
    }
}
