using NotchTasks.Core.Notch;
using Xunit;

namespace NotchTasks.Core.Tests;

public class ToastQueueTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 19, 9, 0, 0, TimeSpan.Zero);

    private static Toast T(string title = "t", bool sticky = false, bool compact = false,
        string? ask = null, string? module = null, string? url = null, string? pane = null, string? action = null) =>
        new("✓", "#34C759", title, Sticky: sticky, Compact: compact, AskId: ask, Module: module,
            Url: url, Pane: pane, Action: action);

    [Fact]
    public void ShowTime_FollowsMac()
    {
        Assert.Equal(30, ToastQueue.ShowTime(T(sticky: true)).TotalSeconds);
        Assert.Equal(3, ToastQueue.ShowTime(T(compact: true)).TotalSeconds);
        Assert.Equal(4.5, ToastQueue.ShowTime(T()).TotalSeconds);
    }

    [Fact]
    public void Enqueue_shows_first_at_once_and_queues_the_rest()
    {
        var q = new ToastQueue();
        var a = T("a");
        var b = T("b");

        q.Enqueue(a, T0);
        q.Enqueue(b, T0);
        Assert.Same(a, q.Current);

        q.Tick(T0.AddSeconds(4.5), hovered: false);
        Assert.Same(b, q.Current);

        q.Tick(T0.AddSeconds(9), hovered: false);
        Assert.Null(q.Current);
    }

    [Fact]
    public void Tick_returns_true_only_when_current_changes()
    {
        var q = new ToastQueue();
        q.Enqueue(T("a"), T0);

        Assert.False(q.Tick(T0.AddSeconds(1), hovered: false));
        Assert.True(q.Tick(T0.AddSeconds(4.5), hovered: false));
    }

    [Fact]
    public void Changed_fires_whenever_current_changes()
    {
        var q = new ToastQueue();
        var count = 0;
        q.Changed += () => count++;

        q.Enqueue(T("a"), T0);
        q.Enqueue(T("b"), T0);
        Assert.Equal(1, count);

        q.Tick(T0.AddSeconds(4.5), hovered: false);
        Assert.Equal(2, count);
    }

    [Fact]
    public void Hover_keeps_toast_until_calm_for_1500ms()
    {
        var q = new ToastQueue();
        var a = T("a");
        q.Enqueue(a, T0);

        q.Tick(T0.AddSeconds(4.4), hovered: true);
        q.Tick(T0.AddSeconds(6), hovered: true);
        q.Tick(T0.AddSeconds(7.4), hovered: false);
        Assert.Same(a, q.Current);

        q.Tick(T0.AddSeconds(7.6), hovered: false);
        Assert.Null(q.Current);
    }

    [Fact]
    public void Hover_before_show_time_does_not_extend()
    {
        var q = new ToastQueue();
        var a = T("a");
        q.Enqueue(a, T0);

        q.Tick(T0.AddSeconds(4.0), hovered: true);
        q.Tick(T0.AddSeconds(4.2), hovered: false);
        Assert.Same(a, q.Current);

        q.Tick(T0.AddSeconds(4.5), hovered: false);
        Assert.Null(q.Current);
    }

    [Fact]
    public void DropModule_raises_Changed_when_it_removes_the_shown_toast()
    {
        var q = new ToastQueue();
        q.Enqueue(T("net", module: "network"), T0);
        var count = 0;
        q.Changed += () => count++;

        q.DropModule("network", T0.AddSeconds(1));

        Assert.Equal(1, count);
        Assert.Null(q.Current);
    }

    [Fact]
    public void Clear_raises_Changed_when_a_toast_was_showing()
    {
        var q = new ToastQueue();
        q.Enqueue(T("a"), T0);
        var count = 0;
        q.Changed += () => count++;

        q.Clear();

        Assert.Equal(1, count);
    }

    [Fact]
    public void Dismiss_shows_next()
    {
        var q = new ToastQueue();
        var a = T("a");
        var b = T("b");
        q.Enqueue(a, T0);
        q.Enqueue(b, T0);

        q.Dismiss(T0.AddSeconds(1));

        Assert.Same(b, q.Current);
    }

    [Fact]
    public void DropModule_removes_queued_and_shown_toasts()
    {
        var q = new ToastQueue();
        var shown = T("net1", module: "network");
        var queued = T("net2", module: "network");
        var keep = T("other", module: "clock");
        q.Enqueue(shown, T0);
        q.Enqueue(queued, T0);
        q.Enqueue(keep, T0);

        q.DropModule("network", T0.AddSeconds(1));

        Assert.Same(keep, q.Current);
        q.Dismiss(T0.AddSeconds(2));
        Assert.Null(q.Current);
    }

    [Fact]
    public void DropAsks_removes_their_toasts()
    {
        var q = new ToastQueue();
        var shown = T("ask1", ask: "x");
        var queued = T("ask2", ask: "y");
        var keep = T("plain");
        q.Enqueue(shown, T0);
        q.Enqueue(queued, T0);
        q.Enqueue(keep, T0);

        q.DropAsks(new HashSet<string> { "x", "y" }, T0.AddSeconds(1));

        Assert.Same(keep, q.Current);
    }

    [Fact]
    public void Clear_removes_current_and_queued()
    {
        var q = new ToastQueue();
        q.Enqueue(T("a"), T0);
        q.Enqueue(T("b"), T0);

        q.Clear();

        Assert.Null(q.Current);
        Assert.False(q.Tick(T0.AddSeconds(10), hovered: false));
    }

    [Fact]
    public void ActionTitle_is_null_without_url_or_pane_else_open_by_default()
    {
        Assert.Null(T().ActionTitle);
        Assert.Equal(L10n.L("Open"), T(url: "https://x").ActionTitle);
        Assert.Equal(L10n.L("Open"), T(pane: "tasks").ActionTitle);
        Assert.Equal("Show", T(url: "https://x", action: "Show").ActionTitle);
    }

    [Fact]
    public void Each_toast_gets_its_own_id()
    {
        Assert.NotEqual(T("a").Id, T("a").Id);
    }
}
