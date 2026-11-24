package com.n3k0chan.klip.core

import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json

/** JSON for every protocol message (field names are the Kotlin property names, camelCase). */
val ProtocolJson = Json {
    ignoreUnknownKeys = true
    encodeDefaults = true
}

/** See docs/protocol.md §8.1. */
@Serializable
data class PairRequest(
    val protocolVersion: Int,
    val deviceId: String,
    val deviceName: String,
    val platform: String,
    val httpPort: Int,
    val publicKey: String,
    val nonce: String,
)

/** See docs/protocol.md §8.1. */
@Serializable
data class PairResponse(
    val protocolVersion: Int,
    val deviceId: String,
    val deviceName: String,
    val platform: String,
    val httpPort: Int,
    val publicKey: String,
    val nonce: String,
    val issuedAt: Long,
)

@Serializable
data class PairConfirmResponse(val accepted: Boolean)

@Serializable
data class ErrorBody(val error: String)

@Serializable
data class PingInfo(val deviceId: String, val deviceName: String, val platform: String)

/** Device described in a group introduction. See docs/protocol.md §8.3. */
@Serializable
data class MemberInfo(
    val deviceId: String,
    val deviceName: String,
    val platform: String,
    val httpPort: Int,
    val host: String,
)

/** See docs/protocol.md §8.3. */
@Serializable
data class IntroduceRequest(
    val member: MemberInfo,
    val issuedAt: Long,
    val iv: String,
    val ciphertext: String,
)

@Serializable
data class IntroduceResponse(val applied: Boolean)

@Serializable
data class RemoveRequest(val deviceId: String)

/** UDP radar beacon. See docs/protocol.md §8.6. */
@Serializable
data class Beacon(
    val klip: Int,
    val deviceId: String,
    val deviceName: String,
    val platform: String,
    val httpPort: Int,
    val pairingOpen: Boolean = false,
)

/** Encryption of the pair secret carried by a group introduction (§8.3). */
object Introductions {
    fun aad(introducerId: String, memberId: String, issuedAt: Long): ByteArray =
        "/group/introduce\n$introducerId\n$memberId\n$issuedAt".toByteArray(Charsets.UTF_8)

    fun seal(
        encKey: ByteArray,
        introducerId: String,
        member: MemberInfo,
        issuedAt: Long,
        secret: ByteArray,
        iv: ByteArray = Crypto.randomBytes(Protocol.AES_GCM_IV_BYTES),
    ): IntroduceRequest {
        val ct = Crypto.aesGcmEncrypt(encKey, iv, aad(introducerId, member.deviceId, issuedAt), secret)
        return IntroduceRequest(member, issuedAt, iv.toBase64(), ct.toBase64())
    }

    /** @throws CryptoException if the secret cannot be decrypted or has the wrong size. */
    fun open(encKey: ByteArray, introducerId: String, request: IntroduceRequest): ByteArray {
        val iv: ByteArray
        val ct: ByteArray
        try {
            iv = request.iv.base64ToBytes()
            ct = request.ciphertext.base64ToBytes()
        } catch (e: IllegalArgumentException) {
            throw CryptoException("Invalid introduction", e)
        }
        if (iv.size != Protocol.AES_GCM_IV_BYTES) throw CryptoException("Invalid IV")
        val secret = Crypto.aesGcmDecrypt(encKey, iv, aad(introducerId, request.member.deviceId, request.issuedAt), ct)
        if (secret.size != 32) throw CryptoException("Secret must be 32 bytes")
        return secret
    }
}

/** Version of a pair secret: (issuedAt, issuerId). See docs/protocol.md §8.3. */
data class SecretVersion(val issuedAt: Long, val issuerId: String) : Comparable<SecretVersion> {
    override fun compareTo(other: SecretVersion): Int {
        val c = issuedAt.compareTo(other.issuedAt)
        return if (c != 0) c else ordinal(issuerId, other.issuerId)
    }

    companion object {
        const val PIN_ISSUER = "pin"

        /** Ordinal (UTF-16 code unit) comparison, same as C# string.CompareOrdinal. */
        private fun ordinal(a: String, b: String): Int = a.compareTo(b)
    }
}
