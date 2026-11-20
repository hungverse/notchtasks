namespace NotchTasks.Core.Media;

/// The Now Playing module (Mac: refreshNowPlaying). The caller reads SMTC into MediaSessionFact values and passes them in
/// with a manual clock; the module makes no OS calls and runs no timers.
/// Current is the session the card shows. Update picks it by the order: the current session if Playing, the first Playing
/// session, the last shown source if it is still listed and Paused, the current session if Paused with a title, else none.
public sealed class MediaModule
{
    private NowPlaying? _current;

    /// The session the card shows, or null when none is picked.
    public NowPlaying? Current => _current;

    /// Picks the session to show from the listed sessions (Other is ignored) and returns whether Current changed.
    /// Position is extrapolated to nowMs while Playing and clamped to the duration. A duration of zero or less is null.
    public bool Update(IReadOnlyList<MediaSessionFact> sessions, long nowMs)
    {
        var previous = _current;
        var lastShown = previous?.Source;

        var listed = sessions.Where(s => s.Status != MediaStatus.Other).ToList();
        var pick = PickSession(listed, lastShown);

        var fresh = pick is null ? null : ToNowPlaying(pick, nowMs);
        if (previous is not null && fresh is not null && SameShown(previous, fresh, nowMs))
        {
            return false;  // the card shows the same; the old reading keeps extrapolating
        }

        _current = fresh;
        return !Equals(previous, _current);
    }

    /// Forgets the shown session. The next Update has no last shown source.
    public void Clear() => _current = null;

    /// The optimistic toggle (Mac control): the card flips its play state at once. The position is first extrapolated to nowMs
    /// with the old state, so it does not jump. The next Update corrects the state to what SMTC reports.
    public void SetPlaying(bool playing, long nowMs)
    {
        if (_current is null)
        {
            return;
        }

        var position = Extrapolate(_current.Position, _current.Playing, _current.PositionAtMs, nowMs, _current.Duration);
        _current = _current with { Playing = playing, Position = position, PositionAtMs = nowMs };
    }

    private static MediaSessionFact? PickSession(IReadOnlyList<MediaSessionFact> listed, string? lastShown)
    {
        // 1. The current session, if it is playing.
        var currentPlaying = listed.FirstOrDefault(s => s.IsCurrent && s.Status == MediaStatus.Playing);
        if (currentPlaying is not null)
        {
            return currentPlaying;
        }

        // 2. The first playing session.
        var firstPlaying = listed.FirstOrDefault(s => s.Status == MediaStatus.Playing);
        if (firstPlaying is not null)
        {
            return firstPlaying;
        }

        // 3. The last shown source, if it is still listed and paused.
        if (lastShown is not null)
        {
            var lastPaused = listed.FirstOrDefault(s => s.Status == MediaStatus.Paused && s.Source == lastShown);
            if (lastPaused is not null)
            {
                return lastPaused;
            }
        }

        // 4. The current session, if it is paused with a non-empty title.
        var currentPaused = listed.FirstOrDefault(s => s.IsCurrent && s.Status == MediaStatus.Paused);
        if (currentPaused is not null && !string.IsNullOrWhiteSpace(currentPaused.Title))
        {
            return currentPaused;
        }

        // 5. Nothing to show.
        return null;
    }

    private static NowPlaying ToNowPlaying(MediaSessionFact s, long nowMs)
    {
        var playing = s.Status == MediaStatus.Playing;
        var duration = s.Duration is > 0 ? s.Duration : null;
        var position = Extrapolate(s.Position, playing, s.UpdatedAtMs, nowMs, duration);

        return new NowPlaying(
            Source: s.Source,
            AppName: MediaApps.Name(s.Source),
            AppExe: MediaApps.Exe(s.Source),
            Title: s.Title,
            Artist: s.Artist,
            Playing: playing,
            Position: position,
            Duration: duration,
            PositionAtMs: nowMs,
            CanSeek: s.CanSeek,
            CanNext: s.CanNext,
            CanPrevious: s.CanPrevious,
            CanToggle: s.CanToggle,
            ArtworkKey: s.ArtworkKey);
    }

    /// A report this close to the shown position is the same position (SMTC's position is coarse).
    private const double ReportDriftSeconds = 1.5;

    /// Whether a fresh reading shows what the card already shows at nowMs: the same session, metadata, play state, duration and
    /// controls, and a position within 1.5 s of the shown one extrapolated to nowMs.
    private static bool SameShown(NowPlaying shown, NowPlaying fresh, long nowMs)
    {
        if (shown.Source != fresh.Source || shown.Title != fresh.Title || shown.Artist != fresh.Artist
            || shown.Playing != fresh.Playing || shown.Duration != fresh.Duration || shown.ArtworkKey != fresh.ArtworkKey
            || shown.CanSeek != fresh.CanSeek || shown.CanNext != fresh.CanNext || shown.CanPrevious != fresh.CanPrevious
            || shown.CanToggle != fresh.CanToggle)
        {
            return false;
        }

        var expected = Extrapolate(shown.Position, shown.Playing, shown.PositionAtMs, nowMs, shown.Duration);
        return (expected, fresh.Position) switch
        {
            (null, null) => true,
            (null, _) or (_, null) => false,
            var (a, b) => Math.Abs(a.Value - b.Value) <= ReportDriftSeconds,
        };
    }

    /// The position at nowMs: the reading plus the time since it, while playing, clamped to [0, duration] when there is one.
    private static double? Extrapolate(double? position, bool playing, long atMs, long nowMs, double? duration)
    {
        if (position is null)
        {
            return null;
        }

        var value = position.Value;
        if (playing)
        {
            value += (nowMs - atMs) / 1000.0;
        }

        if (duration is { } d)
        {
            value = Math.Min(value, d);
        }

        return Math.Max(value, 0);
    }
}
