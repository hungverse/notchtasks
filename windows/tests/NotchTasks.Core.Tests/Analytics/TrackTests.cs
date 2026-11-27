using System.Text.Json.Nodes;
using NotchTasks.Core.Analytics;
using Xunit;

namespace NotchTasks.Core.Tests.Analytics;

public sealed class TrackTests
{
    private sealed class Fake
    {
        public Dictionary<string, string> Store { get; } = new(StringComparer.Ordinal);
        public List<(string Url, JsonObject Body)> Posts { get; } = [];
        public List<(TimeSpan Delay, Action Run)> Timers { get; } = [];
        public bool Enabled { get; set; } = true;

        public Track Make(string secret = "secret") => new(
            secret, "3.0.0", DateTimeOffset.FromUnixTimeSeconds(1_000), () => Enabled,
            key => Store.TryGetValue(key, out var value) ? value : null,
            (key, value) => Store[key] = value,
            (url, body) =>
            {
                Posts.Add((url, JsonNode.Parse(body)!.AsObject()));
                return Task.CompletedTask;
            },
            (delay, run) => Timers.Add((delay, run)));

        public void FireTimer()
        {
            var (_, run) = Timers[^1];
            Timers.RemoveAt(Timers.Count - 1);
            run();
        }
    }

    [Fact]
    public async Task Off_or_without_a_secret_sends_nothing()
    {
        var off = new Fake { Enabled = false };
        Assert.False(off.Make().Event("ask_reply"));
        await off.Make().Flush();
        Assert.Empty(off.Posts);
        Assert.Empty(off.Timers);

        var noSecret = new Fake();
        Assert.False(noSecret.Make("").Event("ask_reply"));
        await noSecret.Make("").Flush();
        Assert.Empty(noSecret.Posts);
    }

    [Fact]
    public async Task Payload_carries_the_client_id_standard_params_and_the_secret_in_the_url()
    {
        var fake = new Fake();
        var track = fake.Make();
        Assert.True(track.Event("agent_hook_on", new Dictionary<string, object> { ["agent"] = "claude", ["phones"] = 2 }));
        await track.Flush();

        var (url, body) = Assert.Single(fake.Posts);
        Assert.Equal("https://www.google-analytics.com/mp/collect?measurement_id=G-H9RD7VRTDS&api_secret=secret", url);
        var clientId = fake.Store["analytics.cid"];
        Assert.Equal(clientId, body["client_id"]!.GetValue<string>());
        var ev = body["events"]!.AsArray().Single()!.AsObject();
        Assert.Equal("agent_hook_on", ev["name"]!.GetValue<string>());
        var p = ev["params"]!.AsObject();
        Assert.Equal("claude", p["agent"]!.GetValue<string>());
        Assert.Equal(2, p["phones"]!.GetValue<int>());
        Assert.Equal("windows", p["platform"]!.GetValue<string>());
        Assert.Equal("3.0.0", p["app_version"]!.GetValue<string>());
        Assert.Equal(1000L, p["session_id"]!.GetValue<long>());
        Assert.Equal(1, p["engagement_time_msec"]!.GetValue<int>());
    }

    [Fact]
    public void Once_fires_once_and_is_marked_only_when_queued()
    {
        var fake = new Fake { Enabled = false };
        Assert.False(fake.Make().Once("first_launch"));
        Assert.False(fake.Store.ContainsKey("analytics.once.first_launch"));

        fake.Enabled = true;
        var track = fake.Make();
        Assert.True(track.Once("first_launch"));
        Assert.False(track.Once("first_launch"));
        Assert.Equal("1", fake.Store["analytics.once.first_launch"]);
        Assert.Single(fake.Timers);
    }

    [Fact]
    public async Task A_full_queue_goes_out_in_batches_of_25_one_request_a_minute()
    {
        var fake = new Fake();
        var track = fake.Make();
        for (var i = 0; i < 26; i++)
        {
            track.Event("ask_reply");
        }
        Assert.Single(fake.Timers);
        Assert.Equal(TimeSpan.FromMinutes(1), fake.Timers[0].Delay);

        fake.FireTimer();
        Assert.Equal(25, fake.Posts[0].Body["events"]!.AsArray().Count);
        Assert.Single(fake.Timers);

        fake.FireTimer();
        Assert.Equal(2, fake.Posts.Count);
        Assert.Single(fake.Posts[1].Body["events"]!.AsArray());
        Assert.Empty(fake.Timers);

        for (var i = 0; i < 26; i++)
        {
            track.Event("ask_reply");
        }
        await track.Flush();
        Assert.Equal(4, fake.Posts.Count);
    }
}
