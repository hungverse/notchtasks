using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NotchTasks.Core.Media;
using NotchTasks.Core.Modules;
using NotchTasks.Pill;
using NotchTasks.Services.Media;

namespace NotchTasks.Services.Modules;

/// The most recently used values of a small keyed cache: a Put past the capacity drops the least recently used. Used for the
/// artwork images (the last eight keys), on the UI thread only.
public sealed class RecentCache<T> where T : class
{
    private readonly int _capacity;
    private readonly List<(string Key, T Value)> _items = new();  // least recently used first

    public RecentCache(int capacity)
    {
        _capacity = capacity;
    }

    public int Count => _items.Count;

    /// The value for a key (and the key becomes the most recently used), or null.
    public T? Get(string key)
    {
        var index = _items.FindIndex(i => i.Key == key);
        if (index < 0)
        {
            return null;
        }
        var item = _items[index];
        _items.RemoveAt(index);
        _items.Add(item);
        return item.Value;
    }

    /// Stores a value under a key, as the most recently used, and drops the least recently used past the capacity.
    public void Put(string key, T value)
    {
        _items.RemoveAll(i => i.Key == key);
        _items.Add((key, value));
        while (_items.Count > _capacity)
        {
            _items.RemoveAt(0);
        }
    }
}

/// The feed for the artwork bytes: a frozen BitmapImage, decoded at the card's and chip's size. Null when the bytes are not an image.
public static class MediaArt
{
    /// The decoded width, in pixels: the card's art is 46 DIP, and the chip's is no larger.
    public const int DecodeWidth = 92;

    public static BitmapImage? Decode(byte[] bytes)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = DecodeWidth;
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception e) when (e is NotSupportedException or IOException or ArgumentException)
        {
            return null;
        }
    }
}

/// The Now Playing feed (module "media"). It is event driven (P3d-R2): while it is started, the reader's Changed (any thread) starts a
/// 150 ms coalescing timer on the dispatcher, and the timer's tick makes one read. One read is in flight at a time, on the background,
/// and a request that arrives during a read makes one more read after it; a read that a newer request overtook is dropped. A read's
/// result goes to MediaModule.Update on the UI thread, and the pill is published when the shown session changed. The artwork and
/// the app icon of the shown session are fetched once each (the artwork is cached by its key, the last eight). Stop stops the
/// reader, unsubscribes and clears the module.
public sealed class MediaSource : IModuleSource
{
    private static readonly TimeSpan CoalesceFor = TimeSpan.FromMilliseconds(150);

    private readonly MediaModule _module;
    private readonly IMediaReader _reader;
    private readonly Action _publish;
    private readonly RecentCache<System.Windows.Media.ImageSource> _art;
    private readonly Func<long> _clock;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer = new() { Interval = CoalesceFor };
    private readonly HashSet<string> _artInFlight = new(StringComparer.Ordinal);
    private readonly HashSet<string> _artFailed = new(StringComparer.Ordinal);  // keys with no usable artwork: not fetched again
    private readonly HashSet<string> _iconInFlight = new(StringComparer.OrdinalIgnoreCase);
    private bool _started;
    private bool _busy;
    private bool _wanted;
    private bool _rebase;  // the next applied read publishes whether or not the shown session changed (the pill opened)
    private int _generation;  // every Start and Stop: a read that started under an older one is dropped when it lands

    /// module gets the reads; publish is called when the shown session changed (or its artwork or icon arrived); art holds the artwork
    /// images by key (AppState's cache). clock is the time in milliseconds (null: this PC's UTC clock).
    public MediaSource(MediaModule module, IMediaReader reader, Action publish, RecentCache<System.Windows.Media.ImageSource> art,
        Func<long>? clock = null)
    {
        _module = module;
        _reader = reader;
        _publish = publish;
        _art = art;
        _clock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _dispatcher = Dispatcher.CurrentDispatcher;
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            Request();
        };
    }

    public void Start()
    {
        if (_started)
        {
            return;
        }
        _started = true;
        _reader.Changed += OnReaderChanged;
        _ = StartReader();
    }

    /// Stops the timer and the reader, unsubscribes, and clears the module. A read in flight is dropped when it lands.
    public void Stop()
    {
        if (!_started)
        {
            return;
        }
        _started = false;
        _wanted = false;
        _generation++;
        _timer.Stop();
        _reader.Changed -= OnReaderChanged;
        _reader.Stop();
        _module.Clear();
    }

    /// Sends a media key to the shown session. The caller flips the card first for a toggle; SMTC's events then read the real state.
    public async Task Send(MediaCommand command)
    {
        if (_module.Current is not { } current)
        {
            return;
        }
        try
        {
            await _reader.Send(current.Source, command).ConfigureAwait(true);
        }
        catch (Exception e)
        {
            ErrorLog.Write(e);
        }
    }

    /// Moves the shown session to a position, in seconds.
    public async Task Seek(double seconds)
    {
        if (_module.Current is not { } current)
        {
            return;
        }
        try
        {
            await _reader.Seek(current.Source, seconds).ConfigureAwait(true);
        }
        catch (Exception e)
        {
            ErrorLog.Write(e);
        }
    }

    private async Task StartReader()
    {
        try
        {
            await _reader.Start().ConfigureAwait(true);
        }
        catch (Exception e)
        {
            ErrorLog.Write(e);
        }
        if (_started)
        {
            Request();
        }
    }

    /// Any thread: the coalescing timer starts on the dispatcher if it is not running. A burst of events is one read.
    private void OnReaderChanged() => _dispatcher.BeginInvoke(new Action(() =>
    {
        if (_started && !_timer.IsEnabled)
        {
            _timer.Start();
        }
    }));

    private void Request()
    {
        if (!_started)
        {
            return;
        }
        _wanted = true;
        if (!_busy)
        {
            _ = Pump();
        }
    }

    /// Reads while a read is wanted. A result that a newer request overtook is dropped, and the next read runs.
    private async Task Pump()
    {
        _busy = true;
        try
        {
            while (_started && _wanted)
            {
                _wanted = false;
                var generation = _generation;
                var sessions = await Task.Run(() => _reader.Read()).ConfigureAwait(true);
                if (!_started || generation != _generation)
                {
                    continue;  // stopped or restarted while the read ran: the result is stale
                }
                Apply(sessions);  // a request that came meanwhile is read next, in this loop
            }
        }
        catch (Exception e)
        {
            ErrorLog.Write(e);
        }
        finally
        {
            _busy = false;
        }
    }

    /// The UI thread: the module takes the read, the artwork and icon of the shown session are fetched once, and the pill is published
    /// when what it shows changed.
    private void Apply(IReadOnlyList<MediaSessionFact> sessions)
    {
        var changed = _module.Update(sessions, _clock());
        if (_module.Current is { } current)
        {
            FetchArtwork(current);
            FetchIcon(current);
        }
        if (changed || _rebase)
        {
            _rebase = false;  // the read that follows an open publishes even when nothing changed: the seek bar rebases on it
            _publish();
        }
    }

    /// The open pill follows the session's timeline (its position moves on its own); the closed pill does not. Opening also reads once,
    /// so the seek bar rebases from the current position. Does nothing while the module is stopped beyond the flag.
    public void FollowTimeline(bool open)
    {
        _reader.FollowTimeline(open);
        if (open)
        {
            _rebase = true;
            Request();
        }
    }

    private void FetchArtwork(NowPlaying current)
    {
        if (current.ArtworkKey is not { } key || _art.Get(key) is not null || _artFailed.Contains(key) || !_artInFlight.Add(key))
        {
            return;
        }
        _ = LoadArtwork(current.Source, key);
    }

    private async Task LoadArtwork(string source, string key)
    {
        try
        {
            var bytes = await _reader.Artwork(source).ConfigureAwait(true);
            if (bytes is not null && MediaArt.Decode(bytes) is { } image)
            {
                _art.Put(key, image);
                if (_started)
                {
                    _publish();
                }
            }
            else
            {
                _artFailed.Add(key);  // no thumbnail, too big, or not an image: the same key is not read again on every event
            }
        }
        catch (Exception e)
        {
            _artFailed.Add(key);
            ErrorLog.Write(e);
        }
        finally
        {
            _artInFlight.Remove(key);
        }
    }

    private void FetchIcon(NowPlaying current)
    {
        if (current.AppExe is not { } exe || ItemIcon.HasExeIcon(exe) || !_iconInFlight.Add(exe))
        {
            return;
        }
        _ = LoadIcon(exe);
    }

    private async Task LoadIcon(string exe)
    {
        try
        {
            await ItemIcon.LoadExeIconAsync(exe).ConfigureAwait(true);
            if (_started)
            {
                _publish();
            }
        }
        catch (Exception e)
        {
            ErrorLog.Write(e);
        }
        finally
        {
            _iconInFlight.Remove(exe);
        }
    }
}
