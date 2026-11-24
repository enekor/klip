namespace Klip.Core;

/// <summary>Protocol constants. See docs/protocol.md §2.</summary>
public static class Protocol
{
    public const int Version = 1;
    public const int HttpPort = 8765;
    public const int DiscoveryPort = 8766;
    public const string MdnsServiceType = "_klip._tcp";
    public const long TimestampWindowMs = 30_000;
    public const long NonceTtlMs = 60_000;
    public const int ProcessedIdsCapacity = 50;
    public const int PairingNonceBytes = 32;
    public const int RequestNonceBytes = 16;
    public const int AesGcmIvBytes = 12;
    public const int AesGcmTagBytes = 16;
    public const long PairingTimeoutMs = 60_000;
    public const int PairRequestsPerMinute = 5;
    public const int BeaconIntervalMs = 1_500;
    public const long DiscoveryExpiryMs = 5_000;
    public const long IntroductionExpiryMs = 7L * 24 * 3600 * 1000;

    public const string HeaderDevice = "X-Klip-Device";
    public const string HeaderTimestamp = "X-Klip-Timestamp";
    public const string HeaderNonce = "X-Klip-Nonce";
    public const string HeaderSignature = "X-Klip-Signature";
}
