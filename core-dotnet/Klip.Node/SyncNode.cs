using System.Collections.Concurrent;
using System.Text;
using Klip.Core;

namespace Klip.Node;

/// <summary>Writes received text to the local clipboard.</summary>
public interface IClipboardWriter
{
    void WriteText(string text);
}

/// <summary>mDNS/DNS-SD integration (docs/protocol.md §8.7). Optional.</summary>
public interface IPeerResolver
{
    void Register(string deviceId, int port);
    void Unregister();

    /// <summary>Returns the current IP of <paramref name="deviceId"/>, or null if not found in time.</summary>
    Task<string?> ResolveAsync(string deviceId, CancellationToken ct);
}

public sealed record SendOutcome(OutgoingDecision Decision, IReadOnlyList<PeerSendResult> Results)
{
    public bool AnyOk => Results.Any(r => r.Ok);
}

/// <summary>Something received from another device.</summary>
public sealed record ReceivedItem(string From, string Content, bool IsTest, long AtMs)
{
    /// <summary>Single-line preview for the UI.</summary>
    public string Preview(int max = 120)
    {
        var oneLine = Content.ReplaceLineEndings(" ⏎ ");
        return oneLine.Length <= max ? oneLine : oneLine[..max] + "…";
    }
}

/// <summary>Read-only view of a peer for the UI.</summary>
public sealed record PeerView(string DeviceId, string Name, string Platform, string Host, int Port, bool Enabled, long? LastSeenMs, string? LastStatus);

/// <summary>Server reply produced by a node handler.</summary>
internal readonly record struct Reply(int Status, object? Body = null)
{
    public static Reply Error(int status, string code) => new(status, new ErrorBody(code));
}

/// <summary>
/// One Klip node: config, pairs, anti-loop state, HTTP server and client, pairing, group and radar.
/// Platform-independent; the platform supplies clipboard access, secret protection and (optionally) mDNS.
/// </summary>
public sealed partial class SyncNode : IAsyncDisposable
{
    private readonly ConfigStore _store;
    private readonly NodeConfig _config;
    private readonly ISecretProtector _protector;
    private readonly IClipboardWriter _clipboard;
    private readonly Func<long> _clock;
    private readonly PeerClient _client = new();
    private readonly KlipServer _server;
    private readonly SyncState _state;
    private readonly ConcurrentDictionary<string, PairKeys> _keys = new();
    private readonly ConcurrentDictionary<string, string> _lastStatus = new();
    private readonly Lock _lock = new();

    public RequestVerifier Verifier { get; }

    /// <summary>Optional mDNS resolver.</summary>
    public IPeerResolver? Resolver { get; set; }

    /// <summary>A test message arrived (argument: sender name).</summary>
    public event Action<string>? TestReceived;

    /// <summary>Real content was written to the clipboard (arguments: sender name, content).</summary>
    public event Action<string, string>? ContentReceived;

    /// <summary>Anything (test or real) was received and written to the clipboard.</summary>
    public event Action<ReceivedItem>? Received;

    /// <summary>Last thing received, shown in the UI so the user can see what arrived.</summary>
    public ReceivedItem? LastReceived { get; private set; }

    public event Action? PeersChanged;
    public event Action<string>? LogMessage;

    public SyncNode(ConfigStore store, ISecretProtector protector, IClipboardWriter clipboard, Func<long>? clock = null)
    {
        _store = store;
        _protector = protector;
        _clipboard = clipboard;
        _clock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _config = store.LoadOrCreate();
        _state = new SyncState(_config.DeviceId, _config.Sync);
        foreach (var peer in _config.Peers)
        {
            if (peer.ProtectedSecret is null) continue;
            try
            {
                _keys[peer.DeviceId] = PairKeys.Derive(_protector.Unprotect(Convert.FromBase64String(peer.ProtectedSecret)));
            }
            catch (Exception e)
            {
                Log($"No se pudo descifrar el secreto de {peer.Name}: {e.Message}");
            }
        }
        Verifier = new RequestVerifier(id => _keys.TryGetValue(id, out var k) ? k.MacKey : null, _clock);
        SessionVerifier = new RequestVerifier(SessionMacKey, _clock);
        _server = new KlipServer(this);
    }

    public string DeviceId => _config.DeviceId;
    public string Platform => _config.Platform;
    public int HttpPort => _config.HttpPort;
    public bool IsServerRunning => _server.IsRunning;
    internal long Now => _clock();

    public string DeviceName
    {
        get { lock (_lock) return _config.DeviceName; }
        set { lock (_lock) { _config.DeviceName = value; Persist(); } }
    }

    public bool SyncEnabled
    {
        get { lock (_lock) return _config.SyncEnabled; }
    }

    public bool StartWithWindows
    {
        get { lock (_lock) return _config.StartWithWindows; }
        set { lock (_lock) { _config.StartWithWindows = value; Persist(); } }
    }

    public bool FirewallPromptShown
    {
        get { lock (_lock) return _config.FirewallPromptShown; }
        set { lock (_lock) { _config.FirewallPromptShown = value; Persist(); } }
    }

    /// <summary>Starts the server if sync is enabled and retries pending introductions.</summary>
    public async Task StartAsync()
    {
        if (SyncEnabled) await StartServerAsync();
        _ = DeliverAllPendingAsync();
    }

    /// <summary>Single on/off switch: starts or stops the server and persists the state.</summary>
    public async Task SetSyncEnabledAsync(bool enabled)
    {
        lock (_lock)
        {
            _config.SyncEnabled = enabled;
            Persist();
        }
        if (enabled) await StartServerAsync();
        else await StopServerAsync();
        Log(enabled ? "Sincronización activada" : "Sincronización desactivada");
    }

    private async Task StartServerAsync()
    {
        await _server.StartAsync(HttpPort);
        try { Resolver?.Register(DeviceId, HttpPort); }
        catch (Exception e) { Log($"mDNS: no se pudo registrar el servicio: {e.Message}"); }
    }

    private async Task StopServerAsync()
    {
        try { Resolver?.Unregister(); }
        catch (Exception e) { Log($"mDNS: {e.Message}"); }
        await _server.StopAsync();
    }

    // ---------- Peers ----------

    public IReadOnlyList<PeerView> Peers
    {
        get
        {
            lock (_lock)
            {
                return _config.Peers
                    .Select(p => new PeerView(p.DeviceId, p.Name, p.Platform, p.Host, p.Port, p.Enabled, p.LastSeenMs,
                        _lastStatus.GetValueOrDefault(p.DeviceId)))
                    .ToList();
            }
        }
    }

    /// <summary>Adds or replaces a pair with a known secret (manual pairing in development mode).</summary>
    public void AddOrUpdatePeer(string deviceId, string name, string host, int port, byte[] pairSecret, string platform = "unknown")
    {
        if (deviceId == DeviceId) throw new ArgumentException("No puedes vincular este mismo dispositivo");
        StorePeer(new MemberInfo { DeviceId = deviceId, DeviceName = name, Platform = platform, HttpPort = port, Host = host },
            pairSecret, new SecretVersion(Now, "manual"));
    }

    /// <summary>Creates or replaces a pair, keeping the enabled flag of an existing one.</summary>
    private void StorePeer(MemberInfo info, byte[] secret, SecretVersion version)
    {
        var keys = PairKeys.Derive(secret);
        lock (_lock)
        {
            var existing = _config.Peers.FirstOrDefault(p => p.DeviceId == info.DeviceId);
            _config.Peers.RemoveAll(p => p.DeviceId == info.DeviceId);
            _config.Peers.Add(new PeerConfig
            {
                DeviceId = info.DeviceId,
                Name = info.DeviceName,
                Host = info.Host,
                Port = info.HttpPort,
                Platform = info.Platform,
                Enabled = existing?.Enabled ?? true,
                LastSeenMs = existing?.LastSeenMs,
                ProtectedSecret = Convert.ToBase64String(_protector.Protect(secret)),
                SecretIssuedAt = version.IssuedAt,
                SecretIssuer = version.IssuerId,
            });
            _keys[info.DeviceId] = keys;
            Persist();
        }
        PeersChanged?.Invoke();
    }

    /// <summary>Removes a pair locally without notifying the other side.</summary>
    public void RemovePeer(string deviceId)
    {
        lock (_lock)
        {
            _config.Peers.RemoveAll(p => p.DeviceId == deviceId);
            _config.PendingIntroductions.RemoveAll(i => i.ReceiverId == deviceId || i.Member.DeviceId == deviceId);
            _keys.TryRemove(deviceId, out _);
            _lastStatus.TryRemove(deviceId, out _);
            Persist();
        }
        PeersChanged?.Invoke();
    }

    public void SetPeerEnabled(string deviceId, bool enabled)
    {
        lock (_lock)
        {
            var peer = _config.Peers.FirstOrDefault(p => p.DeviceId == deviceId);
            if (peer is null || peer.Enabled == enabled) return;
            peer.Enabled = enabled;
            Persist();
        }
        PeersChanged?.Invoke();
    }

    private bool TryGetPeer(string deviceId, out PeerConfig peer, out PairKeys keys)
    {
        lock (_lock)
        {
            var p = _config.Peers.FirstOrDefault(x => x.DeviceId == deviceId);
            if (p is not null && _keys.TryGetValue(deviceId, out var k))
            {
                peer = Clone(p);
                keys = k;
                return true;
            }
        }
        peer = null!;
        keys = null!;
        return false;
    }

    private List<(PeerConfig Peer, PairKeys Keys)> PeersWithKeys(Func<PeerConfig, bool> filter)
    {
        lock (_lock)
        {
            return _config.Peers
                .Where(filter)
                .Where(p => _keys.ContainsKey(p.DeviceId))
                .Select(p => (Clone(p), _keys[p.DeviceId]))
                .ToList();
        }
    }

    private void UpdateHost(string deviceId, string host, int? port = null)
    {
        lock (_lock)
        {
            var peer = _config.Peers.FirstOrDefault(p => p.DeviceId == deviceId);
            if (peer is null || (peer.Host == host && (port is null || peer.Port == port))) return;
            Log($"Dirección de {peer.Name} actualizada: {peer.Host}:{peer.Port} -> {host}:{port ?? peer.Port}");
            peer.Host = host;
            if (port is { } p) peer.Port = p;
            Persist();
        }
        PeersChanged?.Invoke();
    }

    // ---------- Outgoing clipboard ----------

    /// <summary>Applies the outgoing rules and sends to every enabled peer (or only <paramref name="onlyDeviceId"/>).</summary>
    public async Task<SendOutcome> SendClipboardAsync(string content, bool isSensitive = false, bool isTest = false, string? onlyDeviceId = null)
    {
        var decision = _state.EvaluateOutgoing(content, isTest, isSensitive, SyncEnabled);
        if (decision != OutgoingDecision.Send) return new SendOutcome(decision, []);

        var targets = PeersWithKeys(p => onlyDeviceId is null ? p.Enabled : p.DeviceId == onlyDeviceId);
        if (targets.Count == 0) return new SendOutcome(decision, []);

        var payload = ClipboardPayload.Text(content, DeviceName, isTest);
        var messageId = Guid.NewGuid().ToString();
        var results = await Task.WhenAll(targets.Select(t => SendWithResolveAsync(t.Peer, t.Keys, payload, messageId)));

        foreach (var r in results)
        {
            _lastStatus[r.DeviceId] = r.Ok ? $"Enviado ({r.HttpStatus} OK)" : r.Error ?? "Error";
        }
        if (!isTest && results.Any(r => r.Ok))
        {
            _state.MarkSent(payload.ContentHash);
            lock (_lock) Persist();
        }
        Log($"Envío {(isTest ? "de prueba " : "")}a {results.Count(r => r.Ok)}/{results.Length} dispositivos");
        PeersChanged?.Invoke();
        return new SendOutcome(decision, results);
    }

    /// <summary>Sends; if the peer is unreachable and mDNS finds a new IP, retries once there (§8.7).</summary>
    private async Task<PeerSendResult> SendWithResolveAsync(PeerConfig peer, PairKeys keys, ClipboardPayload payload, string messageId)
    {
        var result = await _client.SendClipboardAsync(peer, keys, DeviceId, EnvelopeCodec.Seal(keys.EncKey, DeviceId, payload, messageId));
        if (result.Ok || !result.Unreachable || Resolver is null) return result;

        string? ip;
        using (var cts = new CancellationTokenSource(1500))
        {
            try { ip = await Resolver.ResolveAsync(peer.DeviceId, cts.Token); }
            catch (Exception) { ip = null; }
        }
        if (ip is null || ip == peer.Host) return result;
        UpdateHost(peer.DeviceId, ip);
        peer.Host = ip;
        // Same messageId: if the first attempt actually arrived, the receiver ignores the duplicate.
        return await _client.SendClipboardAsync(peer, keys, DeviceId, EnvelopeCodec.Seal(keys.EncKey, DeviceId, payload, messageId));
    }

    public Task<SendOutcome> SendTestAsync(string? onlyDeviceId = null) =>
        SendClipboardAsync($"Prueba de conexión desde {DeviceName} - {DateTime.Now:HH:mm:ss}", isTest: true, onlyDeviceId: onlyDeviceId);

    public async Task<PingResult> PingAsync(string deviceId)
    {
        if (!TryGetPeer(deviceId, out var peer, out var keys)) return new PingResult(false, 0, null, null, "Dispositivo desconocido");
        var result = await _client.PingAsync(peer.Host, peer.Port, keys, DeviceId);
        _lastStatus[deviceId] = result.Ok ? $"Conectado ({result.ElapsedMs} ms)" : result.Error ?? "Error";
        if (result.Ok) MarkSeen(deviceId, null);
        else PeersChanged?.Invoke();
        return result;
    }

    // ---------- Incoming clipboard (called by KlipServer after signature verification) ----------

    internal Reply ReceiveClipboard(string deviceId, byte[] body, string? remoteIp)
    {
        if (!_keys.TryGetValue(deviceId, out var keys)) return new Reply(403);
        ClipboardPayload payload;
        Envelope envelope;
        try
        {
            envelope = EnvelopeCodec.Decode(Encoding.UTF8.GetString(body));
            if (envelope.OriginDeviceId != deviceId) return new Reply(400);
            payload = EnvelopeCodec.Open(keys.EncKey, envelope);
        }
        catch (EnvelopeException e)
        {
            Log($"Mensaje rechazado de {deviceId}: {e.Kind}");
            return new Reply(400);
        }

        var decision = _state.AcceptIncoming(envelope.OriginDeviceId, envelope.MessageId, payload.ContentHash);
        if (decision != IncomingDecision.Accept) return new Reply(409);

        MarkSeen(deviceId, remoteIp, notify: false);
        try
        {
            _clipboard.WriteText(payload.Content);
        }
        catch (Exception e)
        {
            Log($"No se pudo escribir en el portapapeles: {e.Message}");
        }
        lock (_lock) Persist();

        Log($"Recibido de {payload.OriginDeviceName}{(payload.IsTest ? " (prueba)" : "")}: {payload.Content.Length} caracteres");
        var item = new ReceivedItem(payload.OriginDeviceName, payload.Content, payload.IsTest, Now);
        LastReceived = item;
        Received?.Invoke(item);
        if (payload.IsTest) TestReceived?.Invoke(payload.OriginDeviceName);
        else ContentReceived?.Invoke(payload.OriginDeviceName, payload.Content);
        PeersChanged?.Invoke();
        return new Reply(200);
    }

    internal PingInfo PingInfo => new(DeviceId, DeviceName, Platform);

    /// <summary>
    /// Records verified contact with a peer and, if given, its current IP (passive IP update, plan §5.3).
    /// Also retries pending group introductions for it.
    /// </summary>
    internal void MarkSeen(string deviceId, string? remoteIp, bool notify = true)
    {
        bool hasPending;
        lock (_lock)
        {
            var peer = _config.Peers.FirstOrDefault(p => p.DeviceId == deviceId);
            if (peer is null) return;
            peer.LastSeenMs = Now;
            if (!string.IsNullOrEmpty(remoteIp) && peer.Host != remoteIp)
            {
                Log($"IP de {peer.Name} actualizada: {peer.Host} -> {remoteIp}");
                peer.Host = remoteIp;
            }
            hasPending = _config.PendingIntroductions.Any(i => i.ReceiverId == deviceId);
            Persist();
        }
        if (hasPending) _ = DeliverPendingAsync(deviceId);
        if (notify) PeersChanged?.Invoke();
    }

    internal void Log(string message) => LogMessage?.Invoke(message);

    private void Persist()
    {
        _config.Sync = _state.Snapshot();
        _store.Save(_config);
    }

    private static PeerConfig Clone(PeerConfig p) => new()
    {
        DeviceId = p.DeviceId,
        Name = p.Name,
        Platform = p.Platform,
        Host = p.Host,
        Port = p.Port,
        Enabled = p.Enabled,
        ProtectedSecret = p.ProtectedSecret,
        LastSeenMs = p.LastSeenMs,
        SecretIssuedAt = p.SecretIssuedAt,
        SecretIssuer = p.SecretIssuer,
    };

    public async ValueTask DisposeAsync()
    {
        StopDiscovery();
        await StopServerAsync();
        _client.Dispose();
    }
}
