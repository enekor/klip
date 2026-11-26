package com.n3k0chan.klip.node

import com.n3k0chan.klip.core.OutgoingDecision
import com.n3k0chan.klip.core.PairKeys
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.filterIsInstance
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import java.io.File
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.ServerSocket
import java.nio.file.Files
import java.util.Collections
import kotlin.test.AfterTest
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertTrue
import kotlin.test.fail

class MemoryClipboard : ClipboardWriter {
    val writes: MutableList<String> = Collections.synchronizedList(mutableListOf())
    val images: MutableList<Pair<ByteArray, String>> = Collections.synchronizedList(mutableListOf())
    override fun writeText(text: String) {
        writes += text
    }

    override fun writeImage(data: ByteArray, mimeType: String) {
        images += data to mimeType
    }
}

/** Real nodes talking over HTTP/UDP on localhost. Mirrors the C# SyncNodeTests and PairingTests. */
class NodeTest {
    private val dir: File = Files.createTempDirectory("klip-node").toFile()
    private val nodes = mutableListOf<SyncNode>()
    private val clips = mutableMapOf<SyncNode, MemoryClipboard>()

    private fun freeTcpPort() = ServerSocket(0).use { it.localPort }
    private fun freeUdpPort() = DatagramSocket(0, InetAddress.getLoopbackAddress()).use { it.localPort }

    private suspend fun newNode(name: String, autoAccept: Boolean = true): SyncNode {
        val clip = MemoryClipboard()
        val port = freeTcpPort()
        val node = SyncNode(ConfigStore(File(dir, "$name.json")), PlainSecretProtector(), clip,
            defaults = { NodeConfig(deviceName = name, platform = "test", httpPort = port) })
        node.pairingOpen = true
        if (autoAccept) {
            CoroutineScope(Dispatchers.IO).launch {
                node.events.filterIsInstance<NodeEvent.PairingRequested>().collect { node.respondToPairing(it.pairing.sessionId, true) }
            }
        }
        node.acquireServer()
        nodes += node
        clips[node] = clip
        return node
    }

    private suspend fun pair(initiator: SyncNode, responder: SyncNode): PairingOutcome {
        val p = initiator.startPairing("127.0.0.1", responder.httpPort).getOrElse { fail(it.message) }
        return initiator.confirmPairing(p)
    }

    private suspend fun waitUntil(what: String, timeoutMs: Long = 5_000, condition: () -> Boolean) {
        val deadline = System.currentTimeMillis() + timeoutMs
        while (!condition()) {
            if (System.currentTimeMillis() > deadline) fail("Timed out waiting for: $what")
            delay(50)
        }
    }

    private fun SyncNode.knows(other: SyncNode) = peers.value.any { it.deviceId == other.deviceId }

    @AfterTest
    fun cleanup() {
        nodes.forEach { it.close() }
        dir.deleteRecursively()
    }

    @Test
    fun manualPairDeliversAndDoesNotBounce() = runBlocking {
        val a = newNode("A")
        val b = newNode("B")
        val secret = PairKeys.newRandomSecret()
        a.addOrUpdatePeer(b.deviceId, "B", "127.0.0.1", b.httpPort, secret)
        b.addOrUpdatePeer(a.deviceId, "A", "127.0.0.1", a.httpPort, secret)

        assertTrue(a.sendClipboard("hola\nmundo").anyOk)
        assertEquals(listOf("hola\nmundo"), clips[b]!!.writes)
        assertEquals(OutgoingDecision.SKIP_JUST_RECEIVED, b.sendClipboard("hola\r\nmundo").decision)
        assertEquals(OutgoingDecision.SKIP_ALREADY_SENT, a.sendClipboard("hola\nmundo").decision)
        assertEquals(OutgoingDecision.SKIP_SENSITIVE, a.sendClipboard("pw", isSensitive = true).decision)

        val ping = a.ping(b.deviceId)
        assertTrue(ping.ok, ping.error)
        assertEquals("B", ping.info?.deviceName)

        // The UI can show exactly what arrived.
        assertEquals("hola\nmundo", b.lastReceived.value?.content)
        assertEquals("hola ⏎ mundo", b.lastReceived.value?.preview())
        assertTrue(a.sendTest().anyOk)
        val last = b.lastReceived.value!!
        assertTrue(last.isTest)
        assertEquals("A", last.from)
        assertTrue(last.content.startsWith("Prueba de conexión desde A - "))
        assertEquals(clips[b]!!.writes.last(), last.content)

        // Sensitive text only travels after the explicit opt-in.
        a.sendSensitive = true
        assertTrue(a.sendClipboard("pw", isSensitive = true).anyOk)
        assertEquals("pw", clips[b]!!.writes.last())
        a.sendSensitive = false
        assertEquals(OutgoingDecision.SKIP_SENSITIVE, a.sendClipboard("pw2", isSensitive = true).decision)
    }

    @Test
    fun imagesAreDeliveredAndDoNotBounce() = runBlocking {
        val a = newNode("A")
        val b = newNode("B")
        assertTrue(pair(a, b).ok)
        val jpeg = ByteArray(300_000).also { java.util.Random(1).nextBytes(it) }
        assertTrue(a.sendImage(jpeg, "image/jpeg").anyOk)
        val (data, mime) = clips[b]!!.images.single()
        assertTrue(jpeg.contentEquals(data))
        assertEquals("image/jpeg", mime)
        assertEquals("[Imagen JPEG, 292 KB]", b.lastReceived.value?.preview())
        assertEquals(OutgoingDecision.SKIP_JUST_RECEIVED, b.sendImage(jpeg, "image/jpeg").decision)
        assertEquals(OutgoingDecision.SKIP_ALREADY_SENT, a.sendImage(jpeg, "image/jpeg").decision)
    }

    @Test
    fun largeContentAndOfflinePeer() = runBlocking {
        val a = newNode("A")
        val b = newNode("B")
        val secret = PairKeys.newRandomSecret()
        a.addOrUpdatePeer(b.deviceId, "B", "127.0.0.1", b.httpPort, secret)
        b.addOrUpdatePeer(a.deviceId, "A", "127.0.0.1", a.httpPort, secret)
        a.addOrUpdatePeer("ghost", "Ghost", "127.0.0.1", freeTcpPort(), PairKeys.newRandomSecret())

        val big = "x".repeat(5 * 1024 * 1024)
        val start = System.currentTimeMillis()
        val outcome = a.sendClipboard(big)
        assertTrue(outcome.results.single { it.deviceId == b.deviceId }.ok)
        assertFalse(outcome.results.single { it.deviceId == "ghost" }.ok)
        assertTrue(System.currentTimeMillis() - start < 5_000)
        assertEquals(big.length, clips[b]!!.writes.single().length)
    }

    @Test
    fun pinPairingBothSides() = runBlocking {
        val a = newNode("A")
        val b = newNode("B", autoAccept = false)
        var responderPin: String? = null
        val job = CoroutineScope(Dispatchers.IO).launch {
            b.events.filterIsInstance<NodeEvent.PairingRequested>().collect {
                responderPin = it.pairing.pin
                delay(500) // the user takes a moment: confirm arrives first and waits
                b.respondToPairing(it.pairing.sessionId, true)
            }
        }
        val p = a.startPairing("127.0.0.1", b.httpPort).getOrThrow()
        assertTrue(Regex("^[0-9]{6}$").matches(p.pin))
        assertEquals(PairingStatus.ACCEPTED, a.confirmPairing(p).status)
        assertEquals(responderPin, p.pin)
        assertTrue(a.knows(b) && b.knows(a))
        assertTrue(a.sendClipboard("hola B").anyOk)
        assertTrue(b.sendClipboard("hola A").anyOk)
        job.cancel()
    }

    @Test
    fun rejectionAndClosedWindow() = runBlocking {
        val a = newNode("A")
        val b = newNode("B", autoAccept = false)
        val job = CoroutineScope(Dispatchers.IO).launch {
            b.events.filterIsInstance<NodeEvent.PairingRequested>().collect { b.respondToPairing(it.pairing.sessionId, false) }
        }
        assertEquals(PairingStatus.REJECTED, pair(a, b).status)
        assertFalse(a.knows(b) || b.knows(a))

        b.pairingOpen = false
        val err = a.startPairing("127.0.0.1", b.httpPort).exceptionOrNull()
        assertNotNull(err)
        assertTrue(err.message!!.contains("abierta la ventana"))
        job.cancel()
    }

    @Test
    fun groupIntroductionAndPendingRetry() = runBlocking {
        val a = newNode("A")
        val b = newNode("B")
        val c = newNode("C")
        val d = newNode("D")
        assertTrue(pair(a, b).ok)
        assertTrue(pair(c, a).ok)
        waitUntil("B and C introduced") { b.knows(c) && c.knows(b) }
        assertTrue(c.sendClipboard("de C").results.all { it.ok })

        b.releaseServer() // B offline
        assertTrue(pair(d, a).ok)
        waitUntil("D learns B and C") { d.knows(b) && d.knows(c) }
        delay(300)
        assertFalse(b.knows(d))
        b.acquireServer()
        assertTrue(b.ping(a.deviceId).ok) // contact with A triggers the pending delivery
        waitUntil("pending introduction delivered") { b.knows(d) }
        assertTrue(d.sendTest().results.all { it.ok })
    }

    @Test
    fun repairingAnExistingMemberKeepsTheGroupConsistent() = runBlocking {
        val a = newNode("A")
        val b = newNode("B")
        val c = newNode("C")
        assertTrue(pair(a, b).ok)
        assertTrue(pair(c, a).ok)
        waitUntil("mesh of 3") { listOf(a, b, c).all { it.peers.value.size == 2 } }
        // Pair again two devices that already know each other: introductions must not replace existing pairs.
        assertTrue(pair(c, b).ok)
        delay(500)
        for (x in listOf(a, b, c)) {
            val outcome = x.sendTest()
            assertEquals(2, outcome.results.size)
            assertTrue(outcome.results.all { it.ok }, "${x.deviceName}: ${outcome.results}")
        }
    }

    @Test
    fun expelAndUnpair() = runBlocking {
        val a = newNode("A")
        val b = newNode("B")
        val c = newNode("C")
        assertTrue(pair(a, b).ok)
        assertTrue(pair(a, c).ok)
        waitUntil("mesh") { b.knows(c) && c.knows(b) }

        a.expel(c.deviceId)
        assertFalse(a.knows(c) || b.knows(c))
        assertTrue(c.peers.value.isEmpty())
        a.unpair(b.deviceId)
        assertFalse(a.knows(b) || b.knows(a))
    }

    @Test
    fun radarFindsDevicesAndFixesAddress() = runBlocking {
        val a = newNode("A")
        val b = newNode("B")
        val udpA = freeUdpPort()
        val udpB = freeUdpPort()
        val loopback = { listOf(InetAddress.getLoopbackAddress()) }
        a.startDiscovery(DiscoveryOptions(listenPort = udpA, sendPort = udpB, targets = loopback, intervalMs = 100))
        b.startDiscovery(DiscoveryOptions(listenPort = udpB, sendPort = udpA, targets = loopback, intervalMs = 100))

        waitUntil("A sees B") { a.discovered.value.any { it.deviceId == b.deviceId && it.pairingOpen } }
        val seen = a.discovered.value.single { it.deviceId == b.deviceId }
        val p = a.startPairing(seen.host, seen.httpPort).getOrThrow()
        assertTrue(a.confirmPairing(p).ok)
        waitUntil("paired device leaves the radar list") { a.discovered.value.none { it.deviceId == b.deviceId } }

        a.setPeerEnabled(b.deviceId, true)
        a.updateHost(b.deviceId, "127.0.0.1", 1) // stale port
        waitUntil("port corrected from beacon") { a.peers.value.single { it.deviceId == b.deviceId }.port == b.httpPort }

        a.stopDiscovery()
        assertTrue(a.discovered.value.isEmpty())
    }

    @Test
    fun stateSurvivesRestart() = runBlocking {
        val a = newNode("A")
        val b = newNode("B")
        assertTrue(pair(a, b).ok)
        assertTrue(a.sendClipboard("persistente").anyOk)
        b.close()
        nodes.remove(b)
        val b2 = SyncNode(ConfigStore(File(dir, "B.json")), PlainSecretProtector(), MemoryClipboard())
        nodes += b2
        b2.acquireServer()
        assertTrue(a.ping(b2.deviceId).ok)
        assertEquals(OutgoingDecision.SKIP_JUST_RECEIVED, b2.sendClipboard("persistente").decision)
        withTimeout(1_000) { assertTrue(b2.ping(a.deviceId).ok) }
    }
}
