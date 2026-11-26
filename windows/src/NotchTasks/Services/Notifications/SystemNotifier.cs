using NotchTasks.Core.Notch;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace NotchTasks.Services.Notifications;

/// Shows a toast as a Windows notification, under the installer's AUMID, so it carries the app's name and icon. Without the Start
/// menu shortcut (a portable copy, a dev build) Windows refuses the toast: that is logged once and skipped, since the pill still
/// shows the toast. It never throws, because a notification must never take the app down.
public sealed class SystemNotifier : INotifier
{
    private bool _failureLogged;

    public void Post(Toast t)
    {
        try
        {
            var xml = new XmlDocument();
            xml.LoadXml(ToastXml.Build(t.Title, t.Subtitle));
            ToastNotificationManager.CreateToastNotifier(AppIdentity.UserModelId).Show(new ToastNotification(xml));
        }
        catch (Exception e)  // the OS reports a missing shortcut or a switched-off notification service as a COM error, of any kind
        {
            if (!_failureLogged)
            {
                _failureLogged = true;
                ErrorLog.Write(e);
            }
        }
    }
}
