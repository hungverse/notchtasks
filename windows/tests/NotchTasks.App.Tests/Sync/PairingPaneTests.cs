using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NotchTasks.ControlPanel;
using NotchTasks.ControlPanel.Panes;
using NotchTasks.Core;
using NotchTasks.Core.Sync;
using NotchTasks.Services.Sync;
using Xunit;

namespace NotchTasks.AppTests;

/// The Pairing pane (Mac PhonePane): the steps and the code while unpaired, the toggles, the phones and Unpair all once paired.
/// The hub runs over fakes; nothing reaches the network.
[Collection("L10n")]
public sealed class PairingPaneTests
{
    private const long OneDayMs = 86_400_000L;

    [Fact]
    public void ProStatus_NoPro_Lifetime_Until()
    {
        Sta.Run(() =>
        {
            PairingVisuals.InitEnglish();
            var now = new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);
            const string none = "No Pro: one phone, basic notifications";

            Assert.Equal(none, PairingPane.ProStatus(null, now));
            Assert.Equal(none, PairingPane.ProStatus(now.ToUnixTimeMilliseconds() - 1, now));
            Assert.Equal(none, PairingPane.ProStatus(now.ToUnixTimeMilliseconds(), now));
            Assert.Equal("Pro lifetime", PairingPane.ProStatus(30_000_000_000_000, now));

            var until = now.ToUnixTimeMilliseconds() + 3 * OneDayMs;
            var date = DateTimeOffset.FromUnixTimeMilliseconds(until).ToLocalTime().DateTime.ToString("d", System.Globalization.CultureInfo.CurrentCulture);
            Assert.Equal("Pro until " + date, PairingPane.ProStatus(until, now));
        });
    }

    [Fact]
    public void PaneOrder_PairingAfterWatching()
    {
        var all = Enum.GetValues<PaneId>();
        Assert.Equal(PaneId.Pairing, all[Array.IndexOf(all, PaneId.Watching) + 1]);
        Assert.Equal("Pairing", PaneId.Pairing.TitleKey());
    }

    [Fact]
    public void Pane_NoBridge_SaysPairingIsNotAvailable()
    {
        Sta.Run(() =>
        {
            PairingVisuals.InitEnglish();
            var pane = new PairingPane(null);
            var window = new Window { Content = pane };
            Fixture.ShowOffScreen(window);
            try
            {
                Assert.Equal(Visibility.Visible, Fixture.Named<Border>(pane, "UnavailableCard").Visibility);
                Assert.Equal("Pairing is not available", Fixture.Named<TextBlock>(pane, "UnavailableText").Text);
                Assert.Equal(Visibility.Collapsed, Fixture.Named<StackPanel>(pane, "Body").Visibility);
            }
            finally
            {
                PairingVisuals.Unhost(window);
            }
        });
    }

    [Fact]
    public void Pane_Unpaired_ShowsStepsAndPairButton()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            PairingVisuals.InitEnglish();
            using var state = Fixture.NewState();
            var hub = PairingRig.MakeHub(new PairingHttp(), paired: false);
            var bridge = new SyncBridge(state, hub);
            var pane = new PairingPane(bridge);
            var window = new Window { Content = pane };
            Fixture.ShowOffScreen(window);
            try
            {
                Assert.Equal("1. Install NotchTasks on your phone", Fixture.Named<TextBlock>(pane, "Step1").Text);
                Assert.Equal("2. Click Pair device", Fixture.Named<TextBlock>(pane, "Step2").Text);
                Assert.Equal("3. Scan the QR code with the app or Camera", Fixture.Named<TextBlock>(pane, "Step3").Text);

                var pair = Fixture.Named<Button>(pane, "PairButton");
                Assert.Equal(Visibility.Visible, pair.Visibility);
                Assert.Equal("Pair device…", pair.Content);
                Assert.Equal(Visibility.Collapsed, Fixture.Named<StackPanel>(pane, "CodePanel").Visibility);
            }
            finally
            {
                PairingVisuals.Unhost(window);
                PairingVisuals.Stop(hub);
            }
        });
    }

    [Fact]
    public void Pane_PairClicked_ShowsQrAndCopyCode()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            PairingVisuals.InitEnglish();
            using var state = Fixture.NewState();
            var http = new PairingHttp { HoldSignUp = new TaskCompletionSource() };
            var hub = PairingRig.MakeHub(http, paired: false);
            var bridge = new SyncBridge(state, hub);
            var pane = new PairingPane(bridge);
            var window = new Window { Content = pane };
            Fixture.ShowOffScreen(window);
            try
            {
                var pair = Fixture.Named<Button>(pane, "PairButton");
                pair.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                // The code is being created: the button says so and takes no more clicks.
                Assert.Equal("Creating code…", pair.Content);
                Assert.False(pair.IsEnabled);

                http.HoldSignUp!.SetResult();
                Fixture.PumpUntil(() => Fixture.Named<System.Windows.Controls.Image>(pane, "QrCode").Source is not null);

                Assert.Equal(Visibility.Visible, Fixture.Named<StackPanel>(pane, "CodePanel").Visibility);
                Assert.Equal("This code only works for your devices.", Fixture.Named<TextBlock>(pane, "CodeNote").Text);
                Assert.Equal("Copy code", Fixture.Named<Button>(pane, "CopyButton").Content);
            }
            finally
            {
                PairingVisuals.Unhost(window);
                PairingVisuals.Stop(hub);
            }
        });
    }

    [Fact]
    public void Pane_Paired_ShowsTogglesPhonesUnpairAll()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            PairingVisuals.InitEnglish();
            using var state = Fixture.NewState();
            var http = new PairingHttp
            {
                MembersJson = "{\"uid-pixel\":{\"role\":\"phone\",\"name\":\"Pixel\"},\"uid-android\":{\"role\":\"phone\",\"name\":\"Android\"}}",
            };
            var hub = PairingRig.MakeHub(http, paired: true);
            var bridge = new SyncBridge(state, hub);
            var pane = new PairingPane(bridge);
            var window = new Window { Content = pane };
            Fixture.ShowOffScreen(window);
            try
            {
                Fixture.PumpUntil(() => bridge.State.Phones.Count == 2);
                Fixture.Flush();

                Assert.Equal(Visibility.Collapsed, Fixture.Named<Button>(pane, "PairButton").Visibility);
                Assert.Equal("Connect directly on the same Wi‑Fi", Fixture.Named<CheckBox>(pane, "LanSwitch").Content);
                Assert.Equal("Only alert my phone when I'm away", Fixture.Named<CheckBox>(pane, "OnlyAwaySwitch").Content);
                Assert.Equal("Over the Internet", Fixture.Named<TextBlock>(pane, "ConnectionValue").Text);
                Assert.Equal("Pair another device…", Fixture.Named<Button>(pane, "AnotherButton").Content);

                var texts = PairingVisuals.Texts(Fixture.Named<StackPanel>(pane, "PhoneRows"));
                Assert.Equal(new[] { "Android", "Pixel" }, texts.Where(t => t is "Android" or "Pixel").ToArray());
                Assert.Equal("Unpair all", Fixture.Named<Button>(pane, "UnpairAllButton").Content);
                Assert.Equal("Every device will disconnect from this computer. Pairing again uses a new code.",
                    Fixture.Named<TextBlock>(pane, "PairedNote").Text);
            }
            finally
            {
                PairingVisuals.Unhost(window);
                PairingVisuals.Stop(hub);
            }
        });
    }

    [Fact]
    public void Pane_Unloaded_ClearsTheCode()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            PairingVisuals.InitEnglish();
            using var state = Fixture.NewState();
            var http = new PairingHttp
            {
                MembersJson = "{\"uid-pixel\":{\"role\":\"phone\",\"name\":\"Pixel\"}}",
            };
            var hub = PairingRig.MakeHub(http, paired: true);
            var bridge = new SyncBridge(state, hub);
            var pane = new PairingPane(bridge);
            var window = new Window { Content = pane };
            Fixture.ShowOffScreen(window);
            try
            {
                Fixture.PumpUntil(() => bridge.State.Phones.Count == 1);
                Fixture.Named<Button>(pane, "AnotherButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var qr = Fixture.Named<System.Windows.Controls.Image>(pane, "QrCode");
                Fixture.PumpUntil(() => qr.Source is not null);

                PairingVisuals.Unhost(window);

                Assert.Null(qr.Source);
            }
            finally
            {
                PairingVisuals.Stop(hub);
            }
        });
    }

    [Fact]
    public void Pane_PollsPhonesEvery4sWhileShown()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            PairingVisuals.InitEnglish();
            using var state = Fixture.NewState();
            var http = new PairingHttp
            {
                MembersJson = "{\"uid-pixel\":{\"role\":\"phone\",\"name\":\"Pixel\"}}",
            };
            var hub = PairingRig.MakeHub(http, paired: true);
            var bridge = new SyncBridge(state, hub);
            var pane = new PairingPane(bridge);
            var window = new Window { Content = pane };
            Fixture.ShowOffScreen(window);
            try
            {
                Fixture.PumpUntil(() => http.Count("/members") == 1);
                Assert.Equal(TimeSpan.FromSeconds(4), Fixture.Field<DispatcherTimer>(pane, "_poll").Interval);

                Fixture.Call(pane, "OnPollTick");
                Fixture.PumpUntil(() => http.Count("/members") == 2);

                PairingVisuals.Unhost(window);
                Fixture.Call(pane, "OnPollTick");
                Fixture.Flush();
                Assert.Equal(2, http.Count("/members"));
            }
            finally
            {
                PairingVisuals.Stop(hub);
            }
        });
    }

    [Fact]
    public void Pane_MarksThePhoneThatHoldsPro()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            PairingVisuals.InitEnglish();
            using var state = Fixture.NewState();
            var http = new PairingHttp
            {
                MembersJson = "{\"uid-pixel\":{\"role\":\"phone\",\"name\":\"Pixel\"},\"uid-android\":{\"role\":\"phone\",\"name\":\"Android\"}}",
                GrantsJson = "{\"g1\":{\"uid\":\"uid-pixel\",\"until\":4000000000000}}",
            };
            var hub = PairingRig.MakeHub(http, paired: true);
            var bridge = new SyncBridge(state, hub);
            var pane = new PairingPane(bridge);
            var window = new Window { Content = pane };
            Fixture.ShowOffScreen(window);
            try
            {
                Fixture.PumpUntil(() => bridge.State.ProHolders.Count == 1 && bridge.State.Phones.Count == 2);
                Fixture.Flush();

                var rows = Fixture.Named<StackPanel>(pane, "PhoneRows").Children.OfType<DockPanel>().ToList();
                var pixel = Assert.Single(rows, row => PairingVisuals.Texts(row).Contains("Pixel"));
                var android = Assert.Single(rows, row => PairingVisuals.Texts(row).Contains("Android"));
                Assert.Contains("Pro", PairingVisuals.Texts(pixel));
                Assert.DoesNotContain("Pro", PairingVisuals.Texts(android));
            }
            finally
            {
                PairingVisuals.Unhost(window);
                PairingVisuals.Stop(hub);
            }
        });
    }

    [Fact]
    public void Pane_FailedPairing_ShowsNoCode()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            PairingVisuals.InitEnglish();
            using var state = Fixture.NewState();
            var http = new PairingHttp { HoldSignUp = new TaskCompletionSource() };
            http.HoldSignUp.SetException(new HttpRequestException("offline"));
            var hub = PairingRig.MakeHub(http, paired: false);
            var bridge = new SyncBridge(state, hub);
            var pane = new PairingPane(bridge);
            var window = new Window { Content = pane };
            Fixture.ShowOffScreen(window);
            try
            {
                Fixture.Named<Button>(pane, "PairButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Fixture.PumpUntil(() => Fixture.Named<Button>(pane, "PairButton").IsEnabled);

                // The failed pairing creates no code, so the QR stays hidden and the pane is ready to try again.
                Assert.Null(Fixture.Named<System.Windows.Controls.Image>(pane, "QrCode").Source);
                Assert.Equal(Visibility.Collapsed, Fixture.Named<StackPanel>(pane, "CodePanel").Visibility);
            }
            finally
            {
                PairingVisuals.Unhost(window);
                PairingVisuals.Stop(hub);
            }
        });
    }
}
