package com.n3k0chan.klip.core

import kotlin.test.Test
import kotlin.test.assertEquals

class RequestVerifierTest {
    private val macKey = PairKeys.derive(ByteArray(32) { 7 }).macKey
    private var now = 1_763_930_000_000L
    private val verifier = RequestVerifier(macKeyFor = { if (it == "dev-a") macKey else null }, clock = { now })
    private val body = """{"a":1}""".toByteArray()

    private fun signed(ts: Long = now, device: String = "dev-a", key: ByteArray = macKey) =
        RequestSigner.sign(key, device, "POST", "/clipboard", body, nowMs = ts).toMap()

    @Test
    fun acceptsValidRequestOnce() {
        val h = signed()
        assertEquals(VerifyResult.OK, verifier.verify(h, "POST", "/clipboard", body))
        assertEquals(VerifyResult.REPLAY, verifier.verify(h, "POST", "/clipboard", body))
    }

    @Test
    fun headerNamesAreCaseInsensitive() {
        val h = signed().mapKeys { it.key.lowercase() }
        assertEquals(VerifyResult.OK, verifier.verify(h, "POST", "/clipboard", body))
    }

    @Test
    fun rejectsTamperedBodyPathOrMethod() {
        assertEquals(VerifyResult.BAD_SIGNATURE, verifier.verify(signed(), "POST", "/clipboard", "x".toByteArray()))
        assertEquals(VerifyResult.BAD_SIGNATURE, verifier.verify(signed(), "POST", "/unpair", body))
        assertEquals(VerifyResult.BAD_SIGNATURE, verifier.verify(signed(), "GET", "/clipboard", body))
    }

    @Test
    fun rejectsWrongKeyUnknownDeviceAndClockSkew() {
        assertEquals(VerifyResult.BAD_SIGNATURE, verifier.verify(signed(key = ByteArray(32)), "POST", "/clipboard", body))
        assertEquals(VerifyResult.UNKNOWN_DEVICE, verifier.verify(signed(device = "dev-x"), "POST", "/clipboard", body))
        assertEquals(VerifyResult.EXPIRED, verifier.verify(signed(ts = now - 30_001), "POST", "/clipboard", body))
        assertEquals(VerifyResult.EXPIRED, verifier.verify(signed(ts = now + 30_001), "POST", "/clipboard", body))
        assertEquals(VerifyResult.OK, verifier.verify(signed(ts = now - 30_000), "POST", "/clipboard", body))
    }

    @Test
    fun rejectsMalformedHeaders() {
        val h = signed().toMutableMap()
        h.remove(Protocol.HEADER_NONCE)
        assertEquals(VerifyResult.MALFORMED, verifier.verify(h, "POST", "/clipboard", body))
        val bad = signed() + (Protocol.HEADER_TIMESTAMP to "abc")
        assertEquals(VerifyResult.MALFORMED, verifier.verify(bad, "POST", "/clipboard", body))
    }

    @Test
    fun nonceCanBeReusedAfterTtl() {
        val nonce = Crypto.randomBytes(16).toBase64()
        val first = RequestSigner.sign(macKey, "dev-a", "GET", "/ping", ByteArray(0), nowMs = now, nonce = nonce).toMap()
        assertEquals(VerifyResult.OK, verifier.verify(first, "GET", "/ping", ByteArray(0)))
        now += Protocol.NONCE_TTL_MS + 1
        val again = RequestSigner.sign(macKey, "dev-a", "GET", "/ping", ByteArray(0), nowMs = now, nonce = nonce).toMap()
        assertEquals(VerifyResult.OK, verifier.verify(again, "GET", "/ping", ByteArray(0)))
    }
}

class SyncStateTest {
    private val state = SyncState("local")

    @Test
    fun outgoingRulesInOrder() {
        assertEquals(OutgoingDecision.SKIP_DISABLED, state.evaluateOutgoing("x", isSensitive = true, syncEnabled = false))
        assertEquals(OutgoingDecision.SKIP_SENSITIVE, state.evaluateOutgoing("x", isSensitive = true))
        assertEquals(OutgoingDecision.SKIP_BLANK, state.evaluateOutgoing(" \n\t"))
        assertEquals(OutgoingDecision.SEND, state.evaluateOutgoing("hola"))
        state.markSent(Content.hash("hola"))
        assertEquals(OutgoingDecision.SKIP_ALREADY_SENT, state.evaluateOutgoing("hola"))
        state.markSent(Content.hash("café\nx"))
        assertEquals(OutgoingDecision.SKIP_ALREADY_SENT, state.evaluateOutgoing("café\r\nx"), "same after normalization")
        assertEquals(OutgoingDecision.SEND, state.evaluateOutgoing("hola", isTest = true))
    }

    @Test
    fun receivedContentIsNotSentBackEvenWithCrlf() {
        assertEquals(IncomingDecision.ACCEPT, state.acceptIncoming("pc", "m1", Content.hash("a\nb")))
        // Windows rewrites \n as \r\n when placing it on the clipboard.
        assertEquals(OutgoingDecision.SKIP_JUST_RECEIVED, state.evaluateOutgoing("a\r\nb"))
    }

    @Test
    fun incomingRules() {
        assertEquals(IncomingDecision.IGNORE_SELF, state.acceptIncoming("local", "m1", "h"))
        assertEquals(IncomingDecision.ACCEPT, state.acceptIncoming("pc", "m1", "h"))
        assertEquals(IncomingDecision.IGNORE_DUPLICATE, state.acceptIncoming("pc", "m1", "h"))
    }

    @Test
    fun processedIdsAreBounded() {
        repeat(Protocol.PROCESSED_IDS_CAPACITY + 1) { state.acceptIncoming("pc", "m$it", "h") }
        assertEquals(Protocol.PROCESSED_IDS_CAPACITY, state.snapshot().processedMessageIds.size)
        assertEquals(IncomingDecision.ACCEPT, state.acceptIncoming("pc", "m0", "h"), "oldest id evicted")
    }

    @Test
    fun snapshotRoundTrip() {
        state.markSent("s")
        state.acceptIncoming("pc", "m1", "r")
        val restored = SyncState("local", state.snapshot())
        assertEquals(state.snapshot(), restored.snapshot())
    }
}
