using System.Security.Principal;

namespace NotchTasks.Services;

/// The per-user single-instance guard: a named mutex, Local\NotchTasks-<user SID>. The first launch owns it for its lifetime.
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;

    public SingleInstance()
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        _mutex = new Mutex(initiallyOwned: true, name: $@"Local\NotchTasks-{sid}", createdNew: out bool createdNew);
        IsFirst = createdNew;
    }

    /// True when this process is the first instance of the user's session.
    public bool IsFirst { get; }

    public void Dispose() => _mutex.Dispose();
}
