package com.n3k0chan.klip.core

import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.boolean
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import java.io.File
import kotlin.test.Test
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertTrue

/** Checks the Kotlin core against protocol/test-vectors.json (reference: tools/gen_test_vectors.py). */
class TestVectorsTest {
    private val v: JsonObject = Json.parseToJsonElement(
        File(System.getProperty("klip.vectors") ?: "../protocol/test-vectors.json").readText()
    ).jsonObject

    private fun JsonObject.str(key: String) = this[key]!!.jsonPrimitive.content

    @Test
    fun normalization() {
        for (e in v["normalization"]!!.jsonArray.map { it.jsonObject }) {
            val input = e.str("input")
            assertEquals(e.str("normalized"), Content.normalize(input), "normalize(${input.debug()})")
            assertEquals(e.str("contentHash"), Content.hash(input), "hash(${input.debug()})")
            assertEquals(e["isBlank"]!!.jsonPrimitive.boolean, Content.isBlank(input), "isBlank(${input.debug()})")
        }
    }

    @Test
    fun pairing() {
        val p = v["pairing"]!!.jsonObject
        val pubI = p.str("initiatorPublic").base64ToBytes()
        val pubR = p.str("responderPublic").base64ToBytes()
        val nonceI = p.str("initiatorNonce").base64ToBytes()
        val nonceR = p.str("responderNonce").base64ToBytes()
        val kpI = Pairing.keyPairFromRaw(p.str("initiatorPrivate").hexToBytes(), pubI)
        val kpR = Pairing.keyPairFromRaw(p.str("responderPrivate").hexToBytes(), pubR)

        assertContentEquals(pubI, kpI.publicKey)
        val sharedI = Pairing.sharedSecret(kpI, pubR)
        val sharedR = Pairing.sharedSecret(kpR, pubI)
        assertEquals(p.str("sharedSecret"), sharedI.toHex())
        assertEquals(p.str("sharedSecret"), sharedR.toHex())

        val secret = Pairing.derivePairSecret(sharedI, nonceI, nonceR)
        assertEquals(p.str("pairSecret"), secret.toHex())
        val keys = PairKeys.derive(secret)
        assertEquals(p.str("macKey"), keys.macKey.toHex())
        assertEquals(p.str("encKey"), keys.encKey.toHex())
        assertEquals(p.str("pin"), Pairing.pin(pubI, pubR, nonceI, nonceR))
    }

    @Test
    fun randomSecretDerivation() {
        val r = v["randomSecret"]!!.jsonObject
        val keys = PairKeys.derive(r.str("pairSecret").hexToBytes())
        assertEquals(r.str("macKey"), keys.macKey.toHex())
        assertEquals(r.str("encKey"), keys.encKey.toHex())
    }

    @Test
    fun invalidPublicKeysAreRejected() {
        val own = Pairing.generateKeyPair()
        for (e in v["invalidPublicKeys"]!!.jsonArray.map { it.jsonObject }) {
            val key = e.str("publicKey").base64ToBytes()
            assertFalse(Pairing.isValidPublicKey(key), e.str("reason"))
            assertFailsWith<CryptoException>(e.str("reason")) { Pairing.sharedSecret(own, key) }
        }
    }

    @Test
    fun signatures() {
        for (e in v["signatures"]!!.jsonArray.map { it.jsonObject }) {
            val body = e.str("body").base64ToBytes()
            val args = arrayOf(e.str("method"), e.str("path"), e.str("timestamp"), e.str("nonce"))
            assertEquals(e.str("canonical"), RequestSigner.canonical(args[0], args[1], args[2], args[3], body))
            assertEquals(
                e.str("signature"),
                RequestSigner.signature(e.str("macKey").hexToBytes(), args[0], args[1], args[2], args[3], body),
            )
        }
    }

    @Test
    fun aesGcm() {
        for (e in v["aesGcm"]!!.jsonArray.map { it.jsonObject }) {
            val key = e.str("encKey").hexToBytes()
            val iv = e.str("iv").base64ToBytes()
            val aad = e.str("aad").toByteArray()
            val pt = e.str("plaintext").toByteArray()
            assertEquals(e.str("ciphertext"), Crypto.aesGcmEncrypt(key, iv, aad, pt).toBase64())
            assertContentEquals(pt, Crypto.aesGcmDecrypt(key, iv, aad, e.str("ciphertext").base64ToBytes()))
        }
    }

    @Test
    fun envelopeOpen() {
        val env = v["envelope"]!!.jsonObject
        val key = env.str("encKey").hexToBytes()
        val valid = env["valid"]!!.jsonObject
        val payload = EnvelopeCodec.open(key, EnvelopeCodec.decode(valid["envelope"].toString()))
        val expected = valid["payload"]!!.jsonObject
        assertEquals(expected.str("content"), payload.content)
        assertEquals(expected.str("contentHash"), payload.contentHash)
        assertEquals(expected.str("originDeviceName"), payload.originDeviceName)
        assertEquals(expected["isTest"]!!.jsonPrimitive.boolean, payload.isTest)

        for (bad in env["mustReject"]!!.jsonArray.map { it.jsonObject }) {
            assertFailsWith<EnvelopeException>(bad.str("reason")) {
                EnvelopeCodec.open(key, EnvelopeCodec.decode(bad["envelope"].toString()))
            }
        }
    }

    @Test
    fun introductionSecret() {
        val e = v["introduction"]!!.jsonObject
        val key = e.str("encKey").hexToBytes()
        val introducer = e.str("introducerId")
        val issuedAt = e["issuedAt"]!!.jsonPrimitive.content.toLong()
        val member = MemberInfo(e.str("memberId"), "M", "android", 8765, "10.0.0.2")
        assertEquals(e.str("aad"), Introductions.aad(introducer, member.deviceId, issuedAt).toString(Charsets.UTF_8))
        val sealed = Introductions.seal(key, introducer, member, issuedAt, e.str("secret").hexToBytes(), e.str("iv").base64ToBytes())
        assertEquals(e.str("ciphertext"), sealed.ciphertext)
        assertEquals(e.str("secret"), Introductions.open(key, introducer, sealed).toHex())
        assertFailsWith<CryptoException> { Introductions.open(key, "someone-else", sealed) }
    }

    @Test
    fun secretVersionOrdering() {
        assertTrue(SecretVersion(2, "a") > SecretVersion(1, "z"))
        assertTrue(SecretVersion(1, "b") > SecretVersion(1, "a"))
        assertTrue(SecretVersion(1, "Z") < SecretVersion(1, "a"), "ordinal, not case-insensitive")
    }

    @Test
    fun envelopeRoundTrip() {
        val keys = PairKeys.derive(PairKeys.newRandomSecret())
        val sealed = EnvelopeCodec.seal(keys.encKey, "dev-a", ClipboardPayload.text("hola\r\nmundo", "PC", isTest = true))
        val opened = EnvelopeCodec.open(keys.encKey, EnvelopeCodec.decode(EnvelopeCodec.encode(sealed)))
        assertEquals("hola\r\nmundo", opened.content)
        assertTrue(opened.isTest)
    }

    private fun String.debug() = map { if (it.code in 32..126) "$it" else "\\u%04x".format(it.code) }.joinToString("")
}
