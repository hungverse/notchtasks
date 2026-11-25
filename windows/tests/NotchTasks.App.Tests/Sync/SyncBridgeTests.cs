using System.IO;
using System.IO.Pipelines;
using System.Net;
using System.Text.Json.Nodes;
using NotchTasks.Core;
using NotchTasks.Core.Agents;
using NotchTasks.Core.Asks;
using NotchTasks.Core.Limits;
using NotchTasks.Core.Modules;
using NotchTasks.Core.Notch;
using NotchTasks.Core.Settings;
using NotchTasks.Core.Sync;
using NotchTasks.Services.Sync;
using Xunit;

namespace NotchTasks.AppTests;

/// The bridge between the app state and the sync hub: what the phone is sent, the phone's answers applied here, the notch edits.
/// The hub runs over fakes: the Firebase stand-in answers the group's reads from fixed JSON, and nothing touches the network.
[Collection("L10n")]
public sealed class SyncBridgeTests
{
    private const long ProUntil = 4_000_000_000_000;
    private static readonly string ProJson = $"{{\"pro\":true,\"until\":{ProUntil}}}";

    [Fact]
    public void InputOf_RunsFromRunning_BuildsFromBuildRunning_WithAgentAndIcons()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var settings = SettingsStore.Load(AppPaths.SettingsFile);
            var claudeIcon = "{\"id\":\"BADGE1\",\"symbol\":\"star\"}";
            var claude = new WatchItem("claude-1", "Claude", WatchKind.Claude, Icon: claudeIcon);
            var running = new ItemState(claude, new[]
            {
                new Job("run-1", "Thinking", "12 s", true),
                new Job("run-idle", "Waiting", "", false),
            });
            var ci = new WatchItem("ci-1", "Xcode build", WatchKind.Process, Build: true);
            var build = new ItemState(ci, new[]
            {
                new Job("build-1", "Build", "1 min", true, Tool: "Xcode"),
                new Job("build-done", "Old", "", false, Tool: "Xcode"),
            });
            settings.Set("toolIcons", JsonNode.Parse("{\"Xcode\":{\"id\":\"TOOL1\",\"symbol\":\"hammer\"}}"));
            var snapshot = new NotchSnapshot(
                Array.Empty<string>(), 0, null, null, new SensorState(Array.Empty<string>(), false, false), null, null)
            {
                Running = new[] { running },
                BuildRunning = new[] { build },
            };

            var input = SyncBridge.InputOf(snapshot, Array.Empty<AgentAsk>(), null, settings);

            var run = Assert.Single(input.Runs);
            Assert.Equal("run-1", run.Id);
            Assert.Equal("claude", run.Agent);
            Assert.Equal("BADGE1", run.Icon!["id"]!.GetValue<string>());
            var b = Assert.Single(input.Builds);
            Assert.Equal("build-1", b.Id);
            Assert.Null(b.Agent);
            Assert.Equal("TOOL1", b.Icon!["id"]!.GetValue<string>());
        });
    }

    [Fact]
    public void InputOf_LimitsFlattened_HealthFromBatteryLoadThermal()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var settings = SettingsStore.Load(AppPaths.SettingsFile);
            var resets = DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_000_000);
            var provider = new ProviderLimits(
                "claude",
                "Claude",
                null,
                new[] { new LimitWindow("5h", 42.7, resets), new LimitWindow("7d", 10, resets) },
                DateTimeOffset.UtcNow);
            var snapshot = new NotchSnapshot(
                Array.Empty<string>(), 0, null, null, new SensorState(Array.Empty<string>(), false, false),
                new BatteryState(64, true, true),
                new SystemLoad(0.25, 0.5))
            {
                Limits = new[] { provider },
            };

            var input = SyncBridge.InputOf(snapshot, Array.Empty<AgentAsk>(), new ThermalReading("CPU", 71.5), settings);

            Assert.Equal(2, input.Limits.Count);
            Assert.All(input.Limits, l => Assert.Equal("Claude", l.Provider));
            Assert.Equal(new[] { "5h", "7d" }, input.Limits.Select(l => l.Label).ToArray());
            Assert.Equal(42.7, input.Limits[0].Used);
            var health = input.Health!;
            Assert.Equal(64, health.Battery);
            Assert.True(health.Charging);
            Assert.Equal(71.5, health.Temp);
            Assert.Equal(0.25, health.Cpu);
            Assert.Equal(0.5, health.Memory);
        });
    }

    [Fact]
    public void NotchOf_HasAll25Keys_LockFieldsOff_SeenToolsCapped12()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var settings = SettingsStore.Load(AppPaths.SettingsFile);
            var seen = Enumerable.Range(1, 15).Select(i => $"tool{i}").ToArray();
            settings.Set("seenTools", new JsonArray(seen.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()));

            var notch = SyncBridge.NotchOf(settings);

            Assert.Equal(25, notch.Count);
            Assert.False(notch["haptics"]!.GetValue<bool>());
            Assert.False(notch["lockScreen"]!.GetValue<bool>());
            Assert.False(notch["lockOwnBadges"]!.GetValue<bool>());
            Assert.Empty(notch["lockShows"]!.AsArray());
            Assert.Empty(notch["lockBadges"]!.AsArray());
            Assert.Equal(12, notch["seenTools"]!.AsArray().Count);
            var snapshot = new SyncSnapshot("Mac", Array.Empty<SyncRun>(), Array.Empty<SyncRun>(), Array.Empty<SyncAsk>(),
                Array.Empty<SyncLimit>(), null, Array.Empty<SyncEvent>(), null, notch, null, DateTimeOffset.UtcNow);
            Wire.Snapshot(snapshot);  // the phone's decoder accepts it: no FormatException
        });
    }

    [Fact]
    public void ApplyNotch_ClampsAndFilters()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var settings = SettingsStore.Load(AppPaths.SettingsFile);
            settings.Set("seenTools", JsonNode.Parse("[\"Claude\"]"));
            var notch = JsonNode.Parse(
                """
                {"volume":3,"doneAfter":1,"limitWarn":120,"modules":["timer","bogus"],
                 "badges":[{"id":"A","symbol":"star","image":"C:\\not-here\\a.png"},{"id":"B","symbol":"star"}],
                 "toolIcons":{"Xcode":{"id":"T1","symbol":"hammer"},"Claude":{"id":"T2","symbol":"star"}}}
                """)!.AsObject();

            SyncBridge.ApplyNotch(settings, notch);

            Assert.Equal(1.0, settings.Volume);
            Assert.Equal(5.0, settings.DoneAfter);
            Assert.Equal(100.0, settings.LimitWarn);
            Assert.Equal(new[] { "timer" }, settings.Modules.ToArray());
            var badge = Assert.Single(settings.Badges);
            Assert.Equal("B", badge!["id"]!.GetValue<string>());
            Assert.Equal(new[] { "Claude" }, settings.ToolIcons.Keys.ToArray());
        });
    }

    [Fact]
    public void ApplyNotch_DropsAnInvalidBadgeAndToolIcon_FromThePhone()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var settings = SettingsStore.Load(AppPaths.SettingsFile);
            settings.Set("seenTools", JsonNode.Parse("[\"Claude\"]"));
            var notch = JsonNode.Parse(
                """
                {"badges":[{"id":"BAD","symbol":"star","shape":5},{"id":"OK","symbol":"star","shape":"circle"},
                           {"id":"FR","symbol":"star","frames":4}],
                 "toolIcons":{"Claude":{"id":"T3","symbol":"star","shape":5}}}
                """)!.AsObject();

            SyncBridge.ApplyNotch(settings, notch);

            // BAD has a field of the wrong type and is not stored; FR has frames but no picture this PC holds; OK is the only one kept
            var badge = Assert.Single(settings.Badges);
            Assert.Equal("OK", badge!["id"]!.GetValue<string>());
            Assert.Empty(settings.ToolIcons);
        });
    }

    [Fact]
    public void LoadPath_UpdatesTheInputTheHubIsGiven()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var settings = SettingsStore.Load(AppPaths.SettingsFile);
            using var rig = new Rig(settings, paired: false);
            var app = rig.App;
            app.Bridge!.StartAsync();
            Fixture.PumpUntil(() => app.Bridge!.LastInput is not null);

            // the first sample is the baseline; the second is 50 % busy over the interval (idle 50 of 100)
            app.Health.Sample(new CpuTimes(0, 0, 0), 0.4);
            app.Health.Sample(new CpuTimes(50, 50, 50), 0.4);
            Fixture.Call(app, "PublishLoad");

            var health = app.Bridge!.LastInput!.Health!;
            Assert.Equal(0.5, health.Cpu);
            Assert.Equal(0.4, health.Memory);
        });
    }

    [Fact]
    public void ThermalPath_UpdatesTheInputTheHubIsGiven()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var settings = SettingsStore.Load(AppPaths.SettingsFile);
            using var rig = new Rig(settings, paired: false);
            var app = rig.App;
            app.Bridge!.StartAsync();
            Fixture.PumpUntil(() => app.Bridge!.LastInput is not null);

            Fixture.Call(app, "PublishThermal", new ThermalReading("CPU", 80.0));

            Assert.Equal(80.0, app.Bridge!.LastInput!.Health!.Temp);
        });
    }

    [Fact]
    public void PhoneAnswer_Allow_AnswersTheAsk()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var settings = SettingsStore.Load(AppPaths.SettingsFile);
            using var rig = new Rig(settings, paired: false);
            var ask = new AgentAsk("ask-allow", 1, "s", "Bash", "echo hi", Array.Empty<AskQuestion>(), DateTimeOffset.Now, "claude");
            Fixture.Call(rig.App, "SyncAsks", new List<AgentAsk> { ask });

            rig.App.Bridge!.OnAnswer(Answer("ask-allow", new AskReply(ReplyKinds.Allow, null)));

            Assert.True(File.Exists(Path.Combine(AppPaths.AsksDir, "ask-allow.reply")));
            Assert.Empty(rig.App.Asks);
        });
    }

    [Fact]
    public void PhoneAnswer_ForMissingAsk_IsIgnored()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var settings = SettingsStore.Load(AppPaths.SettingsFile);
            using var rig = new Rig(settings, paired: false);

            rig.App.Bridge!.OnAnswer(Answer("ask-gone", new AskReply(ReplyKinds.Allow, null)));

            Assert.False(File.Exists(Path.Combine(AppPaths.AsksDir, "ask-gone.reply")));
        });
    }

    [Fact]
    public void PhoneAnswer_Timer25_StartsTimer_Timer0_Stops()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var settings = SettingsStore.Load(AppPaths.SettingsFile);
            using var rig = new Rig(settings, paired: false);
            var app = rig.App;

            rig.App.Bridge!.OnAnswer(TimerAnswer(25));
            Assert.Equal(TimeSpan.FromMinutes(25), app.Timer.State!.Total);

            rig.App.Bridge!.OnAnswer(TimerAnswer(0));
            Assert.Null(app.Timer.State);
        });
    }

    [Fact]
    public void PhoneAnswer_Timer500_CappedAt180()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var settings = SettingsStore.Load(AppPaths.SettingsFile);
            using var rig = new Rig(settings, paired: false);

            rig.App.Bridge!.OnAnswer(TimerAnswer(500));

            Assert.Equal(TimeSpan.FromMinutes(180), rig.App.Timer.State!.Total);
        });
    }

    [Fact]
    public void Record_PassesEventKindTitleSubtitle()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var settings = SettingsStore.Load(AppPaths.SettingsFile);
            using var rig = new Rig(settings, paired: true);
            rig.Http.EntitlementJson = ProJson;  // the event rides the snapshot write only for a Pro group
            var app = rig.App;

            app.Bridge!.StartAsync();
            Fixture.PumpUntil(() => rig.Hub!.State.Pro);
            app.Announce(new Toast("check", "#30D158", "Build failed", "main · 3 min", Event: "ciFailed"));
            // A non-ask write reaches Firebase at most once a minute: the clock moves past that, then the publish is repeated
            // until a write carries the event (the publish is a no-op while nothing changed).
            rig.Now = rig.Now.AddSeconds(61);
            Fixture.PumpUntil(() =>
            {
                app.Publish();
                return rig.Http.SnapshotBodies.Any(b => EventBox(b).Length > 0);
            });

            var body = rig.Http.SnapshotBodies.Last(b => EventBox(b).Length > 0);
            var ev = Box.Open(EventBox(body), Box.FromB64Url(rig.Vault.Stored!.K)).AsObject();
            Assert.Equal("ciFailed", ev["kind"]!.GetValue<string>());
            Assert.Equal("Build failed", ev["title"]!.GetValue<string>());
            Assert.Equal("main · 3 min", ev["detail"]!.GetValue<string>());
        });
    }

    [Fact]
    public void ProNote_ShowsToastWithoutSystemNotification()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var settings = SettingsStore.Load(AppPaths.SettingsFile);
            using var rig = new Rig(settings, paired: true);
            rig.Http.ProNoteJson = "{\"why\":\"expired\",\"at\":1800000000000}";
            var app = rig.App;

            app.Bridge!.StartAsync();
            Fixture.PumpUntil(() => app.Toasts.Current is not null);

            var toast = app.Toasts.Current!;
            Assert.Equal("sparkles", toast.Glyph);
            Assert.Equal("#FF9F0A", toast.Tint);
            Assert.Null(toast.Event);  // no sound
        });
    }

    [Fact]
    public void NoSyncSeam_NothingStarts()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var settings = SettingsStore.Load(AppPaths.SettingsFile);
            using var state = new AppState(settings, new AskStore(AppPaths.AsksDir), Fixture.Inert(settings));

            Assert.Null(state.Bridge);
            state.Publish();
        });
    }

    /// The sealed event of a snapshot write body (empty when the write carries no event).
    private static string EventBox(string body) => JsonNode.Parse(body)!["eventBox"]!.GetValue<string>();

    private static PhoneAnswer Answer(string ask, AskReply reply) =>
        new(ask, reply, DateTimeOffset.UtcNow, null, null, null, false);

    private static PhoneAnswer TimerAnswer(int minutes) =>
        new(string.Empty, new AskReply(ReplyKinds.Pass, null), DateTimeOffset.UtcNow, null, null, minutes, false);

    /// The Firebase stand-in: the group's reads answer from the fields, every write answers "null", and the answers stream stays
    /// open until the hub stops it. The snapshot bodies written are kept.
    private sealed class BridgeHttp : ISyncHttp
    {
        private readonly object _gate = new();
        private readonly List<string> _snapshots = new();

        public string EntitlementJson { get; set; } = "null";

        public string ProNoteJson { get; set; } = "null";

        public List<string> SnapshotBodies
        {
            get
            {
                lock (_gate)
                {
                    return _snapshots.ToList();
                }
            }
        }

        public Task<SyncHttpResult> SendAsync(string method, string url, string? body, CancellationToken ct, string contentType = "application/json")
        {
            if (method == "PUT" && url.Contains("/snapshot.json", StringComparison.Ordinal) && body is not null)
            {
                lock (_gate)
                {
                    _snapshots.Add(body);
                }
            }
            var text = url.Contains("securetoken", StringComparison.Ordinal) ? "{\"id_token\":\"tok-1\"}"
                : url.Contains("/entitlement.json", StringComparison.Ordinal) ? EntitlementJson
                : url.Contains("/proNote.json", StringComparison.Ordinal) ? ProNoteJson
                : url.Contains("/members.json", StringComparison.Ordinal) ? "{}"
                : "null";
            return Task.FromResult(new SyncHttpResult(200, text));
        }

        public Task<Stream> OpenEventStreamAsync(string url, CancellationToken ct)
        {
            var pipe = new Pipe();
            _ = ct.Register(() => pipe.Writer.Complete());
            return Task.FromResult(pipe.Reader.AsStream());
        }
    }

    private sealed class BridgeVault : IGroupVault
    {
        public SyncGroup? Stored { get; set; }

        public SyncGroup? Load() => Stored;

        public string? Save(SyncGroup? group)
        {
            Stored = group;
            return null;
        }
    }

    private sealed class BridgeAdvertiser : IServiceAdvertiser
    {
        public void Publish(string instanceName, int port)
        {
        }

        public void Unpublish()
        {
        }

        public void Dispose()
        {
        }
    }

    /// An app state with a sync hub on the fakes: paired (a group is in the vault) or not. Lan is off and the loopback is the bind.
    private sealed class Rig : IDisposable
    {
        public Rig(SettingsStore settings, bool paired)
        {
            if (paired)
            {
                var (g, k) = Box.RandomGroup();
                Vault.Stored = new SyncGroup(g, k, "uid-mac", "refresh-1");
            }
            var options = Fixture.Inert(settings) with { Sync = _ => MakeHub() };
            App = new AppState(settings, new AskStore(AppPaths.AsksDir), options);
        }

        public BridgeHttp Http { get; } = new();

        public BridgeVault Vault { get; } = new();

        /// The hub's clock, moved by the tests (a write is throttled by it).
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;

        public SyncHub? Hub { get; private set; }

        public AppState App { get; }

        public void Dispose() => App.Dispose();

        private SyncHub MakeHub()
        {
            var notes = new Dictionary<string, double>(StringComparer.Ordinal);
            var deps = new SyncDeps(
                Http,
                Vault,
                () => new BridgeAdvertiser(),
                "Test PC",
                () => Now,
                () => false,
                g => notes.TryGetValue(g, out var at) ? at : 0,
                (g, at) => notes[g] = at,
                _ => { })
            {
                LanBindAddress = IPAddress.Loopback,
                Lan = false,
                OnlyAway = false,
            };
            Hub = new SyncHub(deps);
            return Hub;
        }
    }
}
