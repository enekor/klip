using System.Collections.Concurrent;
using System.Text;
using Klip.Core;

namespace Klip.Node;

/// <summary>Writes received text to the local clipboard.</summary>
public interface IClipboardWriter
{
    void WriteText(string text);
}

public sealed record SendOutcome(OutgoingDecision Decision, IReadOnlyList<PeerSendResult> Results)
{
    public bool AnyOk => Results.Any(r => r.Ok);
}

/// <summary>Read-only view of a peer for the UI.</summary>
public sealed record PeerView(string DeviceId, string Name, string Platform, string Host, int Port, bool Enabled, long? LastSeenMs, string? LastStatus);

/// <summary>
/// One Klip node: config, pairs, anti-loop state, HTTP server and client. Platform-independent;
/// the platform supplies clipboard access and secret protection.
/// </summary>
public sealed class SyncNode : IAsyncDisposable
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

    /// <summary>A test message arrived (argument: sender name).</summary>
    public event Action<string>? TestReceived;

    /// <summary>Real content was written to the clipboard (arguments: sender name, content).</summary>
    public event Action<string, string>? ContentReceived;

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
        _server = new KlipServer(this);
    }

    public string DeviceId => _config.DeviceId;
    public string Platform => _config.Platform;
    public int HttpPort => _config.HttpPort;
    public bool IsServerRunning => _server.IsRunning;

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

    /// <summary>Starts the server if sync is enabled.</summary>
    public async Task StartAsync()
    {
        if (SyncEnabled) await _server.StartAsync(HttpPort);
    }

    /// <summary>Single on/off switch: starts or stops the server and persists the state.</summary>
    public async Task SetSyncEnabledAsync(bool enabled)
    {
        lock (_lock)
        {
            _config.SyncEnabled = enabled;
            Persist();
        }
        if (enabled) await _server.StartAsync(HttpPort);
        else await _server.StopAsync();
        Log(enabled ? "Sincronización activada" : "Sincronización desactivada");
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
        var keys = PairKeys.Derive(pairSecret);
        lock (_lock)
        {
            _config.Peers.RemoveAll(p => p.DeviceId == deviceId);
            _config.Peers.Add(new PeerConfig
            {
                DeviceId = deviceId,
                Name = name,
                Host = host,
                Port = port,
                Platform = platform,
                ProtectedSecret = Convert.ToBase64String(_protector.Protect(pairSecret)),
            });
            _keys[deviceId] = keys;
            Persist();
        }
        PeersChanged?.Invoke();
    }

    public void RemovePeer(string deviceId)
    {
        lock (_lock)
        {
            _config.Peers.RemoveAll(p => p.DeviceId == deviceId);
            _keys.TryRemove(deviceId, out _);
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

    // ---------- Outgoing ----------

    /// <summary>Applies the outgoing rules and sends to every enabled peer (or only <paramref name="onlyDeviceId"/>).</summary>
    public async Task<SendOutcome> SendClipboardAsync(string content, bool isSensitive = false, bool isTest = false, string? onlyDeviceId = null)
    {
        var decision = _state.EvaluateOutgoing(content, isTest, isSensitive, SyncEnabled);
        if (decision != OutgoingDecision.Send) return new SendOutcome(decision, []);

        List<(PeerConfig Peer, PairKeys Keys)> targets;
        lock (_lock)
        {
            targets = _config.Peers
                .Where(p => onlyDeviceId is null ? p.Enabled : p.DeviceId == onlyDeviceId)
                .Where(p => _keys.ContainsKey(p.DeviceId))
                .Select(p => (Clone(p), _keys[p.DeviceId]))
                .ToList();
        }
        if (targets.Count == 0) return new SendOutcome(decision, []);

        var payload = ClipboardPayload.Text(content, DeviceName, isTest);
        var messageId = Guid.NewGuid().ToString();
        var results = await Task.WhenAll(targets.Select(t =>
            _client.SendClipboardAsync(t.Peer, t.Keys, DeviceId, EnvelopeCodec.Seal(t.Keys.EncKey, DeviceId, payload, messageId))));

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

    public Task<SendOutcome> SendTestAsync(string? onlyDeviceId = null) =>
        SendClipboardAsync($"Prueba de conexión desde {DeviceName} - {DateTime.Now:HH:mm:ss}", isTest: true, onlyDeviceId: onlyDeviceId);

    public async Task<PingResult> PingAsync(string deviceId)
    {
        PeerConfig peer;
        PairKeys? keys;
        lock (_lock)
        {
            var p = _config.Peers.FirstOrDefault(x => x.DeviceId == deviceId) ?? throw new ArgumentException("Dispositivo desconocido");
            peer = Clone(p);
            _keys.TryGetValue(deviceId, out keys);
        }
        if (keys is null) return new PingResult(false, 0, null, null, "Sin secreto de pareja");
        var result = await _client.PingAsync(peer, keys, DeviceId);
        _lastStatus[deviceId] = result.Ok ? $"Conectado ({result.ElapsedMs} ms)" : result.Error ?? "Error";
        if (result.Ok) MarkSeen(deviceId, null);
        else PeersChanged?.Invoke();
        return result;
    }

    // ---------- Incoming (called by KlipServer after signature verification) ----------

    internal int ReceiveClipboard(string deviceId, byte[] body, string? remoteIp)
    {
        if (!_keys.TryGetValue(deviceId, out var keys)) return 403;
        ClipboardPayload payload;
        Envelope envelope;
        try
        {
            envelope = EnvelopeCodec.Decode(Encoding.UTF8.GetString(body));
            if (envelope.OriginDeviceId != deviceId) return 400;
            payload = EnvelopeCodec.Open(keys.EncKey, envelope);
        }
        catch (EnvelopeException e)
        {
            Log($"Mensaje rechazado de {deviceId}: {e.Kind}");
            return 400;
        }

        var decision = _state.AcceptIncoming(envelope.OriginDeviceId, envelope.MessageId, payload.ContentHash);
        if (decision != IncomingDecision.Accept) return 409;

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
        if (payload.IsTest) TestReceived?.Invoke(payload.OriginDeviceName);
        else ContentReceived?.Invoke(payload.OriginDeviceName, payload.Content);
        PeersChanged?.Invoke();
        return 200;
    }

    /// <summary>Records contact with a peer and, if given, its current IP (passive IP update, plan §5.3).</summary>
    internal void MarkSeen(string deviceId, string? remoteIp, bool notify = true)
    {
        lock (_lock)
        {
            var peer = _config.Peers.FirstOrDefault(p => p.DeviceId == deviceId);
            if (peer is null) return;
            peer.LastSeenMs = _clock();
            if (!string.IsNullOrEmpty(remoteIp) && peer.Host != remoteIp)
            {
                Log($"IP de {peer.Name} actualizada: {peer.Host} -> {remoteIp}");
                peer.Host = remoteIp;
            }
            Persist();
        }
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
    };

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync();
        _client.Dispose();
    }
}
