using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Klip.Core;

/// <summary>JSON options for every protocol message except the clipboard envelope (camelCase names).</summary>
public static class ProtocolJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);

    /// <exception cref="JsonException">If the JSON is invalid or required members are missing.</exception>
    public static T Deserialize<T>(ReadOnlySpan<byte> json) =>
        JsonSerializer.Deserialize<T>(json, Options) ?? throw new JsonException("null");
}

/// <summary>See docs/protocol.md §8.1.</summary>
public sealed record PairRequest
{
    public required int ProtocolVersion { get; init; }
    public required string DeviceId { get; init; }
    public required string DeviceName { get; init; }
    public required string Platform { get; init; }
    public required int HttpPort { get; init; }
    public required string PublicKey { get; init; }
    public required string Nonce { get; init; }
}

/// <summary>See docs/protocol.md §8.1.</summary>
public sealed record PairResponse
{
    public required int ProtocolVersion { get; init; }
    public required string DeviceId { get; init; }
    public required string DeviceName { get; init; }
    public required string Platform { get; init; }
    public required int HttpPort { get; init; }
    public required string PublicKey { get; init; }
    public required string Nonce { get; init; }
    public required long IssuedAt { get; init; }
}

public sealed record PairConfirmResponse(bool Accepted);

public sealed record ErrorBody(string Error);

/// <summary>Device described in a group introduction. See docs/protocol.md §8.3.</summary>
public sealed record MemberInfo
{
    public required string DeviceId { get; init; }
    public required string DeviceName { get; init; }
    public required string Platform { get; init; }
    public required int HttpPort { get; init; }
    public required string Host { get; init; }
}

/// <summary>See docs/protocol.md §8.3.</summary>
public sealed record IntroduceRequest
{
    public required MemberInfo Member { get; init; }
    public required long IssuedAt { get; init; }
    public required string Iv { get; init; }
    public required string Ciphertext { get; init; }
}

public sealed record IntroduceResponse(bool Applied);

public sealed record RemoveRequest(string DeviceId);

/// <summary>UDP radar beacon. See docs/protocol.md §8.6.</summary>
public sealed record Beacon
{
    public required int Klip { get; init; }
    public required string DeviceId { get; init; }
    public required string DeviceName { get; init; }
    public required string Platform { get; init; }
    public required int HttpPort { get; init; }
    public bool PairingOpen { get; init; }
}

/// <summary>Encryption of the pair secret carried by a group introduction (§8.3).</summary>
public static class Introductions
{
    public static byte[] Aad(string introducerId, string memberId, long issuedAt) =>
        Encoding.UTF8.GetBytes($"/group/introduce\n{introducerId}\n{memberId}\n{issuedAt}");

    public static IntroduceRequest Seal(byte[] encKey, string introducerId, MemberInfo member, long issuedAt, byte[] secret, byte[]? iv = null)
    {
        iv ??= KlipCrypto.RandomBytes(Protocol.AesGcmIvBytes);
        var ct = KlipCrypto.AesGcmEncrypt(encKey, iv, Aad(introducerId, member.DeviceId, issuedAt), secret);
        return new IntroduceRequest { Member = member, IssuedAt = issuedAt, Iv = Convert.ToBase64String(iv), Ciphertext = Convert.ToBase64String(ct) };
    }

    /// <exception cref="KlipCryptoException">If the secret cannot be decrypted or has the wrong size.</exception>
    public static byte[] Open(byte[] encKey, string introducerId, IntroduceRequest request)
    {
        try
        {
            var secret = KlipCrypto.AesGcmDecrypt(encKey, Convert.FromBase64String(request.Iv),
                Aad(introducerId, request.Member.DeviceId, request.IssuedAt), Convert.FromBase64String(request.Ciphertext));
            return secret.Length == 32 ? secret : throw new KlipCryptoException("Secret must be 32 bytes");
        }
        catch (Exception e) when (e is FormatException or ArgumentException)
        {
            throw new KlipCryptoException("Invalid introduction", e);
        }
    }
}

/// <summary>Version of a pair secret: (issuedAt, issuerId). See docs/protocol.md §8.3.</summary>
public readonly record struct SecretVersion(long IssuedAt, string IssuerId) : IComparable<SecretVersion>
{
    public const string PinIssuer = "pin";

    public int CompareTo(SecretVersion other)
    {
        var c = IssuedAt.CompareTo(other.IssuedAt);
        return c != 0 ? c : string.CompareOrdinal(IssuerId, other.IssuerId);
    }
}
