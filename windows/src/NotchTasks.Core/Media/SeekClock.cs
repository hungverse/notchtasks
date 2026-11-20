namespace NotchTasks.Core.Media;

/// The seek bar's clock (Mac: SeekBar). It predicts the position from a base and runs it while playing.
/// Report moves the base only on the first report, when the report is more than 1.5 s away from the prediction, or when the
/// duration changes. A play-state change freezes the clock at the predicted value (pause) or resumes it from there (play).
/// The clock takes its time from the caller (nowMs) and runs no timers.
public sealed class SeekClock
{
    /// A report this close to the prediction is treated as the same position (the SMTC position is coarse).
    private const double DriftSeconds = 1.5;

    private bool _started;
    private double _base;
    private long _baseAtMs;
    private bool _playing;
    private double _duration;

    /// Whether the last Report set the base from its reported position (the first report, a jump, or a duration change).
    /// A play-state change anchors the base at the predicted value, which is not a reported position, so it is false.
    public bool Rebased { get; private set; }

    /// Takes a report from SMTC (position in seconds, play state and duration) as of nowMs. See the class comment for when the base moves.
    public void Report(double position, bool playing, double duration, long nowMs)
    {
        Rebased = false;

        if (!_started)
        {
            Rebase(position, playing, duration, nowMs);
            Rebased = true;
            return;
        }

        if (duration != _duration)
        {
            Rebase(position, playing, duration, nowMs);
            Rebased = true;
            return;
        }

        var predicted = At(nowMs);
        if (playing != _playing)
        {
            // Freeze or resume from the predicted value; the reported position is not used for this change.
            _base = predicted;
            _baseAtMs = nowMs;
            _playing = playing;
            return;
        }

        if (Math.Abs(position - predicted) > DriftSeconds)
        {
            Rebase(position, playing, duration, nowMs);
            Rebased = true;
        }
    }

    /// The position at nowMs: the base, plus the time since it while playing, clamped to [0, duration].
    public double At(long nowMs)
    {
        if (!_started)
        {
            return 0;
        }

        var value = _base;
        if (_playing)
        {
            value += (nowMs - _baseAtMs) / 1000.0;
        }

        return Math.Clamp(value, 0, Math.Max(0, _duration));
    }

    private void Rebase(double position, bool playing, double duration, long nowMs)
    {
        _started = true;
        _base = position;
        _baseAtMs = nowMs;
        _playing = playing;
        _duration = duration;
    }
}
