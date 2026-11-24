using System.Text.Json;
using Klip.Core;

namespace Klip.Node;

/// <summary>Incoming pairing request shown to the user (docs/protocol.md §8.1).</summary>
public sealed record IncomingPairing(string SessionId, string DeviceId, string DeviceName, string Platform, string Pin, long ExpiresAt);

/// <summary>Outgoing pairing in progress: show <see cref="Pin"/> and ask the user to confirm.</summary>
public sealed class OutgoingPairing
{
    public required string Host { get; init; }
    public required string DeviceId { get; init; }
    public required string DeviceName { get; init; }
    public required string Platform { get; init; }
    public required int HttpPort { get; init; }
    public required string Pin { get; init; }
    internal PairKeys Keys { get; init; } = null!;
    internal byte[] Secret { get; init; } = [];
    internal long IssuedAt { get; init; }
}

public enum PairingStatus { Accepted, Rejected, Cancelled, TimedOut, Failed }

public sealed record PairingOutcome(PairingStatus Status, string? Message = null)
{
    public bool Ok => Status == PairingStatus.Accepted;
}

public sealed partial class SyncNode
{
    private sealed class IncomingSession
    {
        public required string Id { get; init; }
        public required PairRequest Request { get; init; }
        public required PairKeys Keys { get; init; }
        public required byte[] Secret { get; init; }
        public required string Pin { get; init; }
        public required long IssuedAt { get; init; }
        public required long ExpiresAt { get; init; }
        public TaskCompletionSource<bool> Decision { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly Lock _pairLock = new();
    private readonly Dictionary<string, Queue<long>> _pairRequestsByIp = [];
    private IncomingSession? _session;

    /// <summary>Verifies /pair/confirm and /pair/cancel, signed with the pending session keys.</summary>
    internal RequestVerifier SessionVerifier { get; }

    /// <summary>True while the configuration window is visible: only then are pairing requests accepted.</summary>
    public bool PairingOpen { get; set; }

    /// <summary>Someone wants to pair: show the PIN and call <see cref="RespondToPairing"/>.</summary>
    public event Action<IncomingPairing>? PairingRequested;

    /// <summary>An incoming session ended (arguments: session id, outcome, device name).</summary>
    public event Action<string, PairingStatus, string>? IncomingPairingFinished;

    private byte[]? SessionMacKey(string deviceId)
    {
        lock (_pairLock)
        {
            return _session is { } s && s.Request.DeviceId == deviceId && Now <= s.ExpiresAt ? s.Keys.MacKey : null;
        }
    }

    // ---------- Responder side ----------

    internal Reply HandlePairRequest(byte[] body, string? remoteIp)
    {
        if (!PairingOpen) return Reply.Error(403, "pairing_closed");
        if (!AllowPairRequest(remoteIp ?? "?")) return Reply.Error(429, "rate_limited");

        PairRequest req;
        byte[] peerPub, peerNonce;
        try
        {
            req = ProtocolJson.Deserialize<PairRequest>(body);
            peerPub = Convert.FromBase64String(req.PublicKey);
            peerNonce = Convert.FromBase64String(req.Nonce);
        }
        catch (Exception e) when (e is JsonException or FormatException)
        {
            return Reply.Error(400, "invalid_request");
        }
        if (req.ProtocolVersion != Protocol.Version || string.IsNullOrWhiteSpace(req.DeviceId) || req.DeviceId == DeviceId
            || peerNonce.Length != Protocol.PairingNonceBytes || !Pairing.IsValidPublicKey(peerPub)
            || req.HttpPort is < 1 or > 65535)
        {
            return Reply.Error(400, "invalid_request");
        }

        IncomingSession session;
        lock (_pairLock)
        {
            if (_session is { } s && Now <= s.ExpiresAt && s.Request.DeviceId != req.DeviceId) return Reply.Error(409, "busy");
            _session?.Decision.TrySetResult(false); // replaced by a new request from the same device

            using var kp = Pairing.GenerateKeyPair();
            var nonce = Pairing.NewNonce();
            var secret = Pairing.DerivePairSecret(Pairing.SharedSecret(kp, peerPub), peerNonce, nonce);
            session = new IncomingSession
            {
                Id = Guid.NewGuid().ToString(),
                Request = req,
                Keys = PairKeys.Derive(secret),
                Secret = secret,
                Pin = Pairing.Pin(peerPub, kp.PublicKey, peerNonce, nonce),
                IssuedAt = Now,
                ExpiresAt = Now + Protocol.PairingTimeoutMs,
            };
            _session = session;

            var response = new PairResponse
            {
                ProtocolVersion = Protocol.Version,
                DeviceId = DeviceId,
                DeviceName = DeviceName,
                Platform = Platform,
                HttpPort = HttpPort,
                PublicKey = Convert.ToBase64String(kp.PublicKey),
                Nonce = Convert.ToBase64String(nonce),
                IssuedAt = session.IssuedAt,
            };
            _ = ExpireSessionAsync(session);
            Log($"Solicitud de vinculación de {req.DeviceName} ({remoteIp})");
            PairingRequested?.Invoke(new IncomingPairing(session.Id, req.DeviceId, req.DeviceName, req.Platform, session.Pin, session.ExpiresAt));
            return new Reply(200, response);
        }
    }

    /// <summary>The local user accepted or rejected the incoming request shown via <see cref="PairingRequested"/>.</summary>
    public void RespondToPairing(string sessionId, bool accept)
    {
        IncomingSession? session;
        lock (_pairLock)
        {
            session = _session?.Id == sessionId ? _session : null;
            if (session is null) return;
            if (!accept) _session = null;
        }
        session.Decision.TrySetResult(accept);
        if (!accept) IncomingPairingFinished?.Invoke(sessionId, PairingStatus.Rejected, session.Request.DeviceName);
    }

    internal async Task<Reply> HandlePairConfirmAsync(string deviceId, string? remoteIp)
    {
        IncomingSession? session;
        lock (_pairLock) session = _session?.Request.DeviceId == deviceId ? _session : null;
        if (session is null) return Reply.Error(403, "rejected");

        var remaining = Math.Max(0, session.ExpiresAt - Now);
        var finished = await Task.WhenAny(session.Decision.Task, Task.Delay(TimeSpan.FromMilliseconds(remaining)));
        lock (_pairLock)
        {
            if (_session == session) _session = null;
        }
        var name = session.Request.DeviceName;
        if (finished != session.Decision.Task)
        {
            IncomingPairingFinished?.Invoke(session.Id, PairingStatus.TimedOut, name);
            return Reply.Error(408, "timeout");
        }
        if (!session.Decision.Task.Result) return Reply.Error(403, "rejected");

        var r = session.Request;
        StorePeer(new MemberInfo { DeviceId = r.DeviceId, DeviceName = r.DeviceName, Platform = r.Platform, HttpPort = r.HttpPort, Host = remoteIp ?? "" },
            session.Secret, new SecretVersion(session.IssuedAt, SecretVersion.PinIssuer));
        Log($"Vinculado con {name}");
        IncomingPairingFinished?.Invoke(session.Id, PairingStatus.Accepted, name);
        _ = IntroduceNewPeerAsync(r.DeviceId);
        return new Reply(200, new PairConfirmResponse(true));
    }

    internal Reply HandlePairCancel(string deviceId)
    {
        IncomingSession? session;
        lock (_pairLock)
        {
            session = _session?.Request.DeviceId == deviceId ? _session : null;
            if (session is not null) _session = null;
        }
        if (session is null) return new Reply(200);
        session.Decision.TrySetResult(false);
        IncomingPairingFinished?.Invoke(session.Id, PairingStatus.Cancelled, session.Request.DeviceName);
        return new Reply(200);
    }

    private async Task ExpireSessionAsync(IncomingSession session)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(Protocol.PairingTimeoutMs + 500));
        bool expired;
        lock (_pairLock)
        {
            expired = _session == session;
            if (expired) _session = null;
        }
        if (expired)
        {
            session.Decision.TrySetResult(false);
            IncomingPairingFinished?.Invoke(session.Id, PairingStatus.TimedOut, session.Request.DeviceName);
        }
    }

    private bool AllowPairRequest(string ip)
    {
        lock (_pairLock)
        {
            if (!_pairRequestsByIp.TryGetValue(ip, out var q)) _pairRequestsByIp[ip] = q = new Queue<long>();
            while (q.Count > 0 && Now - q.Peek() > 60_000) q.Dequeue();
            if (q.Count >= Protocol.PairRequestsPerMinute) return false;
            q.Enqueue(Now);
            return true;
        }
    }

    // ---------- Initiator side ----------

    /// <summary>Step 1: contacts the other device. On success, show <see cref="OutgoingPairing.Pin"/>.</summary>
    public async Task<(OutgoingPairing? Pairing, string? Error)> StartPairingAsync(string host, int port)
    {
        using var kp = Pairing.GenerateKeyPair();
        var nonce = Pairing.NewNonce();
        var request = new PairRequest
        {
            ProtocolVersion = Protocol.Version,
            DeviceId = DeviceId,
            DeviceName = DeviceName,
            Platform = Platform,
            HttpPort = HttpPort,
            PublicKey = Convert.ToBase64String(kp.PublicKey),
            Nonce = Convert.ToBase64String(nonce),
        };
        var r = await _client.SendAsync(HttpMethod.Post, host, port, "/pair/request", ProtocolJson.Serialize(request), null, DeviceId);
        if (!r.IsSuccess) return (null, DescribePairError(r));

        try
        {
            var resp = ProtocolJson.Deserialize<PairResponse>(r.Body);
            var peerPub = Convert.FromBase64String(resp.PublicKey);
            var peerNonce = Convert.FromBase64String(resp.Nonce);
            if (resp.DeviceId == DeviceId || peerNonce.Length != Protocol.PairingNonceBytes || !Pairing.IsValidPublicKey(peerPub))
            {
                return (null, "Respuesta de vinculación inválida");
            }
            var secret = Pairing.DerivePairSecret(Pairing.SharedSecret(kp, peerPub), nonce, peerNonce);
            return (new OutgoingPairing
            {
                Host = host,
                DeviceId = resp.DeviceId,
                DeviceName = resp.DeviceName,
                Platform = resp.Platform,
                HttpPort = resp.HttpPort,
                Pin = Pairing.Pin(kp.PublicKey, peerPub, nonce, peerNonce),
                Keys = PairKeys.Derive(secret),
                Secret = secret,
                IssuedAt = resp.IssuedAt,
            }, null);
        }
        catch (Exception e) when (e is JsonException or FormatException or KlipCryptoException)
        {
            return (null, "Respuesta de vinculación inválida");
        }
    }

    /// <summary>Step 2: the local user confirmed the PIN. Waits for the other user's decision.</summary>
    public async Task<PairingOutcome> ConfirmPairingAsync(OutgoingPairing p)
    {
        var r = await _client.SendAsync(HttpMethod.Post, p.Host, p.HttpPort, "/pair/confirm", "{}"u8.ToArray(), p.Keys, DeviceId,
            TimeSpan.FromMilliseconds(Protocol.PairingTimeoutMs + 5_000));
        switch (r.Status)
        {
            case 200:
                StorePeer(new MemberInfo { DeviceId = p.DeviceId, DeviceName = p.DeviceName, Platform = p.Platform, HttpPort = p.HttpPort, Host = p.Host },
                    p.Secret, new SecretVersion(p.IssuedAt, SecretVersion.PinIssuer));
                Log($"Vinculado con {p.DeviceName}");
                _ = IntroduceNewPeerAsync(p.DeviceId);
                return new PairingOutcome(PairingStatus.Accepted);
            case 403:
                return new PairingOutcome(PairingStatus.Rejected, $"{p.DeviceName} rechazó la vinculación");
            case 408:
                return new PairingOutcome(PairingStatus.TimedOut, "Se agotó el tiempo de espera");
            default:
                return new PairingOutcome(PairingStatus.Failed, DescribePairError(r));
        }
    }

    /// <summary>The local user rejected the PIN or closed the dialog.</summary>
    public async Task CancelPairingAsync(OutgoingPairing p) =>
        await _client.SendAsync(HttpMethod.Post, p.Host, p.HttpPort, "/pair/cancel", "{}"u8.ToArray(), p.Keys, DeviceId, TimeSpan.FromSeconds(3));

    private static string DescribePairError(HttpOutcome r)
    {
        if (r.Status is null) return r.Error ?? "Error de conexión";
        string? code = null;
        try { code = ProtocolJson.Deserialize<ErrorBody>(r.Body).Error; }
        catch (JsonException) { }
        return code switch
        {
            "pairing_closed" => "El otro dispositivo debe tener abierta la ventana de Klip",
            "busy" => "El otro dispositivo está atendiendo otra vinculación",
            "rate_limited" => "Demasiados intentos; espera un minuto",
            "invalid_request" => "El otro dispositivo rechazó la solicitud (¿versión distinta?)",
            _ => $"HTTP {r.Status}",
        };
    }
}
