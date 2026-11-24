package com.n3k0chan.klip.core

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import kotlinx.serialization.SerializationException
import kotlinx.serialization.json.Json
import java.util.UUID

/** Clipboard envelope (HTTP body). See docs/protocol.md §6.1. */
@Serializable
data class Envelope(
    val protocolVersion: Int,
    val messageId: String,
    val originDeviceId: String,
    val timestamp: Long,
    val iv: String,
    val ciphertext: String,
)

/** Encrypted clipboard payload. See docs/protocol.md §6.2. */
@Serializable
data class ClipboardPayload(
    val type: String = TYPE_TEXT,
    val content: String,
    val contentHash: String,
    val originDeviceName: String,
    val isTest: Boolean = false,
) {
    companion object {
        const val TYPE_TEXT = "text"

        fun text(content: String, originDeviceName: String, isTest: Boolean = false) =
            ClipboardPayload(TYPE_TEXT, content, Content.hash(content), originDeviceName, isTest)
    }
}

class EnvelopeException(val reason: Reason, message: String, cause: Throwable? = null) : Exception(message, cause) {
    enum class Reason { MALFORMED, UNSUPPORTED_VERSION, DECRYPTION_FAILED, UNSUPPORTED_TYPE, HASH_MISMATCH }
}

/** Seals and opens envelopes. See docs/protocol.md §6. */
object EnvelopeCodec {
    val json = Json {
        ignoreUnknownKeys = true
        encodeDefaults = true
    }

    fun aad(messageId: String, originDeviceId: String): ByteArray =
        "$messageId\n$originDeviceId".toByteArray(Charsets.UTF_8)

    fun seal(
        encKey: ByteArray,
        originDeviceId: String,
        payload: ClipboardPayload,
        messageId: String = UUID.randomUUID().toString(),
        timestamp: Long = System.currentTimeMillis(),
        iv: ByteArray = Crypto.randomBytes(Protocol.AES_GCM_IV_BYTES),
    ): Envelope {
        val plaintext = json.encodeToString(ClipboardPayload.serializer(), payload).toByteArray(Charsets.UTF_8)
        val ct = Crypto.aesGcmEncrypt(encKey, iv, aad(messageId, originDeviceId), plaintext)
        return Envelope(Protocol.VERSION, messageId, originDeviceId, timestamp, iv.toBase64(), ct.toBase64())
    }

    fun open(encKey: ByteArray, envelope: Envelope): ClipboardPayload {
        if (envelope.protocolVersion != Protocol.VERSION) {
            throw EnvelopeException(EnvelopeException.Reason.UNSUPPORTED_VERSION, "Unsupported protocolVersion ${envelope.protocolVersion}")
        }
        val iv: ByteArray
        val ct: ByteArray
        try {
            iv = envelope.iv.base64ToBytes()
            ct = envelope.ciphertext.base64ToBytes()
        } catch (e: IllegalArgumentException) {
            throw EnvelopeException(EnvelopeException.Reason.MALFORMED, "Invalid base64", e)
        }
        if (iv.size != Protocol.AES_GCM_IV_BYTES || ct.size < Protocol.AES_GCM_TAG_BYTES) {
            throw EnvelopeException(EnvelopeException.Reason.MALFORMED, "Invalid iv or ciphertext length")
        }
        val plaintext = try {
            Crypto.aesGcmDecrypt(encKey, iv, aad(envelope.messageId, envelope.originDeviceId), ct)
        } catch (e: CryptoException) {
            throw EnvelopeException(EnvelopeException.Reason.DECRYPTION_FAILED, "Decryption failed", e)
        }
        val payload = try {
            json.decodeFromString(ClipboardPayload.serializer(), plaintext.toString(Charsets.UTF_8))
        } catch (e: SerializationException) {
            throw EnvelopeException(EnvelopeException.Reason.MALFORMED, "Invalid payload JSON", e)
        } catch (e: IllegalArgumentException) {
            throw EnvelopeException(EnvelopeException.Reason.MALFORMED, "Invalid payload JSON", e)
        }
        if (payload.type != ClipboardPayload.TYPE_TEXT) {
            throw EnvelopeException(EnvelopeException.Reason.UNSUPPORTED_TYPE, "Unsupported type ${payload.type}")
        }
        if (payload.contentHash != Content.hash(payload.content)) {
            throw EnvelopeException(EnvelopeException.Reason.HASH_MISMATCH, "contentHash does not match content")
        }
        return payload
    }

    fun encode(envelope: Envelope): String = json.encodeToString(Envelope.serializer(), envelope)

    fun decode(body: String): Envelope = try {
        json.decodeFromString(Envelope.serializer(), body)
    } catch (e: SerializationException) {
        throw EnvelopeException(EnvelopeException.Reason.MALFORMED, "Invalid envelope JSON", e)
    } catch (e: IllegalArgumentException) {
        throw EnvelopeException(EnvelopeException.Reason.MALFORMED, "Invalid envelope JSON", e)
    }
}
