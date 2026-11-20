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
/// shows. Start subscribes to the manager's session list and, for each session, to its media properties, playback info and
/// timeline events; a change of any raises Changed, and the source reads once after it. Every failure is logged once per kind and
/// reads as empty or as a no-op.
public sealed class SmtcReader : IMediaReader
{
    /// The largest artwork read, in bytes.
    private const long MaxArtworkBytes = 1_048_576;

    /// One session the reader is subscribed to, with the handlers to take off again.
    private sealed record Subscription(
        GlobalSystemMediaTransportControlsSession Session,
        TypedEventHandler<GlobalSystemMediaTransportControlsSession, MediaPropertiesChangedEventArgs> Properties,
        TypedEventHandler<GlobalSystemMediaTransportControlsSession, PlaybackInfoChangedEventArgs> Playback,
        TypedEventHandler<GlobalSystemMediaTransportControlsSession, TimelinePropertiesChangedEventArgs> Timeline);

    private readonly object _gate = new();
    private readonly HashSet<string> _logged = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Subscription> _subscriptions = new(StringComparer.Ordinal);
    private Action? _changed;
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private bool _running;

    public event Action Changed
    {
        add => _changed += value;
        remove => _changed -= value;
    }

    /// Tells the subscribers that a session changed (on whatever thread SMTC raised it).
    private void Raise() => _changed?.Invoke();

    /// Asks Windows for the session manager, then subscribes to the sessions it lists. A failure is logged and leaves the reader empty.
    public async Task Start()
    {
        _running = true;
        try
        {
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            if (!_running)
            {
                return;
            }
            _manager = manager;
            manager.SessionsChanged += OnSessionsChanged;
            manager.CurrentSessionChanged += OnCurrentSessionChanged;
            SyncSessions(manager);
            Raise();
        }
        catch (Exception e)
        {
            Log("start", e);
        }
    }

    /// Takes the handlers off the manager and every session. A Start still in progress does nothing when it lands.
    public void Stop()
    {
        _running = false;
        var manager = _manager;
        _manager = null;
        if (manager is not null)
        {
            manager.SessionsChanged -= OnSessionsChanged;
            manager.CurrentSessionChanged -= OnCurrentSessionChanged;
        }
        foreach (var subscription in _subscriptions.Values)
        {
            Unsubscribe(subscription);
        }
        _subscriptions.Clear();
    }

    /// Reads every listed session: its title and artist, its playback state and controls, and its timeline.
    public async Task<IReadOnlyList<MediaSessionFact>> Read()
    {
        var facts = new List<MediaSessionFact>();
        var manager = _manager;
        if (manager is null)
        {
            return facts;
        }
        try
        {
            var currentId = manager.GetCurrentSession()?.SourceAppUserModelId;
            foreach (var session in manager.GetSessions())
            {
                facts.Add(await ReadSession(session, session.SourceAppUserModelId == currentId));
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
        var session = Find(source);
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
        var session = Find(source);
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
        var session = Find(source);
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

    private void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args)
    {
        SyncSessions(sender);
        Raise();
    }

    private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args) =>
        Raise();

    /// Subscribes to the sessions that came and unsubscribes from the ones that went, by their app id.
    private void SyncSessions(GlobalSystemMediaTransportControlsSessionManager manager)
    {
        var listed = new Dictionary<string, GlobalSystemMediaTransportControlsSession>(StringComparer.Ordinal);
        foreach (var session in manager.GetSessions())
        {
            listed[session.SourceAppUserModelId] = session;
        }
        foreach (var id in _subscriptions.Keys.Where(id => !listed.ContainsKey(id)).ToList())
        {
            Unsubscribe(_subscriptions[id]);
            _subscriptions.Remove(id);
        }
        foreach (var (id, session) in listed)
        {
            if (!_subscriptions.ContainsKey(id))
            {
                _subscriptions[id] = Subscribe(session);
            }
        }
    }

    private Subscription Subscribe(GlobalSystemMediaTransportControlsSession session)
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
        return new Subscription(session, properties, playback, timeline);
    }

    private void Unsubscribe(Subscription subscription)
    {
        try
        {
            subscription.Session.MediaPropertiesChanged -= subscription.Properties;
            subscription.Session.PlaybackInfoChanged -= subscription.Playback;
            subscription.Session.TimelinePropertiesChanged -= subscription.Timeline;
        }
        catch (Exception e)
        {
            Log("unsubscribe", e);
        }
    }

    /// The session a source id names, from the subscriptions, or null when it is not listed.
    private GlobalSystemMediaTransportControlsSession? Find(string source) =>
        _subscriptions.TryGetValue(source, out var subscription) ? subscription.Session : null;

    /// Unix milliseconds of a time. A time before 1970 (SMTC's unset timeline) reads as now.
    private static long UnixMs(DateTimeOffset time) =>
        time.Year >= 1970 ? time.ToUnixTimeMilliseconds() : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// Logs a failure of one kind the first time it happens, and never again (a session that fails every second would flood the log).
    private void Log(string kind, Exception e)
    {
        lock (_gate)
        {
            if (!_logged.Add(kind))
            {
                return;
            }
        }
        ErrorLog.Write(e);
    }
}
