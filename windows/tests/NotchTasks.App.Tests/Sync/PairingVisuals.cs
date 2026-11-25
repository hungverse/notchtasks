using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using NotchTasks.Core;

namespace NotchTasks.AppTests;

/// Helpers the Pairing tests read the visual tree with, and the English tables they assert against.
internal static class PairingVisuals
{
    /// Loads the Mac's tables with English, so the texts the tests assert are the English keys.
    public static void InitEnglish()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "mac", "Sources")))
        {
            dir = Path.GetDirectoryName(dir);
        }
        L10n.Init(Path.Combine(dir!, "mac", "Sources"), "en");
    }

    /// The text of every TextBlock and Button under the element, in tree order.
    public static List<string> Texts(DependencyObject root)
    {
        var texts = new List<string>();
        Walk(root, element =>
        {
            if (element is TextBlock text)
            {
                texts.Add(text.Text);
            }
            else if (element is ContentControl { Content: string content })
            {
                texts.Add(content);
            }
        });
        return texts;
    }

    /// Every element under the root, the root first.
    public static void Walk(DependencyObject root, Action<DependencyObject> visit)
    {
        visit(root);
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            Walk(child, visit);
        }
    }

    /// Stops a hub and waits for the stop, pumping the UI thread so its continuations run.
    public static void Stop(NotchTasks.Core.Sync.SyncHub hub)
    {
        var stop = hub.StopAsync();
        Fixture.PumpUntil(() => stop.IsCompleted);
    }

    /// Detaches the element from its window (its Unloaded runs) and closes the window.
    public static void Unhost(Window window)
    {
        window.Content = null;
        window.UpdateLayout();
        // Unloaded is raised after the layout, on the dispatcher: the queued work runs before the test looks.
        Fixture.Flush();
        window.Close();
    }
}
