namespace NotchTasks.Core.Modules;

/// A module's feed: the small piece in the app that reads the OS while its module is on. Start and Stop run on the UI
/// thread. A source is started when its module turns on, and stopped when it turns off or the app exits.
public interface IModuleSource
{
    void Start();

    void Stop();
}
