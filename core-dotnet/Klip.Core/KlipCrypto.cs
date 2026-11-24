using System.Security.Cryptography;

namespace Klip.Core;

/// <summary>Low-level primitives.</summary>
public static class KlipCrypto
{
    public static byte[] RandomBytes(int n) => RandomNumberGenerator.GetBytes(n);

    public static byte[] Sha256(ReadOnlySpan<byte> data) => SHA256.HashData(data);

    public static byte[] HmacSha256(byte[] key, ReadOnlySpan<byte> data) => HMACSHA256.HashData(key, data);

    public static byte[] Hkdf(byte[] ikm, byte[] salt, byte[] info, int length) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, length, salt, info);

    public static bool ConstantTimeEquals(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) =>
        CryptographicOperations.FixedTimeEquals(a, b);

    /// <summary>AES-256-GCM. Returns ct || tag.</summary>
    public static byte[] AesGcmEncrypt(byte[] key, byte[] iv, byte[] aad, byte[] plaintext)
    {
        Validate(key, iv);
        var output = new byte[plaintext.Length + Protocol.AesGcmTagBytes];
        using var gcm = new AesGcm(key, Protocol.AesGcmTagBytes);
        gcm.Encrypt(iv, plaintext, output.AsSpan(0, plaintext.Length), output.AsSpan(plaintext.Length), aad);
        return output;
    }

    /// <summary>AES-256-GCM. <paramref name="ciphertext"/> is ct || tag.</summary>
    /// <exception cref="KlipCryptoException">If authentication fails.</exception>
    public static byte[] AesGcmDecrypt(byte[] key, byte[] iv, byte[] aad, byte[] ciphertext)
    {
        Validate(key, iv);
        if (ciphertext.Length < Protocol.AesGcmTagBytes) throw new KlipCryptoException("Ciphertext too short");
        var ctLen = ciphertext.Length - Protocol.AesGcmTagBytes;
        var plaintext = new byte[ctLen];
        using var gcm = new AesGcm(key, Protocol.AesGcmTagBytes);
        try
        {
            gcm.Decrypt(iv, ciphertext.AsSpan(0, ctLen), ciphertext.AsSpan(ctLen), plaintext, aad);
        }
        catch (AuthenticationTagMismatchException e)
        {
            throw new KlipCryptoException("AES-GCM authentication failed", e);
        }
        return plaintext;
    }

    private static void Validate(byte[] key, byte[] iv)
    {
        if (key.Length != 32) throw new ArgumentException("AES-256 key must be 32 bytes", nameof(key));
        if (iv.Length != Protocol.AesGcmIvBytes) throw new ArgumentException($"IV must be {Protocol.AesGcmIvBytes} bytes", nameof(iv));
    }
}

public class KlipCryptoException(string message, Exception? inner = null) : Exception(message, inner);
