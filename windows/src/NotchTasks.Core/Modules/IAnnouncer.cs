using NotchTasks.Core.Notch;

namespace NotchTasks.Core.Modules;

/// Where a module's toasts go: the app plays the toast's sound and queues the toast. notify is the Mac's notify: true also
/// posts it as a Windows notification (when the systemNotifications setting is on), false keeps it to the pill.
public interface IAnnouncer
{
    void Announce(Toast t, bool notify = true);
}
