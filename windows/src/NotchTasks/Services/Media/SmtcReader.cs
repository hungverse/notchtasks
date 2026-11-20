using System.IO;
using NotchTasks.Core.Media;
using Windows.Foundation;
using Windows.Media.Control;

namespace NotchTasks.Services.Media;

/// What the media source reads and sends (AppStateOptions.Media). Changed may be raised on any thread. Every call is safe to make
/// after Stop: a reader that has stopped answers with nothing. A test passes a fake; production passes SmtcReader.
public interface IMediaReader
{
    /// Raised when a session or its state changed (SMTC's events, or a session that came or went).
    event Action Changed;

    /// Starts listening for sessions. Changed is raised once the sessions are known.
    Task Start();

    /// Stops listening and unsubscribes from every session.
    void Stop();

    /// The sessions the OS lists now, with their state, position and artwork key.
    Task<IReadOnlyList<MediaSessionFact>> Read();

    /// Sends a media key (play or pause, next, previous) to a session.
    Task Send(string source, MediaCommand command);

    /// Moves a session to a position, in seconds.
    Task Seek(string source, double seconds);

    /// The artwork bytes of a session's thumbnail, or null when it has none (or it is over 1 MB).
    Task<byte[]?> Artwork(string source);
}

/// The Windows media session reader (P3d-R2): GlobalSystemMediaTransportControlsSessionManager, the list the Windows media flyout
/// shows. Start asks for the manager and subscribes to the session list and, for each session, to its media properties, playback
/// info and timeline events (the bookkeeping is MediaSessionBook). A change of any raises Changed, and the source reads once after it.
/// A session that fails to read is skipped for that read; every failure is logged once per kind.
public sealed class SmtcReader : IMediaReader
{
    /// The largest artwork read, in bytes.
    private const long MaxArtworkBytes = 1_048_576;

    private readonly HashSet<string> _logged = new(StringComparer.Ordinal);
    private readonly object _logGate = new();
    private readonly MediaSessionBook<GlobalSystemMediaTransportControlsSessionManager, GlobalSystemMediaTransportControlsSession> _book;
    private Action? _changed;

    public SmtcReader()
    {
        _book = new(
            list: manager => manager.GetSessions().ToList(),
            idOf: session => session.SourceAppUserModelId,
            subscribe: SubscribeSession,
            watch: WatchManager);
    }

    public event Action Changed
    {
        add => _changed += value;
        remove => _changed -= value;
    }

    /// Tells the subscribers that a session changed (on whatever thread SMTC raised it).
    private void Raise() => _changed?.Invoke();

    /// Asks Windows for the session manager, then subscribes to the sessions it lists. A Start overtaken by a Stop, or by a later
    /// Start, while the request is pending does nothing when it lands.
    public async Task Start()
    {
        var generation = _book.BeginStart();
        try
        {
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            if (!_book.Attach(generation, manager))
            {
                return;
            }
            _book.Sync(manager);
            Raise();
        }
        catch (Exception e)
        {
            Log("start", e);
        }
    }

    /// Takes the handlers off the manager and every session.
    public void Stop() => _book.Stop();

    /// Reads every listed session: its title and artist, its playback state and controls, and its timeline. A session that fails is
    /// left out of this read; the others still come.
    public async Task<IReadOnlyList<MediaSessionFact>> Read()
    {
        var facts = new List<MediaSessionFact>();
        var manager = _book.Manager;
        if (manager is null)
        {
            return facts;
        }
        try
        {
            var currentId = manager.GetCurrentSession()?.SourceAppUserModelId;
            foreach (var session in manager.GetSessions())
            {
                try
                {
                    facts.Add(await ReadSession(session, session.SourceAppUserModelId == currentId));
                }
                catch (Exception e)
                {
                    Log("session", e);
                }
            }
        }
        catch (Exception e)
        {
            Log("read", e);
        }
        return facts;
    }

    /// Sends a media key to a session. Windows may refuse (the session cannot do it); that is not an error here.
    public async Task Send(string source, MediaCommand command)
    {
        var session = _book.Find(source);
        if (session is null)
        {
            return;
        }
        try
        {
            _ = command switch
            {
                MediaCommand.Toggle => await session.TryTogglePlayPauseAsync(),
                MediaCommand.Next => await session.TrySkipNextAsync(),
                MediaCommand.Previous => await session.TrySkipPreviousAsync(),
                _ => false,
            };
        }
        catch (Exception e)
        {
            Log("send", e);
        }
    }

    /// Moves a session to a position. The position is in 100 ns ticks, as SMTC takes it.
    public async Task Seek(string source, double seconds)
    {
        var session = _book.Find(source);
        if (session is null)
        {
            return;
        }
        try
        {
            await session.TryChangePlaybackPositionAsync((long)Math.Round(seconds * 10_000_000));
        }
        catch (Exception e)
        {
            Log("seek", e);
        }
    }

    /// The thumbnail's bytes, read up to 1 MB. Over that, or with no thumbnail, there are no bytes.
    public async Task<byte[]?> Artwork(string source)
    {
        var session = _book.Find(source);
        if (session is null)
        {
            return null;
        }
        try
        {
            var thumbnail = (await session.TryGetMediaPropertiesAsync())?.Thumbnail;
            if (thumbnail is null)
            {
                return null;
            }
            using var stream = (await thumbnail.OpenReadAsync()).AsStreamForRead();
            using var bytes = new MemoryStream();
            var buffer = new byte[81_920];
            while (true)
            {
                var read = await stream.ReadAsync(buffer);
                if (read == 0)
                {
                    break;
                }
                if (bytes.Length + read > MaxArtworkBytes)
                {
                    return null;
                }
                bytes.Write(buffer, 0, read);
            }
            return bytes.ToArray();
        }
        catch (Exception e)
        {
            Log("artwork", e);
            return null;
        }
    }

    private async Task<MediaSessionFact> ReadSession(GlobalSystemMediaTransportControlsSession session, bool isCurrent)
    {
        var source = session.SourceAppUserModelId;
        var properties = await TryProperties(session);
        var info = session.GetPlaybackInfo();
        var timeline = session.GetTimelineProperties();
        var controls = info.Controls;

        var status = info.PlaybackStatus switch
        {
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => MediaStatus.Playing,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => MediaStatus.Paused,
            _ => MediaStatus.Other,
        };
        var duration = (timeline.EndTime - timeline.StartTime).TotalSeconds;
        var position = duration > 0 ? timeline.Position.TotalSeconds : (double?)null;
        var artworkKey = properties?.Thumbnail is not null
            ? $"{source}|{properties.Title}|{properties.Artist}"
            : null;

        return new MediaSessionFact(
            source,
            properties?.Title,
            properties?.Artist,
            status,
            position,
            duration > 0 ? duration : null,
            UnixMs(timeline.LastUpdatedTime),
            CanSeek: controls.IsPlaybackPositionEnabled,
            CanNext: controls.IsNextEnabled,
            CanPrevious: controls.IsPreviousEnabled,
            CanToggle: controls.IsPlayPauseToggleEnabled || controls.IsPlayEnabled || controls.IsPauseEnabled,
            ArtworkKey: artworkKey,
            IsCurrent: isCurrent);
    }

    private async Task<GlobalSystemMediaTransportControlsSessionMediaProperties?> TryProperties(
        GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            return await session.TryGetMediaPropertiesAsync();
        }
        catch (Exception e)
        {
            Log("properties", e);
            return null;
        }
    }

    /// Hooks the manager's session-list events: a list change brings the subscriptions in line and raises Changed; a current-session
    /// change raises Changed. The handle takes both off.
    private IDisposable WatchManager(GlobalSystemMediaTransportControlsSessionManager manager)
    {
        TypedEventHandler<GlobalSystemMediaTransportControlsSessionManager, SessionsChangedEventArgs> sessions = (sender, _) =>
        {
            if (_book.Sync(sender))
            {
                Raise();
            }
        };
        TypedEventHandler<GlobalSystemMediaTransportControlsSessionManager, CurrentSessionChangedEventArgs> current = (_, _) => Raise();
        manager.SessionsChanged += sessions;
        manager.CurrentSessionChanged += current;
        return new ActionDisposable(() =>
        {
            manager.SessionsChanged -= sessions;
            manager.CurrentSessionChanged -= current;
        });
    }

    /// Hooks a session's media properties, playback info and timeline events, each raising Changed. The handle takes them off.
    private IDisposable SubscribeSession(GlobalSystemMediaTransportControlsSession session)
    {
        var properties = new TypedEventHandler<GlobalSystemMediaTransportControlsSession, MediaPropertiesChangedEventArgs>(
            (_, _) => Raise());
        var playback = new TypedEventHandler<GlobalSystemMediaTransportControlsSession, PlaybackInfoChangedEventArgs>(
            (_, _) => Raise());
        var timeline = new TypedEventHandler<GlobalSystemMediaTransportControlsSession, TimelinePropertiesChangedEventArgs>(
            (_, _) => Raise());
        session.MediaPropertiesChanged += properties;
        session.PlaybackInfoChanged += playback;
        session.TimelinePropertiesChanged += timeline;
        return new ActionDisposable(() =>
        {
            try
            {
                session.MediaPropertiesChanged -= properties;
                session.PlaybackInfoChanged -= playback;
                session.TimelinePropertiesChanged -= timeline;
            }
            catch (Exception e)
            {
                Log("unsubscribe", e);
            }
        });
    }

    /// Unix milliseconds of a time. A time before 1970 (SMTC's unset timeline) reads as now.
    private static long UnixMs(DateTimeOffset time) =>
        time.Year >= 1970 ? time.ToUnixTimeMilliseconds() : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// Logs a failure of one kind the first time it happens, and never again (a session that fails every second would flood the log).
    private void Log(string kind, Exception e)
    {
        lock (_logGate)
        {
            if (!_logged.Add(kind))
            {
                return;
            }
        }
        ErrorLog.Write(e);
    }
}
