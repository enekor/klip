using System.Collections.Concurrent;
using System.Text.Json;
using Klip.Core;

namespace Klip.Node;

/// <summary>Group mesh: introductions, unpair and expel. See docs/protocol.md §8.3–8.4.</summary>
public sealed partial class SyncNode
{
    private readonly ConcurrentDictionary<string, byte> _deliveringTo = new();

    /// <summary>After pairing with <paramref name="newId"/>, introduce it to every other peer and vice versa.</summary>
    private async Task IntroduceNewPeerAsync(string newId)
    {
        if (!TryGetPeer(newId, out var newPeer, out _)) return;
        var others = PeersWithKeys(p => p.DeviceId != newId);
        foreach (var (member, _) in others)
        {
            var secret = PairKeys.NewRandomSecret();
            var issuedAt = Now;
            await DeliverIntroductionAsync(newId, member.ToMemberInfo(), issuedAt, secret);
            await DeliverIntroductionAsync(member.DeviceId, newPeer.ToMemberInfo(), issuedAt, secret);
        }
    }

    /// <summary>Sends one introduction; queues it if the receiver is unreachable.</summary>
    private async Task<bool> DeliverIntroductionAsync(string receiverId, MemberInfo member, long issuedAt, byte[] secret, bool queueOnFailure = true)
    {
        if (!TryGetPeer(receiverId, out var receiver, out var keys)) return false;
        var request = Introductions.Seal(keys.EncKey, DeviceId, member, issuedAt, secret);
        var r = await _client.SendAsync(HttpMethod.Post, receiver.Host, receiver.Port, "/group/introduce", ProtocolJson.Serialize(request), keys, DeviceId);
        if (r.IsSuccess)
        {
            Log($"Presentado {member.DeviceName} a {receiver.Name}");
            return true;
        }
        if (queueOnFailure && (r.Status is null || r.Status >= 500))
        {
            lock (_lock)
            {
                _config.PendingIntroductions.Add(new PendingIntroduction
                {
                    ReceiverId = receiverId,
                    Member = member,
                    IssuedAt = issuedAt,
                    ProtectedSecret = Convert.ToBase64String(_protector.Protect(secret)),
                    CreatedAt = Now,
                });
                Persist();
            }
            Log($"Presentación de {member.DeviceName} a {receiver.Name} pendiente ({r.Error ?? $"HTTP {r.Status}"})");
        }
        else
        {
            Log($"Presentación de {member.DeviceName} a {receiver.Name} rechazada (HTTP {r.Status})");
        }
        return false;
    }

    private async Task DeliverAllPendingAsync()
    {
        List<string> receivers;
        lock (_lock) receivers = _config.PendingIntroductions.Select(i => i.ReceiverId).Distinct().ToList();
        foreach (var id in receivers) await DeliverPendingAsync(id);
    }

    private async Task DeliverPendingAsync(string receiverId)
    {
        if (!_deliveringTo.TryAdd(receiverId, 0)) return;
        try
        {
            List<PendingIntroduction> pending;
            lock (_lock)
            {
                _config.PendingIntroductions.RemoveAll(i => Now - i.CreatedAt > Protocol.IntroductionExpiryMs);
                pending = _config.PendingIntroductions.Where(i => i.ReceiverId == receiverId).ToList();
            }
            foreach (var p in pending)
            {
                byte[] secret;
                try { secret = _protector.Unprotect(Convert.FromBase64String(p.ProtectedSecret)); }
                catch (Exception) { secret = []; }
                var delivered = secret.Length == 32 && await DeliverIntroductionAsync(receiverId, p.Member, p.IssuedAt, secret, queueOnFailure: false);
                if (delivered || secret.Length != 32)
                {
                    lock (_lock)
                    {
                        _config.PendingIntroductions.Remove(p);
                        Persist();
                    }
                }
                else
                {
                    break; // still unreachable: try again on next contact
                }
            }
        }
        finally
        {
            _deliveringTo.TryRemove(receiverId, out _);
        }
    }

    internal Reply HandleIntroduce(string introducerId, byte[] body)
    {
        if (!_keys.TryGetValue(introducerId, out var keys)) return new Reply(403);
        IntroduceRequest req;
        byte[] secret;
        try
        {
            req = ProtocolJson.Deserialize<IntroduceRequest>(body);
            if (req.Member.DeviceId == DeviceId || string.IsNullOrWhiteSpace(req.Member.DeviceId) || req.Member.DeviceId == introducerId)
                return Reply.Error(400, "invalid_request");
            secret = Introductions.Open(keys.EncKey, introducerId, req);
        }
        catch (Exception e) when (e is JsonException or KlipCryptoException)
        {
            return Reply.Error(400, "invalid_request");
        }

        var version = new SecretVersion(req.IssuedAt, introducerId);
        lock (_lock)
        {
            var existing = _config.Peers.FirstOrDefault(p => p.DeviceId == req.Member.DeviceId);
            if (existing is not null && existing.SecretVersion.CompareTo(version) >= 0) return new Reply(200, new IntroduceResponse(false));
        }
        StorePeer(req.Member, secret, version);
        Log($"Nuevo miembro del grupo: {req.Member.DeviceName} (presentado por {introducerId})");
        return new Reply(200, new IntroduceResponse(true));
    }

    /// <summary>Breaks the pair with <paramref name="deviceId"/> on both sides (best effort remotely).</summary>
    public async Task UnpairAsync(string deviceId)
    {
        if (TryGetPeer(deviceId, out var peer, out var keys))
        {
            await _client.SendAsync(HttpMethod.Post, peer.Host, peer.Port, "/unpair", "{}"u8.ToArray(), keys, DeviceId, TimeSpan.FromSeconds(3));
        }
        RemovePeer(deviceId);
    }

    /// <summary>Removes <paramref name="deviceId"/> from the whole group (best effort remotely).</summary>
    public async Task ExpelAsync(string deviceId)
    {
        var body = ProtocolJson.Serialize(new RemoveRequest(deviceId));
        var targets = PeersWithKeys(_ => true);
        await Task.WhenAll(targets.Select(t =>
            _client.SendAsync(HttpMethod.Post, t.Peer.Host, t.Peer.Port, "/group/remove", body, t.Keys, DeviceId, TimeSpan.FromSeconds(3))));
        RemovePeer(deviceId);
    }

    internal Reply HandleUnpair(string senderId)
    {
        RemovePeer(senderId);
        Log($"{senderId} ha deshecho la vinculación");
        return new Reply(200);
    }

    internal Reply HandleGroupRemove(string senderId, byte[] body)
    {
        RemoveRequest req;
        try { req = ProtocolJson.Deserialize<RemoveRequest>(body); }
        catch (JsonException) { return Reply.Error(400, "invalid_request"); }
        if (string.IsNullOrEmpty(req.DeviceId)) return Reply.Error(400, "invalid_request");

        if (req.DeviceId == DeviceId)
        {
            Log($"Expulsado del grupo por {senderId}: se eliminan todas las vinculaciones");
            foreach (var p in Peers) RemovePeer(p.DeviceId);
        }
        else
        {
            RemovePeer(req.DeviceId);
        }
        return new Reply(200);
    }
}
