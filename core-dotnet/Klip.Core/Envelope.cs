using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Klip.Core;

/// <summary>Clipboard envelope (HTTP body). See docs/protocol.md §6.1.</summary>
public sealed record Envelope
{
    [JsonPropertyName("protocolVersion")] public required int ProtocolVersion { get; init; }
    [JsonPropertyName("messageId")] public required string MessageId { get; init; }
    [JsonPropertyName("originDeviceId")] public required string OriginDeviceId { get; init; }
    [JsonPropertyName("timestamp")] public required long Timestamp { get; init; }
    [JsonPropertyName("iv")] public required string Iv { get; init; }
    [JsonPropertyName("ciphertext")] public required string Ciphertext { get; init; }
}

/// <summary>Encrypted clipboard payload. See docs/protocol.md §6.2.</summary>
public sealed record ClipboardPayload
{
    public const string TypeText = "text";
    public const string TypeImage = "image";

    [JsonPropertyName("type")] public string Type { get; init; } = TypeText;

    /// <summary>The text; empty for images.</summary>
    [JsonPropertyName("content")] public required string Content { get; init; }

    /// <summary>Images only: "image/png" or "image/jpeg".</summary>
    [JsonPropertyName("mimeType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MimeType { get; init; }

    /// <summary>Images only: the bytes, Base64.</summary>
    [JsonPropertyName("data")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Data { get; init; }

    [JsonIgnore] public bool IsImage => Type == TypeImage;

    /// <summary>Decoded image bytes (images only).</summary>
    public byte[] ImageBytes() => Convert.FromBase64String(Data ?? "");
    [JsonPropertyName("contentHash")] public required string ContentHash { get; init; }
    [JsonPropertyName("originDeviceName")] public required string OriginDeviceName { get; init; }
    [JsonPropertyName("isTest")] public bool IsTest { get; init; }

    public static ClipboardPayload Text(string content, string originDeviceName, bool isTest = false) => new()
    {
        Content = content,
        ContentHash = Core.Content.Hash(content),
        OriginDeviceName = originDeviceName,
        IsTest = isTest,
    };

    public static ClipboardPayload Image(byte[] bytes, string mimeType, string originDeviceName) => new()
    {
        Type = TypeImage,
        Content = "",
        MimeType = mimeType,
        Data = Convert.ToBase64String(bytes),
        ContentHash = Core.Content.HashBytes(bytes),
        OriginDeviceName = originDeviceName,
    };
}

public class EnvelopeException(EnvelopeException.Reason reason, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public enum Reason { Malformed, UnsupportedVersion, DecryptionFailed, UnsupportedType, HashMismatch }

    public Reason Kind { get; } = reason;
}

/// <summary>Seals and opens envelopes. See docs/protocol.md §6.</summary>
public static class EnvelopeCodec
{
    public static readonly JsonSerializerOptions Json = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // not embedded in HTML; keeps payloads compact
    };

    public static byte[] Aad(string messageId, string originDeviceId) =>
        Encoding.UTF8.GetBytes(messageId + "\n" + originDeviceId);

    public static Envelope Seal(byte[] encKey, string originDeviceId, ClipboardPayload payload,
        string? messageId = null, long? timestamp = null, byte[]? iv = null)
    {
        messageId ??= Guid.NewGuid().ToString();
        iv ??= KlipCrypto.RandomBytes(Protocol.AesGcmIvBytes);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(payload, Json);
        var ct = KlipCrypto.AesGcmEncrypt(encKey, iv, Aad(messageId, originDeviceId), plaintext);
        return new Envelope
        {
            ProtocolVersion = Protocol.Version,
            MessageId = messageId,
            OriginDeviceId = originDeviceId,
            Timestamp = timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Iv = Convert.ToBase64String(iv),
            Ciphertext = Convert.ToBase64String(ct),
        };
    }

    public static ClipboardPayload Open(byte[] encKey, Envelope envelope)
    {
        if (envelope.ProtocolVersion != Protocol.Version)
            throw new EnvelopeException(EnvelopeException.Reason.UnsupportedVersion, $"Unsupported protocolVersion {envelope.ProtocolVersion}");

        byte[] iv, ct;
        try
        {
            iv = Convert.FromBase64String(envelope.Iv);
            ct = Convert.FromBase64String(envelope.Ciphertext);
        }
        catch (FormatException e)
        {
            throw new EnvelopeException(EnvelopeException.Reason.Malformed, "Invalid base64", e);
        }
        if (iv.Length != Protocol.AesGcmIvBytes || ct.Length < Protocol.AesGcmTagBytes)
            throw new EnvelopeException(EnvelopeException.Reason.Malformed, "Invalid iv or ciphertext length");

        byte[] plaintext;
        try
        {
            plaintext = KlipCrypto.AesGcmDecrypt(encKey, iv, Aad(envelope.MessageId, envelope.OriginDeviceId), ct);
        }
        catch (KlipCryptoException e)
        {
            throw new EnvelopeException(EnvelopeException.Reason.DecryptionFailed, "Decryption failed", e);
        }

        ClipboardPayload payload;
        try
        {
            payload = JsonSerializer.Deserialize<ClipboardPayload>(plaintext, Json)
                      ?? throw new EnvelopeException(EnvelopeException.Reason.Malformed, "Empty payload");
        }
        catch (JsonException e)
        {
            throw new EnvelopeException(EnvelopeException.Reason.Malformed, "Invalid payload JSON", e);
        }
        switch (payload.Type)
        {
            case ClipboardPayload.TypeText:
                if (payload.ContentHash != Content.Hash(payload.Content))
                    throw new EnvelopeException(EnvelopeException.Reason.HashMismatch, "contentHash does not match content");
                break;
            case ClipboardPayload.TypeImage:
                if (payload.MimeType is null || !Content.ImageMimeTypes.Contains(payload.MimeType))
                    throw new EnvelopeException(EnvelopeException.Reason.UnsupportedType, $"Unsupported image type {payload.MimeType}");
                byte[] bytes;
                try { bytes = payload.ImageBytes(); }
                catch (FormatException e) { throw new EnvelopeException(EnvelopeException.Reason.Malformed, "Invalid image data", e); }
                if (bytes.Length == 0 || payload.ContentHash != Content.HashBytes(bytes))
                    throw new EnvelopeException(EnvelopeException.Reason.HashMismatch, "contentHash does not match image");
                break;
            default:
                throw new EnvelopeException(EnvelopeException.Reason.UnsupportedType, $"Unsupported type {payload.Type}");
        }
        return payload;
    }

    public static string Encode(Envelope envelope) => JsonSerializer.Serialize(envelope, Json);

    public static Envelope Decode(string body)
    {
        try
        {
            return JsonSerializer.Deserialize<Envelope>(body, Json)
                   ?? throw new EnvelopeException(EnvelopeException.Reason.Malformed, "Empty envelope");
        }
        catch (JsonException e)
        {
            throw new EnvelopeException(EnvelopeException.Reason.Malformed, "Invalid envelope JSON", e);
        }
    }
}
