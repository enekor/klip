using System.Net;
using System.Runtime.InteropServices;
using Klip.Core;


namespace Klip.Node;

/// <summary>
/// mDNS/DNS-SD with the native Windows API (dnsapi.dll, Windows 10 1809+), so it does not compete for UDP 5353
/// with the system's own responder. Announces <c>&lt;deviceId&gt;._klip._tcp.local</c> and resolves peers (§8.7).
/// Every native callback catches all exceptions: an exception escaping into native code would kill the process.
/// </summary>
/// <param name="log">Diagnostics sink.</param>
/// <param name="preferredIPv4">
/// Address to announce. Without it Windows may announce a virtual adapter (Hyper-V/WSL) that phones cannot reach.
/// </param>
public sealed class WindowsDnsSd(Action<string> log, Func<IPAddress?>? preferredIPv4 = null) : IPeerResolver, IDisposable
{
    private const uint DnsQueryRequestVersion1 = 1;
    private const uint DnsRequestPending = 9506;

    private readonly Lock _lock = new();
    private RegisterComplete? _registerCallback; // kept alive while registered
    private IntPtr _registerRequest;
    private IntPtr _instance;
    private IntPtr _instanceName;
    private IntPtr _hostName;
    private IntPtr _ip4;

    public void Register(string deviceId, int port)
    {
        lock (_lock)
        {
            if (_registerRequest != IntPtr.Zero) return;
            try
            {
                _instanceName = Marshal.StringToHGlobalUni($"{deviceId}.{Protocol.MdnsServiceType}.local");
                _hostName = Marshal.StringToHGlobalUni($"{Dns.GetHostName()}.local");
                if (preferredIPv4?.Invoke() is { AddressFamily: System.Net.Sockets.AddressFamily.InterNetwork } ip)
                {
                    // IP4_ADDRESS: a DWORD holding the address in network byte order.
                    _ip4 = Marshal.AllocHGlobal(4);
                    Marshal.Copy(ip.GetAddressBytes(), 0, _ip4, 4);
                    log($"mDNS: anunciando {ip}");
                }
                _instance = DnsServiceConstructInstance(_instanceName, _hostName, _ip4, IntPtr.Zero, (ushort)port, 0, 0, 0, IntPtr.Zero, IntPtr.Zero);
                if (_instance == IntPtr.Zero) throw new InvalidOperationException("DnsServiceConstructInstance failed");

                _registerCallback = (status, _, _) =>
                {
                    // The instance pointer is not freed here: it may alias the one we constructed (freed on Unregister).
                    try { log(status == 0 ? "mDNS: registro actualizado" : $"mDNS: registro falló ({status})"); }
                    catch { /* never let an exception reach native code */ }
                };
                var request = new DnsServiceRegisterRequest
                {
                    Version = DnsQueryRequestVersion1,
                    InterfaceIndex = 0,
                    pServiceInstance = _instance,
                    pRegisterCompletionCallback = Marshal.GetFunctionPointerForDelegate(_registerCallback),
                };
                _registerRequest = Marshal.AllocHGlobal(Marshal.SizeOf<DnsServiceRegisterRequest>());
                Marshal.StructureToPtr(request, _registerRequest, false);
                var rc = DnsServiceRegister(_registerRequest, IntPtr.Zero);
                if (rc != DnsRequestPending) throw new InvalidOperationException($"DnsServiceRegister returned {rc}");
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
            {
                log($"mDNS no disponible: {e.Message}");
                FreeRegistration();
            }
        }
    }

    public void Unregister()
    {
        lock (_lock)
        {
            if (_registerRequest == IntPtr.Zero) return;
            try { DnsServiceDeRegister(_registerRequest, IntPtr.Zero); }
            catch (Exception e) { log($"mDNS: {e.Message}"); }

            // Deregistration completes asynchronously and may still read the request and fire the callback:
            // release everything a few seconds later.
            var (request, instance, name, host, ip4, callback) = (_registerRequest, _instance, _instanceName, _hostName, _ip4, _registerCallback);
            _registerRequest = _instance = _instanceName = _hostName = _ip4 = IntPtr.Zero;
            _registerCallback = null;
            _ = Task.Delay(5_000).ContinueWith(_ =>
            {
                GC.KeepAlive(callback);
                Marshal.FreeHGlobal(request);
                DnsServiceFreeInstance(instance);
                Marshal.FreeHGlobal(name);
                Marshal.FreeHGlobal(host);
                if (ip4 != IntPtr.Zero) Marshal.FreeHGlobal(ip4);
            }, TaskScheduler.Default);
        }
    }

    public async Task<string?> ResolveAsync(string deviceId, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        ResolveComplete callback = (status, _, instance) =>
        {
            try
            {
                string? ip = null;
                if (status == 0 && instance != IntPtr.Zero)
                {
                    var inst = Marshal.PtrToStructure<DnsServiceInstance>(instance);
                    if (inst.ip4Address != IntPtr.Zero) ip = new IPAddress((uint)Marshal.ReadInt32(inst.ip4Address)).ToString();
                }
                if (instance != IntPtr.Zero) DnsServiceFreeInstance(instance);
                tcs.TrySetResult(ip);
            }
            catch
            {
                tcs.TrySetResult(null);
            }
        };

        var queryName = Marshal.StringToHGlobalUni($"{deviceId}.{Protocol.MdnsServiceType}.local");
        var request = Marshal.AllocHGlobal(Marshal.SizeOf<DnsServiceResolveRequest>());
        var cancel = Marshal.AllocHGlobal(IntPtr.Size);
        Marshal.WriteIntPtr(cancel, IntPtr.Zero);
        var started = false;
        try
        {
            Marshal.StructureToPtr(new DnsServiceResolveRequest
            {
                Version = DnsQueryRequestVersion1,
                InterfaceIndex = 0,
                QueryName = queryName,
                pResolveCompletionCallback = Marshal.GetFunctionPointerForDelegate(callback),
            }, request, false);
            if (DnsServiceResolve(request, cancel) != DnsRequestPending) return null;
            started = true;
            await using (ct.Register(() => tcs.TrySetResult(null)))
            {
                return await tcs.Task;
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
        finally
        {
            // Not resolved (timeout or failure): stop the query. A completed query needs no cancel.
            if (started && (!tcs.Task.IsCompleted || tcs.Task.Result is null))
            {
                try { DnsServiceResolveCancel(cancel); } catch { /* best effort */ }
            }
            // Give a late callback time to run before releasing its memory and delegate.
            _ = Task.Delay(5_000).ContinueWith(_ =>
            {
                GC.KeepAlive(callback);
                Marshal.FreeHGlobal(queryName);
                Marshal.FreeHGlobal(request);
                Marshal.FreeHGlobal(cancel);
            }, TaskScheduler.Default);
        }
    }

    /// <summary>Frees a registration that never reached the native API (setup failed).</summary>
    private void FreeRegistration()
    {
        if (_registerRequest != IntPtr.Zero) Marshal.FreeHGlobal(_registerRequest);
        if (_instance != IntPtr.Zero) DnsServiceFreeInstance(_instance);
        if (_instanceName != IntPtr.Zero) Marshal.FreeHGlobal(_instanceName);
        if (_hostName != IntPtr.Zero) Marshal.FreeHGlobal(_hostName);
        if (_ip4 != IntPtr.Zero) Marshal.FreeHGlobal(_ip4);
        _registerRequest = _instance = _instanceName = _hostName = _ip4 = IntPtr.Zero;
        _registerCallback = null;
    }

    public void Dispose() => Unregister();

    // ---------- dnsapi.dll ----------

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void RegisterComplete(uint status, IntPtr queryContext, IntPtr instance);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void ResolveComplete(uint status, IntPtr queryContext, IntPtr instance);

    [StructLayout(LayoutKind.Sequential)]
    private struct DnsServiceInstance
    {
        public IntPtr pszInstanceName;
        public IntPtr pszHostName;
        public IntPtr ip4Address;
        public IntPtr ip6Address;
        public ushort wPort;
        public ushort wPriority;
        public ushort wWeight;
        public uint dwPropertyCount;
        public IntPtr keys;
        public IntPtr values;
        public uint dwInterfaceIndex;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DnsServiceRegisterRequest
    {
        public uint Version;
        public uint InterfaceIndex;
        public IntPtr pServiceInstance;
        public IntPtr pRegisterCompletionCallback;
        public IntPtr pQueryContext;
        public IntPtr hCredentials;
        public int unicastEnabled;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DnsServiceResolveRequest
    {
        public uint Version;
        public uint InterfaceIndex;
        public IntPtr QueryName;
        public IntPtr pResolveCompletionCallback;
        public IntPtr pQueryContext;
    }

    [DllImport("dnsapi.dll")]
    private static extern IntPtr DnsServiceConstructInstance(IntPtr pServiceName, IntPtr pHostName, IntPtr pIp4, IntPtr pIp6,
        ushort wPort, ushort wPriority, ushort wWeight, uint dwPropertiesCount, IntPtr keys, IntPtr values);

    [DllImport("dnsapi.dll")] private static extern void DnsServiceFreeInstance(IntPtr pInstance);
    [DllImport("dnsapi.dll")] private static extern uint DnsServiceRegister(IntPtr pRequest, IntPtr pCancel);
    [DllImport("dnsapi.dll")] private static extern uint DnsServiceDeRegister(IntPtr pRequest, IntPtr pCancel);
    [DllImport("dnsapi.dll")] private static extern uint DnsServiceResolve(IntPtr pRequest, IntPtr pCancel);
    [DllImport("dnsapi.dll")] private static extern uint DnsServiceResolveCancel(IntPtr pCancelHandle);
}
