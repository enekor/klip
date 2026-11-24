package com.n3k0chan.klip.core

/** Headers that authenticate one request. */
data class SignedHeaders(
    val deviceId: String,
    val timestamp: String,
    val nonce: String,
    val signature: String,
) {
    fun toMap(): Map<String, String> = mapOf(
        Protocol.HEADER_DEVICE to deviceId,
        Protocol.HEADER_TIMESTAMP to timestamp,
        Protocol.HEADER_NONCE to nonce,
        Protocol.HEADER_SIGNATURE to signature,
    )
}

/** Request signing. See docs/protocol.md §5. */
object RequestSigner {
    fun canonical(method: String, path: String, timestamp: String, nonce: String, body: ByteArray): String =
        listOf(method.uppercase(), path, timestamp, nonce, Crypto.sha256(body).toHex()).joinToString("\n")

    fun signature(macKey: ByteArray, method: String, path: String, timestamp: String, nonce: String, body: ByteArray): String =
        Crypto.hmacSha256(macKey, canonical(method, path, timestamp, nonce, body).toByteArray(Charsets.UTF_8)).toBase64()

    fun sign(
        macKey: ByteArray,
        deviceId: String,
        method: String,
        path: String,
        body: ByteArray,
        nowMs: Long = System.currentTimeMillis(),
        nonce: String = Crypto.randomBytes(Protocol.REQUEST_NONCE_BYTES).toBase64(),
    ): SignedHeaders {
        val ts = nowMs.toString()
        return SignedHeaders(deviceId, ts, nonce, signature(macKey, method, path, ts, nonce, body))
    }
}

enum class VerifyResult(val httpStatus: Int) {
    OK(200),
    MALFORMED(400),
    UNKNOWN_DEVICE(403),
    EXPIRED(401),
    BAD_SIGNATURE(401),
    REPLAY(401),
}

/**
 * Verifies signed requests and keeps the replay cache. Thread-safe.
 *
 * @param macKeyFor returns the MAC key of a paired device, or null if unknown.
 */
class RequestVerifier(
    private val macKeyFor: (deviceId: String) -> ByteArray?,
    private val clock: () -> Long = System::currentTimeMillis,
) {
    private val seenNonces = LinkedHashMap<String, Long>()

    fun verify(headers: Map<String, String?>, method: String, path: String, body: ByteArray): VerifyResult {
        val h = headers.mapKeys { it.key.lowercase() }
        val deviceId = h[Protocol.HEADER_DEVICE.lowercase()]
        val timestamp = h[Protocol.HEADER_TIMESTAMP.lowercase()]
        val nonce = h[Protocol.HEADER_NONCE.lowercase()]
        val signature = h[Protocol.HEADER_SIGNATURE.lowercase()]
        if (deviceId.isNullOrEmpty() || timestamp == null || nonce == null || signature == null) return VerifyResult.MALFORMED
        val ts = timestamp.toLongOrNull() ?: return VerifyResult.MALFORMED
        val nonceBytes = runCatching { nonce.base64ToBytes() }.getOrNull()
        val sigBytes = runCatching { signature.base64ToBytes() }.getOrNull()
        if (nonceBytes?.size != Protocol.REQUEST_NONCE_BYTES || sigBytes == null) return VerifyResult.MALFORMED

        val macKey = macKeyFor(deviceId) ?: return VerifyResult.UNKNOWN_DEVICE
        val now = clock()
        if (kotlin.math.abs(now - ts) > Protocol.TIMESTAMP_WINDOW_MS) return VerifyResult.EXPIRED

        val expected = Crypto.hmacSha256(macKey, RequestSigner.canonical(method, path, timestamp, nonce, body).toByteArray(Charsets.UTF_8))
        if (!Crypto.constantTimeEquals(expected, sigBytes)) return VerifyResult.BAD_SIGNATURE

        val key = "$deviceId\n$nonce"
        synchronized(seenNonces) {
            evictExpired(now)
            if (seenNonces.containsKey(key)) return VerifyResult.REPLAY
            seenNonces[key] = now
        }
        return VerifyResult.OK
    }

    private fun evictExpired(now: Long) {
        val it = seenNonces.entries.iterator()
        while (it.hasNext()) {
            if (now - it.next().value > Protocol.NONCE_TTL_MS) it.remove() else break
        }
    }
}
