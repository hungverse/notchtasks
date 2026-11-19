using System.Globalization;
using NotchTasks.Core.Modules;
using NotchTasks.Core.Notch;

namespace NotchTasks.Core.Limits;

/// What the limits module reads on one update: the switches, the three providers' limits (null when not read), and the
/// warning level (50-100, the Mac's limitWarn).
public sealed record LimitInputs(
    bool On,
    bool ClaudeEnabled,
    bool CodexEnabled,
    bool AgyEnabled,
    ProviderLimits? Claude,
    ProviderLimits? Codex,
    ProviderLimits? Antigravity,
    double LimitWarn);

/// The limits module (Mac: Model.refreshLimits). It keeps the limits of the providers that are on, and announces each
/// window once per step (80, 85 … 99, 100) and reset hour. Pure logic: the time is passed in as now.
public sealed class LimitsModule
{
    private const string RedTint = "#FF453A";
    private const string OrangeTint = "#FF9F0A";
    private const int RedFromStep = 95;

    private readonly IAnnouncer _announce;
    private readonly HashSet<string> _warned = new(StringComparer.Ordinal);
    private IReadOnlyList<ProviderLimits> _limits = Array.Empty<ProviderLimits>();

    /// The toasts for the warnings go to the announcer.
    public LimitsModule(IAnnouncer announce)
    {
        _announce = announce;
    }

    /// The limits shown: Claude, Codex, Antigravity, in that order, each only when its provider is on. None while the module is off.
    public IReadOnlyList<ProviderLimits> Limits => _limits;

    /// Reads the new limits and warns about the windows at or above the warning level. Returns true when Limits changed.
    public bool Update(LimitInputs input, DateTimeOffset now)
    {
        var next = new List<ProviderLimits>();
        if (input.On)
        {
            if (input.ClaudeEnabled && input.Claude is { } claude) next.Add(claude);
            if (input.CodexEnabled && input.Codex is { } codex) next.Add(codex);
            if (input.AgyEnabled && input.Antigravity is { } agy) next.Add(agy);
        }

        var changed = !Same(next, _limits);
        _limits = next;
        Warn(next, input.LimitWarn, now);
        return changed;
    }

    /// Every window counts (a weekly limit near its end matters as much as the 5 h one). Each step is warned once per window and
    /// reset hour: Antigravity reports the same reset a few seconds apart from one asking to the next.
    private void Warn(IReadOnlyList<ProviderLimits> limits, double warn, DateTimeOffset now)
    {
        foreach (var p in limits)
        {
            foreach (var w in p.Windows)
            {
                var step = Limit.Step(w.Used);
                if (step < warn) continue;
                var key = $"{p.Name}-{w.Label}-{w.Resets.ToUnixTimeSeconds() / 3600}-{step}";
                if (!_warned.Add(key)) continue;

                var done = step == 100;
                var at = Limit.ResetText(w, now, CultureInfo.CurrentCulture, TimeZoneInfo.Local);
                var left = (int)(Math.Max(0, (w.Resets - now).TotalSeconds) / 60);
                var wait = left >= 60 ? L10n.L("{0}h {1}m", left / 60, left % 60) : L10n.L("{0} min", left);
                _announce.Announce(new Toast(
                    Glyph: done ? "hourglass" : "gauge",
                    Tint: step >= RedFromStep ? RedTint : OrangeTint,
                    Title: done
                        ? L10n.L("{0} reached its {1} limit", p.Name, w.Label)
                        : L10n.L("{0} used {1}% of its {2} limit", p.Name, (int)w.Used, w.Label),
                    Subtitle: done
                        ? L10n.L("Wait until {0} ({1} left)", at, wait)
                        : L10n.L("Resets at {0}", at),
                    Event: "limitWarn",
                    Sticky: true,
                    Module: "limits"));
            }
        }
    }

    private static bool Same(IReadOnlyList<ProviderLimits> a, IReadOnlyList<ProviderLimits> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }
        for (var i = 0; i < a.Count; i++)
        {
            if (a[i].Kind != b[i].Kind || a[i].Name != b[i].Name || a[i].Updated != b[i].Updated
                || !a[i].Windows.SequenceEqual(b[i].Windows))
            {
                return false;
            }
        }
        return true;
    }
}
