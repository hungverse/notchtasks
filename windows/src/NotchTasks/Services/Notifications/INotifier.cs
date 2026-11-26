using NotchTasks.Core.Notch;

namespace NotchTasks.Services.Notifications;

/// The Windows toast seam. The app state posts a toast here when the toast notifies (the Mac's notify) and the
/// systemNotifications setting is on. Tests pass a fake, so no test shows a real Windows notification.
public interface INotifier
{
    void Post(Toast t);
}
