using System.Text.Json.Nodes;

namespace NotchTasks.Core.Analytics;

/// Anonymous usage statistics for Google Analytics, by its Measurement Protocol (the events of the Mac's Track.swift).
/// Names are lowercase_snake; params are enums or counts, never free text. A request holds up to 25 events and goes out at
/// most once a minute. A request that fails is lost: these are statistics, not data. Nothing is sent without an API secret,
/// with the opt-out on, or under CI_SELFTEST.
public sealed class Track
{
    /// The web stream's measurement ID (Google Analytics, Data streams).
    public const string MeasurementId = "G-H9RD7VRTDS";
    public const int BatchSize = 25;
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    /// Events waiting to go out. Beyond this an event is dropped, so a long offline spell cannot grow the queue without limit.
    private const int MaxQueued = 500;
    private const string Endpoint = "https://www.google-analytics.com/mp/collect";

    private readonly string _secret;
    private readonly string _appVersion;
    private readonly long _session;
    private readonly Func<bool> _enabled;
    private readonly Func<string, string?> _read;
    private readonly Action<string, string> _write;
    private readonly Func<string, string, Task> _post;
    private readonly Action<TimeSpan, Action> _later;
    private readonly object _gate = new();
    private readonly List<JsonObject> _queue = [];
    private string? _clientId;
    private bool _scheduled;

    /// `enabled` is read at each call, so the opt-out is live. `read` and `write` keep the client id and the once-flags.
    /// `post` gets the URL and the JSON body; a throw loses that request. `later` runs an action once, after the delay.
    public Track(string secret, string appVersion, DateTimeOffset now, Func<bool> enabled, Func<string, string?> read,
        Action<string, string> write, Func<string, string, Task> post, Action<TimeSpan, Action> later)
    {
        _secret = secret;
        _appVersion = appVersion;
        _session = now.ToUnixTimeSeconds();
        _enabled = enabled;
        _read = read;
        _write = write;
        _post = post;
        _later = later;
    }

    private bool Sending => _secret.Length > 0 && _enabled() && Environment.GetEnvironmentVariable("CI_SELFTEST") is null;

    /// Queues an event. False when it is not queued: opted out, no secret, a self-test, or a full queue.
    public bool Event(string name, IReadOnlyDictionary<string, object>? parameters = null)
    {
        lock (_gate)
        {
            if (!Sending)
            {
                _queue.Clear();
                return false;
            }
            if (_queue.Count >= MaxQueued)
            {
                return false;
            }
            var values = new JsonObject();
            foreach (var (key, value) in parameters ?? new Dictionary<string, object>())
            {
                values[key] = value is int number ? JsonValue.Create(number) : JsonValue.Create(value.ToString());
            }
            values["platform"] = JsonValue.Create("windows");
            values["app_version"] = JsonValue.Create(_appVersion);
            values["session_id"] = JsonValue.Create(_session);
            values["engagement_time_msec"] = JsonValue.Create(1);
            _queue.Add(new JsonObject { ["name"] = JsonValue.Create(name), ["params"] = values });
            Schedule();
            return true;
        }
    }

    /// Queues an event the first time only. The once-flag is written only when the event is queued.
    public bool Once(string name, IReadOnlyDictionary<string, object>? parameters = null)
    {
        var key = "analytics.once." + name;
        lock (_gate)
        {
            if (_read(key) is not null || !Event(name, parameters))
            {
                return false;
            }
            _write(key, "1");
            return true;
        }
    }

    /// Sends everything queued now, a request per 25 events (the exit path). Completes when each request is sent or lost.
    public async Task Flush()
    {
        while (true)
        {
            string? body;
            lock (_gate)
            {
                body = TakeBatch();
            }
            if (body is null)
            {
                return;
            }
            await Send(body).ConfigureAwait(false);
        }
    }

    private void Schedule()
    {
        if (_scheduled || _queue.Count == 0)
        {
            return;
        }
        _scheduled = true;
        _later(Interval, Tick);
    }

    /// One request a minute: the timer sends the next batch and sets the next timer when events are still waiting.
    private void Tick()
    {
        string? body;
        lock (_gate)
        {
            _scheduled = false;
            body = TakeBatch();
            Schedule();
        }
        if (body is not null)
        {
            _ = Send(body);
        }
    }

    /// The request body of the next batch, or null when there is nothing to send. Called under the gate.
    private string? TakeBatch()
    {
        if (!Sending)
        {
            _queue.Clear();
            return null;
        }
        if (_queue.Count == 0)
        {
            return null;
        }
        var count = Math.Min(BatchSize, _queue.Count);
        var events = new JsonArray(_queue.GetRange(0, count).ToArray());
        _queue.RemoveRange(0, count);
        _clientId ??= ClientId();
        return new JsonObject { ["client_id"] = JsonValue.Create(_clientId), ["events"] = events }.ToJsonString();
    }

    /// One random id per install: it ties the events to an install, never to a person.
    private string ClientId()
    {
        var id = _read("analytics.cid");
        if (string.IsNullOrEmpty(id))
        {
            id = Guid.NewGuid().ToString();
            _write("analytics.cid", id);
        }
        return id;
    }

    private async Task Send(string body)
    {
        var url = $"{Endpoint}?measurement_id={MeasurementId}&api_secret={Uri.EscapeDataString(_secret)}";
        try
        {
            await _post(url, body).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Lost on failure, by design: statistics, not data.
        }
    }
}
