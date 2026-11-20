namespace NotchTasks.Core.Media;

/// A media key press the Now Playing card sends. A seek is a separate call (SeekClock and the card's seek bar).
public enum MediaCommand
{
    Toggle,
    Next,
    Previous,
}

/// The playback state of one SMTC session. Other covers Stopped, Changing and any state the card does not show.
public enum MediaStatus
{
    Playing,
    Paused,
    Other,
}

/// One session as the SMTC reader sees it. Source is the SourceAppUserModelId. Position is in seconds, as of UpdatedAtMs.
/// IsCurrent is the session SMTC marks as the current one (GetCurrentSession).
public sealed record MediaSessionFact(
    string Source,
    string? Title,
    string? Artist,
    MediaStatus Status,
    double? Position,
    double? Duration,
    long UpdatedAtMs,
    bool CanSeek,
    bool CanNext,
    bool CanPrevious,
    bool CanToggle,
    string? ArtworkKey,
    bool IsCurrent);

/// The session the Now Playing card shows. Position is the value at PositionAtMs, and the card extrapolates it while Playing.
/// AppExe is null for packaged apps with no known exe.
public sealed record NowPlaying(
    string Source,
    string AppName,
    string? AppExe,
    string? Title,
    string? Artist,
    bool Playing,
    double? Position,
    double? Duration,
    long PositionAtMs,
    bool CanSeek,
    bool CanNext,
    bool CanPrevious,
    bool CanToggle,
    string? ArtworkKey);
