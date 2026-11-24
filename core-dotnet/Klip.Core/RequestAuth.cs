using System.Text;

namespace Klip.Core;

/// <summary>Headers that authenticate one request.</summary>
public sealed record SignedHeaders(string DeviceId, string Timestamp, string Nonce, string Signature)
{
    public IReadOnlyDictionary<string, string> ToDictionary() => new Dictionary<string, string>
    {
        [Protocol.HeaderDevice] = DeviceId,
        [Protocol.HeaderTimestamp] = Timestamp,
        [Protocol.HeaderNonce] = Nonce,
        [Protocol.HeaderSignature] = Signature,
    };
}

/// <summary>Request signing. See docs/protocol.md §5.</summary>
public static class RequestSigner
{
    public static string Canonical(string method, string path, string timestamp, string nonce, ReadOnlySpan<byte> body) =>
        string.Join('\n', method.ToUpperInvariant(), path, timestamp, nonce, Convert.ToHexStringLower(KlipCrypto.Sha256(body)));

    public static string Signature(byte[] macKey, string method, string path, string timestamp, string nonce, ReadOnlySpan<byte> body) =>
        Convert.ToBase64String(KlipCrypto.HmacSha256(macKey, Encoding.UTF8.GetBytes(Canonical(method, path, timestamp, nonce, body))));

    public static SignedHeaders Sign(byte[] macKey, string deviceId, string method, string path, ReadOnlySpan<byte> body,
        long? nowMs = null, string? nonce = null)
    {
        var ts = (nowMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).ToString();
        nonce ??= Convert.ToBase64String(KlipCrypto.RandomBytes(Protocol.RequestNonceBytes));
        return new SignedHeaders(deviceId, ts, nonce, Signature(macKey, method, path, ts, nonce, body));
    }
}

public enum VerifyResult
{
    Ok,
    Malformed,
    UnknownDevice,
    Expired,
    BadSignature,
    Replay,
}

public static class VerifyResultExtensions
{
    public static int HttpStatus(this VerifyResult r) => r switch
    {
        VerifyResult.Ok => 200,
        VerifyResult.Malformed => 400,
        VerifyResult.UnknownDevice => 403,
        _ => 401,
    };
}

/// <summary>Verifies signed requests and keeps the replay cache. Thread-safe.</summary>
/// <param name="macKeyFor">Returns the MAC key of a paired device, or null if unknown.</param>
public sealed class RequestVerifier(Func<string, byte[]?> macKeyFor, Func<long>? clock = null)
{
    private readonly Func<long> _clock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    private readonly LinkedList<(string Key, long Seen)> _order = new();
    private readonly HashSet<string> _seen = [];
    private readonly Lock _lock = new();

    public VerifyResult Verify(IEnumerable<KeyValuePair<string, string?>> headers, string method, string path, ReadOnlySpan<byte> body)
    {
        var h = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, val) in headers) h[k] = val;
        h.TryGetValue(Protocol.HeaderDevice, out var deviceId);
        h.TryGetValue(Protocol.HeaderTimestamp, out var timestamp);
        h.TryGetValue(Protocol.HeaderNonce, out var nonce);
        h.TryGetValue(Protocol.HeaderSignature, out var signature);
        if (string.IsNullOrEmpty(deviceId) || timestamp is null || nonce is null || signature is null) return VerifyResult.Malformed;
        if (!long.TryParse(timestamp, out var ts)) return VerifyResult.Malformed;
        var nonceBuf = new byte[Protocol.RequestNonceBytes + 4];
        if (!Convert.TryFromBase64String(nonce, nonceBuf, out var nonceLen) || nonceLen != Protocol.RequestNonceBytes) return VerifyResult.Malformed;
        var sigBuf = new byte[64];
        if (!Convert.TryFromBase64String(signature, sigBuf, out var sigLen)) return VerifyResult.Malformed;

        var macKey = macKeyFor(deviceId);
        if (macKey is null) return VerifyResult.UnknownDevice;
        var now = _clock();
        if (Math.Abs(now - ts) > Protocol.TimestampWindowMs) return VerifyResult.Expired;

        var expected = KlipCrypto.HmacSha256(macKey, Encoding.UTF8.GetBytes(RequestSigner.Canonical(method, path, timestamp, nonce, body)));
        if (!KlipCrypto.ConstantTimeEquals(expected, sigBuf.AsSpan(0, sigLen))) return VerifyResult.BadSignature;

        var key = deviceId + "\n" + nonce;
        lock (_lock)
        {
            while (_order.First is { } first && now - first.Value.Seen > Protocol.NonceTtlMs)
            {
                _seen.Remove(first.Value.Key);
                _order.RemoveFirst();
            }
            if (!_seen.Add(key)) return VerifyResult.Replay;
            _order.AddLast((key, now));
        }
        return VerifyResult.Ok;
    }
}
