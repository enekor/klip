using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using Klip.Core;

namespace Klip.Node;

/// <summary>A device seen by the radar that is not paired yet.</summary>
public sealed record DiscoveredDevice(string DeviceId, string DeviceName, string Platform, string Host, int HttpPort, bool PairingOpen, long LastSeenMs);

/// <summary>Radar settings. Defaults follow docs/protocol.md §8.6; tests override ports and targets.</summary>
public sealed record DiscoveryOptions
{
    public int ListenPort { get; init; } = Protocol.DiscoveryPort;
    public int SendPort { get; init; } = Protocol.DiscoveryPort;

    /// <summary>Destination addresses; null = 255.255.255.255 plus every interface's broadcast address.</summary>
    public Func<IEnumerable<IPAddress>>? Targets { get; init; }

    public int IntervalMs { get; init; } = Protocol.BeaconIntervalMs;
}

/// <summary>UDP broadcast sender/receiver for the radar. Active only while the configuration UI is visible.</summary>
internal sealed class DiscoveryService : IDisposable
{
    private readonly DiscoveryOptions _options;
    private readonly Func<Beacon> _beacon;
    private readonly Action<Beacon, string> _onBeacon;
    private readonly Action<string> _log;
    private readonly UdpClient _udp;
    private readonly CancellationTokenSource _cts = new();

    public DiscoveryService(DiscoveryOptions options, Func<Beacon> beacon, Action<Beacon, string> onBeacon, Action<string> log)
    {
        _options = options;
        _beacon = beacon;
        _onBeacon = onBeacon;
        _log = log;
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        // Several Klip processes on one machine (e.g. Klip + DevPeer) must be able to listen at once.
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        socket.EnableBroadcast = true;
        socket.Bind(new IPEndPoint(IPAddress.Any, options.ListenPort));
        _udp = new UdpClient { Client = socket };
        _ = Task.Run(SendLoopAsync);
        _ = Task.Run(ReceiveLoopAsync);
    }

    private async Task SendLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            var bytes = ProtocolJson.Serialize(_beacon());
            foreach (var target in (_options.Targets ?? BroadcastAddresses)())
            {
                try { await _udp.SendAsync(bytes, new IPEndPoint(target, _options.SendPort), _cts.Token); }
                catch (OperationCanceledException) { return; }
                catch (SocketException) { /* interface went away: ignore */ }
                catch (ObjectDisposedException) { return; }
            }
            try { await Task.Delay(_options.IntervalMs, _cts.Token); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task ReceiveLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try { result = await _udp.ReceiveAsync(_cts.Token); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { continue; }

            if (result.Buffer.Length > 2048) continue;
            Beacon beacon;
            try { beacon = ProtocolJson.Deserialize<Beacon>(result.Buffer); }
            catch (JsonException) { continue; }
            if (beacon.Klip != 1 || string.IsNullOrWhiteSpace(beacon.DeviceId) || beacon.HttpPort is < 1 or > 65535) continue;

            var ip = result.RemoteEndPoint.Address;
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            try { _onBeacon(beacon, ip.ToString()); }
            catch (Exception e) { _log($"Radar: {e.Message}"); }
        }
    }

    /// <summary>255.255.255.255 plus the directed broadcast address of each active IPv4 interface.</summary>
    public static IEnumerable<IPAddress> BroadcastAddresses()
    {
        var result = new HashSet<IPAddress> { IPAddress.Broadcast };
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask is null) continue;
                var ip = ua.Address.GetAddressBytes();
                var mask = ua.IPv4Mask.GetAddressBytes();
                var bcast = new byte[4];
                for (var i = 0; i < 4; i++) bcast[i] = (byte)(ip[i] | ~mask[i]);
                result.Add(new IPAddress(bcast));
            }
        }
        return result;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _udp.Dispose();
        _cts.Dispose();
    }
}

public sealed partial class SyncNode
{
    private readonly ConcurrentDictionary<string, DiscoveredDevice> _discovered = new();
    private readonly ConcurrentDictionary<string, byte> _verifyingHost = new();
    private DiscoveryService? _discovery;
    private Timer? _discoveryExpiry;

    /// <summary>The radar list changed.</summary>
    public event Action? DiscoveredChanged;

    public IReadOnlyList<DiscoveredDevice> Discovered =>
        _discovered.Values.OrderBy(d => d.DeviceName, StringComparer.CurrentCultureIgnoreCase).ToList();

    public bool IsDiscovering => _discovery is not null;

    /// <summary>Starts the radar (call when the configuration UI becomes visible).</summary>
    public void StartDiscovery(DiscoveryOptions? options = null)
    {
        if (_discovery is not null) return;
        try
        {
            _discovery = new DiscoveryService(options ?? new DiscoveryOptions(), CurrentBeacon, OnBeacon, Log);
        }
        catch (SocketException e)
        {
            Log($"Radar: no se pudo abrir el puerto UDP: {e.Message}");
            return;
        }
        _discoveryExpiry = new Timer(_ => ExpireDiscovered(), null, 1000, 1000);
    }

    /// <summary>Stops the radar and clears the list (call when the configuration UI is hidden).</summary>
    public void StopDiscovery()
    {
        _discoveryExpiry?.Dispose();
        _discoveryExpiry = null;
        _discovery?.Dispose();
        _discovery = null;
        if (!_discovered.IsEmpty)
        {
            _discovered.Clear();
            DiscoveredChanged?.Invoke();
        }
    }

    private Beacon CurrentBeacon() => new()
    {
        Klip = 1,
        DeviceId = DeviceId,
        DeviceName = DeviceName,
        Platform = Platform,
        HttpPort = HttpPort,
        PairingOpen = PairingOpen,
    };

    private void OnBeacon(Beacon b, string ip)
    {
        if (b.DeviceId == DeviceId) return;

        if (TryGetPeer(b.DeviceId, out var peer, out var keys))
        {
            if (_discovered.TryRemove(b.DeviceId, out _)) DiscoveredChanged?.Invoke();
            if ((peer.Host != ip || peer.Port != b.HttpPort) && _verifyingHost.TryAdd(b.DeviceId, 0))
            {
                // A beacon is not authenticated: confirm the new address with a signed ping first.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var ping = await _client.PingAsync(ip, b.HttpPort, keys, DeviceId);
                        if (ping.Ok && ping.Info?.DeviceId == b.DeviceId) UpdateHost(b.DeviceId, ip, b.HttpPort);
                    }
                    finally
                    {
                        _verifyingHost.TryRemove(b.DeviceId, out _);
                    }
                });
            }
            return;
        }

        var device = new DiscoveredDevice(b.DeviceId, b.DeviceName, b.Platform, ip, b.HttpPort, b.PairingOpen, Now);
        var changed = !_discovered.TryGetValue(b.DeviceId, out var old) || old with { LastSeenMs = device.LastSeenMs } != device;
        _discovered[b.DeviceId] = device;
        if (changed) DiscoveredChanged?.Invoke();
    }

    private void ExpireDiscovered()
    {
        var removed = false;
        foreach (var (id, d) in _discovered)
        {
            if (Now - d.LastSeenMs > Protocol.DiscoveryExpiryMs && _discovered.TryRemove(id, out _)) removed = true;
        }
        if (removed) DiscoveredChanged?.Invoke();
    }
}
