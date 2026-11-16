using System.Windows;
using System.Windows.Media.Animation;
using NotchTasks.Core.Motion;

namespace NotchTasks.Pill;

/// The Mac's spring as a WPF easing. The animation lasts curve.Settle seconds; progress p maps to curve.Ease(p).
public sealed class SpringEase : EasingFunctionBase
{
    private readonly SpringCurve _curve;

    public SpringEase(SpringCurve curve)
    {
        _curve = curve;
        EasingMode = EasingMode.EaseIn;
    }

    protected override double EaseInCore(double normalizedTime) => _curve.Ease(normalizedTime);

    protected override Freezable CreateInstanceCore() => new SpringEase(_curve);
}
