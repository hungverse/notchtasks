using System.Threading;
using System.Threading.Tasks;
using NotchTasks.Core.Notch;
using NotchTasks.Services;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace NotchTasks.Services.Notifications;

/// Shows a toast as a Windows notification, under the installer's AUMID, so it carries the app's name and icon. The show runs on a
/// thread-pool task: the shell's notification service is another process, and a stalled one must not stall the pill on the UI
/// thread. Without the Start menu shortcut (a portable copy, a dev build) Windows refuses the toast: that is logged once and
/// skipped, since the pill still shows the toast. It never throws, because a notification must never take the app down.
public sealed class SystemNotifier : INotifier
{
    private readonly Action<string> _show;
    private readonly Action<Exception> _logFailure;
    private int _failureLogged;  // 0 until the first failure is logged; Interlocked so two posts racing cannot both log

    /// The app's notifier: the toast goes to Windows, and a failure goes to the error log.
    public SystemNotifier()
        : this(ShowWindowsToast, ErrorLog.Write)
    {
    }

    /// The seams: show gets the toast's XML, logFailure gets a failure (at most once). A test passes fakes for both.
    internal SystemNotifier(Action<string> show, Action<Exception> logFailure)
    {
        _show = show;
        _logFailure = logFailure;
    }

    public void Post(Toast t) => _ = PostAsync(t);

    /// The post as a task, so a test can wait for it. It never faults: a failure is logged once and dropped.
    internal Task PostAsync(Toast t) => Task.Run(() =>
    {
        try
        {
            _show(ToastXml.Build(t.Title, t.Subtitle));
        }
        catch (Exception e)  // the OS reports a missing shortcut or a switched-off notification service as a COM error, of any kind
        {
            if (Interlocked.Exchange(ref _failureLogged, 1) == 0)
            {
                _logFailure(e);
            }
        }
    });

    private static void ShowWindowsToast(string xml)
    {
        var doc = new XmlDocument();
        doc.LoadXml(xml);
        ToastNotificationManager.CreateToastNotifier(AppIdentity.UserModelId).Show(new ToastNotification(doc));
    }
}
