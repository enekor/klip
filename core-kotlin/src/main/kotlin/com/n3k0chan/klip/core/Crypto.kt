package com.n3k0chan.klip.core

import java.security.MessageDigest
import java.security.SecureRandom
import javax.crypto.AEADBadTagException
import javax.crypto.Cipher
import javax.crypto.Mac
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.SecretKeySpec

/** Low-level primitives. Only JCA APIs available on Android API 29+ are used. */
object Crypto {
    private val random = SecureRandom()

    fun randomBytes(n: Int): ByteArray = ByteArray(n).also(random::nextBytes)

    fun sha256(data: ByteArray): ByteArray = MessageDigest.getInstance("SHA-256").digest(data)

    fun hmacSha256(key: ByteArray, data: ByteArray): ByteArray {
        val mac = Mac.getInstance("HmacSHA256")
        mac.init(SecretKeySpec(key, "HmacSHA256"))
        return mac.doFinal(data)
    }

    /** HKDF-SHA256 (RFC 5869). [salt] must be non-empty (JCA rejects empty HMAC keys). */
    fun hkdf(ikm: ByteArray, salt: ByteArray, info: ByteArray, length: Int): ByteArray {
        require(salt.isNotEmpty()) { "Use an explicit zero salt instead of an empty one" }
        require(length in 1..255 * 32)
        val prk = hmacSha256(salt, ikm)
        val out = ByteArray(length)
        var t = ByteArray(0)
        var offset = 0
        var counter = 1
        while (offset < length) {
            t = hmacSha256(prk, t + info + counter.toByte())
            val n = minOf(t.size, length - offset)
            t.copyInto(out, offset, 0, n)
            offset += n
            counter++
        }
        return out
    }

    fun constantTimeEquals(a: ByteArray, b: ByteArray): Boolean = MessageDigest.isEqual(a, b)

    /** AES-256-GCM. Returns ct || tag. */
    fun aesGcmEncrypt(key: ByteArray, iv: ByteArray, aad: ByteArray, plaintext: ByteArray): ByteArray {
        val cipher = gcm(Cipher.ENCRYPT_MODE, key, iv)
        cipher.updateAAD(aad)
        return cipher.doFinal(plaintext)
    }

    /** AES-256-GCM. [ciphertext] is ct || tag. Throws [CryptoException] if authentication fails. */
    fun aesGcmDecrypt(key: ByteArray, iv: ByteArray, aad: ByteArray, ciphertext: ByteArray): ByteArray {
        val cipher = gcm(Cipher.DECRYPT_MODE, key, iv)
        cipher.updateAAD(aad)
        try {
            return cipher.doFinal(ciphertext)
        } catch (e: AEADBadTagException) {
            throw CryptoException("AES-GCM authentication failed", e)
        }
    }

    private fun gcm(mode: Int, key: ByteArray, iv: ByteArray): Cipher {
        require(key.size == 32) { "AES-256 key must be 32 bytes" }
        require(iv.size == Protocol.AES_GCM_IV_BYTES) { "IV must be ${Protocol.AES_GCM_IV_BYTES} bytes" }
        return Cipher.getInstance("AES/GCM/NoPadding").apply {
            init(mode, SecretKeySpec(key, "AES"), GCMParameterSpec(Protocol.AES_GCM_TAG_BYTES * 8, iv))
        }
    }
}

class CryptoException(message: String, cause: Throwable? = null) : Exception(message, cause)
