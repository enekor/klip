using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace Klip.Core;

/// <summary>Ephemeral P-256 key pair used for one pairing attempt.</summary>
public sealed class PairingKeyPair : IDisposable
{
    internal ECDiffieHellman Key { get; }

    /// <summary>SEC1 uncompressed public key (65 bytes).</summary>
    public byte[] PublicKey { get; }

    internal PairingKeyPair(ECDiffieHellman key)
    {
        Key = key;
        var q = key.ExportParameters(false).Q;
        PublicKey = [0x04, .. q.X!, .. q.Y!];
    }

    public void Dispose() => Key.Dispose();
}

/// <summary>Pairing key agreement, key derivation and PIN. See docs/protocol.md §4.</summary>
public static class Pairing
{
    private static readonly byte[] ZeroSalt = new byte[32];

    // NIST P-256 domain parameters (a = p - 3).
    private static readonly BigInteger P = Parse("ffffffff00000001000000000000000000000000ffffffffffffffffffffffff");
    private static readonly BigInteger B = Parse("5ac635d8aa3a93e7b3ebbd55769886bc651d06b0cc53b0f63bce3c3e27d2604b");

    public static PairingKeyPair GenerateKeyPair() =>
        new(ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256));

    public static byte[] NewNonce() => KlipCrypto.RandomBytes(Protocol.PairingNonceBytes);

    /// <summary>Rebuilds a key pair from a raw private scalar and its public key (tests only).</summary>
    public static PairingKeyPair KeyPairFromRaw(byte[] privateScalar, byte[] publicKey)
    {
        var (x, y) = DecodePoint(publicKey);
        return new PairingKeyPair(ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = privateScalar,
            Q = new ECPoint { X = x, Y = y },
        }));
    }

    /// <summary>Raw ECDH shared secret (X coordinate, 32 bytes). Validates <paramref name="peerPublicKey"/>.</summary>
    /// <exception cref="KlipCryptoException">If the peer key is invalid.</exception>
    public static byte[] SharedSecret(PairingKeyPair own, byte[] peerPublicKey)
    {
        var (x, y) = DecodePoint(peerPublicKey);
        using var peer = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = x, Y = y },
        });
        return own.Key.DeriveRawSecretAgreement(peer.PublicKey);
    }

    public static byte[] DerivePairSecret(byte[] shared, byte[] initiatorNonce, byte[] responderNonce) =>
        KlipCrypto.Hkdf(shared, [.. initiatorNonce, .. responderNonce], "klip-pair-v1"u8.ToArray(), 32);

    public static string Pin(byte[] initiatorPublic, byte[] responderPublic, byte[] initiatorNonce, byte[] responderNonce)
    {
        var h = KlipCrypto.Sha256([.. initiatorPublic, .. responderPublic, .. initiatorNonce, .. responderNonce]);
        var n = BinaryPrimitives.ReadUInt32BigEndian(h);
        return (n % 1_000_000).ToString("D6");
    }

    /// <summary>True if <paramref name="publicKey"/> is a valid uncompressed P-256 point.</summary>
    public static bool IsValidPublicKey(byte[] publicKey)
    {
        try { DecodePoint(publicKey); return true; }
        catch (KlipCryptoException) { return false; }
    }

    internal static byte[] ZeroSaltCopy() => (byte[])ZeroSalt.Clone();

    private static (byte[] X, byte[] Y) DecodePoint(byte[] encoded)
    {
        if (encoded.Length != 65 || encoded[0] != 0x04) throw new KlipCryptoException("Public key must be 65-byte uncompressed SEC1");
        var xBytes = encoded[1..33];
        var yBytes = encoded[33..65];
        var x = new BigInteger(xBytes, isUnsigned: true, isBigEndian: true);
        var y = new BigInteger(yBytes, isUnsigned: true, isBigEndian: true);
        if (x >= P || y >= P) throw new KlipCryptoException("Coordinate out of range");
        // y^2 == x^3 - 3x + b (mod p)
        var lhs = BigInteger.ModPow(y, 2, P);
        var rhs = Mod(BigInteger.ModPow(x, 3, P) - 3 * x + B, P);
        if (lhs != rhs) throw new KlipCryptoException("Point is not on P-256");
        return (xBytes, yBytes);
    }

    private static BigInteger Mod(BigInteger v, BigInteger m) => ((v % m) + m) % m;

    private static BigInteger Parse(string hex) =>
        new(Convert.FromHexString(hex), isUnsigned: true, isBigEndian: true);
}

/// <summary>Working keys derived from a pair secret. See docs/protocol.md §4.3.</summary>
public sealed record PairKeys(byte[] MacKey, byte[] EncKey)
{
    public static PairKeys Derive(byte[] pairSecret)
    {
        if (pairSecret.Length != 32) throw new ArgumentException("Pair secret must be 32 bytes", nameof(pairSecret));
        return new PairKeys(
            KlipCrypto.Hkdf(pairSecret, Pairing.ZeroSaltCopy(), Encoding.UTF8.GetBytes("klip-mac"), 32),
            KlipCrypto.Hkdf(pairSecret, Pairing.ZeroSaltCopy(), Encoding.UTF8.GetBytes("klip-enc"), 32));
    }

    /// <summary>Random pair secret for group introductions.</summary>
    public static byte[] NewRandomSecret() => KlipCrypto.RandomBytes(32);
}
