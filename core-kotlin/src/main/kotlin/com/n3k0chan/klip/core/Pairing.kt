package com.n3k0chan.klip.core

import java.math.BigInteger
import java.security.AlgorithmParameters
import java.security.KeyFactory
import java.security.KeyPair
import java.security.KeyPairGenerator
import java.security.PrivateKey
import java.security.interfaces.ECPublicKey
import java.security.spec.ECGenParameterSpec
import java.security.spec.ECParameterSpec
import java.security.spec.ECPoint
import java.security.spec.ECPrivateKeySpec
import java.security.spec.ECPublicKeySpec
import javax.crypto.KeyAgreement

/** Ephemeral P-256 key pair used for one pairing attempt. */
class PairingKeyPair internal constructor(internal val keyPair: KeyPair) {
    /** SEC1 uncompressed public key (65 bytes). */
    val publicKey: ByteArray = Pairing.encodePublicKey(keyPair.public as ECPublicKey)
    internal val privateKey: PrivateKey get() = keyPair.private
}

/** Pairing key agreement, key derivation and PIN. See docs/protocol.md §4. */
object Pairing {
    private val ZERO_SALT = ByteArray(32)

    private val P256: ECParameterSpec by lazy {
        AlgorithmParameters.getInstance("EC").run {
            init(ECGenParameterSpec("secp256r1"))
            getParameterSpec(ECParameterSpec::class.java)
        }
    }

    fun generateKeyPair(): PairingKeyPair {
        val gen = KeyPairGenerator.getInstance("EC")
        gen.initialize(ECGenParameterSpec("secp256r1"))
        return PairingKeyPair(gen.generateKeyPair())
    }

    fun newNonce(): ByteArray = Crypto.randomBytes(Protocol.PAIRING_NONCE_BYTES)

    /** Rebuilds a key pair from a raw private scalar and its public key (tests only). */
    fun keyPairFromRaw(privateScalar: ByteArray, publicKey: ByteArray): PairingKeyPair {
        val kf = KeyFactory.getInstance("EC")
        val priv = kf.generatePrivate(ECPrivateKeySpec(BigInteger(1, privateScalar), P256))
        val pub = kf.generatePublic(ECPublicKeySpec(decodePoint(publicKey), P256))
        return PairingKeyPair(KeyPair(pub, priv))
    }

    /** Raw ECDH shared secret (X coordinate, 32 bytes). Validates [peerPublicKey]. */
    fun sharedSecret(own: PairingKeyPair, peerPublicKey: ByteArray): ByteArray {
        val peer = KeyFactory.getInstance("EC").generatePublic(ECPublicKeySpec(decodePoint(peerPublicKey), P256))
        val ka = KeyAgreement.getInstance("ECDH")
        ka.init(own.privateKey)
        ka.doPhase(peer, true)
        return ka.generateSecret()
    }

    fun derivePairSecret(shared: ByteArray, initiatorNonce: ByteArray, responderNonce: ByteArray): ByteArray =
        Crypto.hkdf(shared, initiatorNonce + responderNonce, "klip-pair-v1".toByteArray(), 32)

    fun pin(initiatorPublic: ByteArray, responderPublic: ByteArray, initiatorNonce: ByteArray, responderNonce: ByteArray): String {
        val h = Crypto.sha256(initiatorPublic + responderPublic + initiatorNonce + responderNonce)
        val n = ((h[0].toLong() and 0xFF) shl 24) or ((h[1].toLong() and 0xFF) shl 16) or
            ((h[2].toLong() and 0xFF) shl 8) or (h[3].toLong() and 0xFF)
        return (n % 1_000_000).toString().padStart(6, '0')
    }

    /** Returns true if [publicKey] is a valid uncompressed P-256 point. */
    fun isValidPublicKey(publicKey: ByteArray): Boolean =
        try { decodePoint(publicKey); true } catch (_: CryptoException) { false }

    internal fun encodePublicKey(key: ECPublicKey): ByteArray =
        byteArrayOf(0x04) + key.w.affineX.toFixed32() + key.w.affineY.toFixed32()

    private fun decodePoint(encoded: ByteArray): ECPoint {
        if (encoded.size != 65 || encoded[0] != 0x04.toByte()) throw CryptoException("Public key must be 65-byte uncompressed SEC1")
        val x = BigInteger(1, encoded.copyOfRange(1, 33))
        val y = BigInteger(1, encoded.copyOfRange(33, 65))
        val curve = P256.curve
        val p = (curve.field as java.security.spec.ECFieldFp).p
        if (x >= p || y >= p) throw CryptoException("Coordinate out of range")
        // y^2 == x^3 + a*x + b (mod p)
        val lhs = y.modPow(BigInteger.valueOf(2), p)  // BigInteger.TWO is not available on older Android
        val rhs = x.modPow(BigInteger.valueOf(3), p).add(curve.a.multiply(x)).add(curve.b).mod(p)
        if (lhs != rhs) throw CryptoException("Point is not on P-256")
        return ECPoint(x, y)
    }

    private fun BigInteger.toFixed32(): ByteArray {
        val raw = toByteArray()
        return when {
            raw.size == 32 -> raw
            raw.size > 32 -> raw.copyOfRange(raw.size - 32, raw.size)
            else -> ByteArray(32 - raw.size) + raw
        }
    }

    internal fun zeroSalt(): ByteArray = ZERO_SALT.copyOf()
}

/** Working keys derived from a pair secret. See docs/protocol.md §4.3. */
class PairKeys(val macKey: ByteArray, val encKey: ByteArray) {
    companion object {
        fun derive(pairSecret: ByteArray): PairKeys {
            require(pairSecret.size == 32) { "Pair secret must be 32 bytes" }
            return PairKeys(
                macKey = Crypto.hkdf(pairSecret, Pairing.zeroSalt(), "klip-mac".toByteArray(), 32),
                encKey = Crypto.hkdf(pairSecret, Pairing.zeroSalt(), "klip-enc".toByteArray(), 32),
            )
        }

        /** Random pair secret for group introductions. */
        fun newRandomSecret(): ByteArray = Crypto.randomBytes(32)
    }
}
