using NotchTasks.Core.Notch;

namespace NotchTasks.Core.Modules;

/// Where a module's toasts go: the app plays the toast's sound and queues the toast. notify is kept for system
/// notifications, which are not built yet, so it is ignored for now.
public interface IAnnouncer
{
    void Announce(Toast t, bool notify = true);
}
