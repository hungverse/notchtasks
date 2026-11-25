using System;
using System.Net;
using System.Runtime.InteropServices;
using NotchTasks.Core.Sync;

namespace NotchTasks.Services.Sync;

/// Advertises the LAN listener through Windows' own DNS-SD (dnsapi.dll DnsServiceRegister, Windows 10 1809 and later).
/// Publish and Unpublish swallow and log failures. A missing entry point (an old Windows) is logged once; the LAN still
/// listens, and phones reach the PC through the relay.
public sealed class DnsSdAdvertiser : IServiceAdvertiser
{
    private const string ServiceSuffix = "._notchtasks._tcp.local";
    private const uint DnsRequestPending = 9506;

    private readonly Action<string> _log;
    private readonly object _gate = new();
    private Registration? _current;
    private bool _unavailableLogged;
    private bool _registrationLogged;
    private readonly object _logGate = new();

    public DnsSdAdvertiser()
        : this(text => ErrorLog.Write(new InvalidOperationException(text)))
    {
    }

    internal DnsSdAdvertiser(Action<string> log) => _log = log;

    public void Publish(string instanceName, int port)
    {
        lock (_gate)
        {
            UnpublishLocked();
            var registration = new Registration(LogRegistrationOnce);
            try
            {
                registration.Prepare($"{instanceName}{ServiceSuffix}", $"{Dns.GetHostName()}.local", port);
                var status = DnsSdNative.DnsServiceRegister(registration.Request, IntPtr.Zero);
                if (status is not 0 and not DnsRequestPending)
                {
                    LogRegistrationOnce($"mDNS: registration refused (status {status})");
                    registration.Release();
                    return;
                }
                _current = registration;
            }
            catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException)
            {
                registration.Release();
                LogUnavailableOnce();
            }
            catch (Exception e)
            {
                registration.Release();
                LogRegistrationOnce($"mDNS: registration failed ({e.GetType().Name})");
            }
        }
    }

    public void Unpublish()
    {
        lock (_gate)
        {
            UnpublishLocked();
        }
    }

    public void Dispose() => Unpublish();

    /// Deregisters the current registration. Its memory is freed by the deregistration callback (or at once when the call fails).
    private void UnpublishLocked()
    {
        var registration = _current;
        if (registration is null)
        {
            return;
        }
        _current = null;
        try
        {
            var status = registration.Deregister();
            if (status is null)
            {
                return; // a registration that failed was freed when it failed: nothing to deregister
            }
            if (status.Value == DnsRequestPending)
            {
                return; // the deregistration callback frees it
            }
            if (status.Value != 0)
            {
                _log($"mDNS: deregistration refused (status {status.Value})");
            }
            registration.Release(); // dnsapi is done with it at once (sync status), so no callback will come
        }
        catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException)
        {
            registration.Release();
            LogUnavailableOnce();
        }
        catch (Exception e)
        {
            registration.Release();
            _log($"mDNS: deregistration failed ({e.GetType().Name})");
        }
    }

    /// A refused or failed registration is logged once per advertiser (P4-R5), however many times Publish is tried.
    /// Its own lock: a callback can run on the thread that holds _gate.
    private void LogRegistrationOnce(string text)
    {
        lock (_logGate)
        {
            if (_registrationLogged)
            {
                return;
            }
            _registrationLogged = true;
        }
        _log(text);
    }

    private void LogUnavailableOnce()
    {
        if (_unavailableLogged)
        {
            return;
        }
        _unavailableLogged = true;
        _log("mDNS: DnsServiceRegister is not available on this Windows; phones reach this PC through the relay");
    }

    /// The unmanaged state of one registration. dnsapi keeps the pointers until the deregistration completes, so they are
    /// allocated outside the GC heap; the context handle links the callbacks back to this object for logging and freeing.
    internal sealed class Registration(Action<string> log)
    {
        private nint _instanceName;
        private nint _hostName;
        private nint _instance;
        private GCHandle _context;
        private readonly object _state = new(); // the memory is freed once, by whichever side finishes last
        private bool _freed;

        /// The DNS_SERVICE_REGISTER_REQUEST in unmanaged memory (what dnsapi is given).
        public nint Request;

        public void Prepare(string fullName, string hostName, int port)
        {
            _context = GCHandle.Alloc(this);
            _instanceName = Marshal.StringToHGlobalUni(fullName);
            _hostName = Marshal.StringToHGlobalUni(hostName);
            _instance = Marshal.AllocHGlobal(Marshal.SizeOf<DnsSdNative.ServiceInstance>());
            Marshal.StructureToPtr(new DnsSdNative.ServiceInstance
            {
                InstanceName = _instanceName,
                HostName = _hostName,
                Port = (ushort)port,
            }, _instance, fDeleteOld: false);
            Request = Marshal.AllocHGlobal(Marshal.SizeOf<DnsSdNative.RegisterRequest>());
            WriteRequest(DnsSdNative.RegisterCallbackPointer);
        }

        /// Starts the deregistration with dnsapi. Returns its status, or null when the memory is already freed (the registration
        /// failed, so there is nothing to deregister).
        public uint? Deregister()
        {
            lock (_state)
            {
                if (_freed)
                {
                    return null;
                }
                WriteRequest(DnsSdNative.DeregisterCallbackPointer);
                return DnsSdNative.DnsServiceDeRegister(Request, IntPtr.Zero);
            }
        }

        public void Log(string text) => log(text);

        public static Registration? FromContext(nint context) =>
            context == IntPtr.Zero ? null : GCHandle.FromIntPtr(context).Target as Registration;

        /// Frees the unmanaged memory and the context handle. Called once the native side is done with them. Safe to call twice.
        public void Release()
        {
            lock (_state)
            {
                if (_freed)
                {
                    return;
                }
                _freed = true;
                if (_context.IsAllocated)
                {
                    _context.Free();
                }
                FreeIfSet(ref Request);
                FreeIfSet(ref _instance);
                FreeIfSet(ref _instanceName);
                FreeIfSet(ref _hostName);
            }
        }

        private void WriteRequest(nint callback) =>
            Marshal.StructureToPtr(new DnsSdNative.RegisterRequest
            {
                Version = 1,
                InterfaceIndex = 0,
                ServiceInstance = _instance,
                Callback = callback,
                Context = GCHandle.ToIntPtr(_context),
                Credentials = IntPtr.Zero,
                UnicastEnabled = 0,
            }, Request, fDeleteOld: false);

        private static void FreeIfSet(ref nint pointer)
        {
            if (pointer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(pointer);
                pointer = IntPtr.Zero;
            }
        }
    }
}

/// The dnsapi.dll calls and their structures (DNS_SERVICE_INSTANCE, DNS_SERVICE_REGISTER_REQUEST). The callbacks are static
/// delegates so the native side always has a live target.
internal static partial class DnsSdNative
{
    public delegate void CompletionCallback(uint status, nint context, nint instance);

    private static readonly CompletionCallback RegisterComplete = OnRegisterComplete;
    private static readonly CompletionCallback DeregisterComplete = OnDeregisterComplete;

    public static readonly nint RegisterCallbackPointer = Marshal.GetFunctionPointerForDelegate(RegisterComplete);
    public static readonly nint DeregisterCallbackPointer = Marshal.GetFunctionPointerForDelegate(DeregisterComplete);

    [LibraryImport("dnsapi.dll", EntryPoint = "DnsServiceRegister")]
    public static partial uint DnsServiceRegister(nint request, nint cancel);

    [LibraryImport("dnsapi.dll", EntryPoint = "DnsServiceDeRegister")]
    public static partial uint DnsServiceDeRegister(nint request, nint cancel);

    [LibraryImport("dnsapi.dll", EntryPoint = "DnsServiceFreeInstance")]
    public static partial void DnsServiceFreeInstance(nint instance);

    // Callbacks run on a dnsapi thread. An exception that escapes into native code would end the process, so nothing escapes.
    private static void OnRegisterComplete(uint status, nint context, nint instance)
    {
        try
        {
            if (instance != IntPtr.Zero)
            {
                DnsServiceFreeInstance(instance); // dnsapi's copy of the instance is ours to free
            }
            if (status != 0)
            {
                // a failed registration gets no deregistration, so its memory is released here
                var registration = DnsSdAdvertiser.Registration.FromContext(context);
                registration?.Log($"mDNS: registration failed (status {status})");
                registration?.Release();
            }
        }
        catch (Exception)
        {
            // Swallowed: the advertisement is best effort and the LAN listener still runs.
        }
    }

    private static void OnDeregisterComplete(uint status, nint context, nint instance)
    {
        try
        {
            DnsSdAdvertiser.Registration.FromContext(context)?.Release();
        }
        catch (Exception)
        {
            // Swallowed: a native thread must not see an exception.
        }
    }

    /// DNS_SERVICE_INSTANCE: the instance and host names, the port (the WORD fields), and no addresses or TXT keys.
    [StructLayout(LayoutKind.Sequential)]
    internal struct ServiceInstance
    {
        public nint InstanceName;
        public nint HostName;
        public nint Ip4Address;
        public nint Ip6Address;
        public ushort Port;
        public ushort Priority;
        public ushort Weight;
        public uint PropertiesCount;
        public nint Keys;
        public nint Values;
        public uint InterfaceIndex;
    }

    /// DNS_SERVICE_REGISTER_REQUEST: version 1, the callback and its context, no credentials, unicast off.
    [StructLayout(LayoutKind.Sequential)]
    internal struct RegisterRequest
    {
        public uint Version;
        public uint InterfaceIndex;
        public nint ServiceInstance;
        public nint Callback;
        public nint Context;
        public nint Credentials;
        public int UnicastEnabled;
    }
}
