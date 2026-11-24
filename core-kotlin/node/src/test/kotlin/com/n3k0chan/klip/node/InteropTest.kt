package com.n3k0chan.klip.node

import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.filterIsInstance
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import java.io.File
import java.net.ServerSocket
import java.nio.file.Files
import java.util.concurrent.TimeUnit
import kotlin.test.AfterTest
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue
import kotlin.test.fail

/**
 * Kotlin node <-> real C# node (tools/Klip.DevPeer) over HTTP. This is what Android <-> Windows does.
 * Runs only when KLIP_DOTNET_DEVPEER points to the DevPeer executable; skipped otherwise.
 */
class InteropTest {
    private val devPeer: String? = System.getProperty("klip.dotnetDevPeer")
    private val dir: File = Files.createTempDirectory("klip-interop").toFile()
    private val processes = mutableListOf<Process>()
    private val nodes = mutableListOf<SyncNode>()

    private fun freePort() = ServerSocket(0).use { it.localPort }

    /** A running DevPeer with its accumulated stdout. */
    private inner class DotNetPeer(name: String, val port: Int = freePort()) {
        val config = File(dir, "$name.json").path
        val output = StringBuffer()
        var process: Process? = null

        init {
            run("init", "--name", name, "--port", port.toString())
        }

        fun run(vararg args: String): Pair<Int, String> {
            val p = ProcessBuilder(listOf(devPeer!!, "--config", config) + args).redirectErrorStream(true).start()
            val out = p.inputStream.bufferedReader().readText()
            assertTrue(p.waitFor(90, TimeUnit.SECONDS), "DevPeer ${args.first()} timed out")
            return p.exitValue() to out
        }

        fun listen(vararg extra: String) {
            val p = ProcessBuilder(listOf(devPeer!!, "--config", config, "listen") + extra).redirectErrorStream(true).start()
            processes += p
            process = p
            Thread { p.inputStream.bufferedReader().forEachLine { output.appendLine(it) } }.apply { isDaemon = true }.start()
        }

        fun type(line: String) {
            process!!.outputStream.apply { write((line + "\n").toByteArray()); flush() }
        }
    }

    private suspend fun kotlinNode(name: String, autoAccept: Boolean = true): Pair<SyncNode, MemoryClipboard> {
        val clip = MemoryClipboard()
        val port = freePort()
        val node = SyncNode(ConfigStore(File(dir, "$name.json")), PlainSecretProtector(), clip,
            defaults = { NodeConfig(deviceName = name, platform = "android", httpPort = port) })
        node.pairingOpen = true
        if (autoAccept) {
            CoroutineScope(Dispatchers.IO).launch {
                node.events.filterIsInstance<NodeEvent.PairingRequested>().collect { node.respondToPairing(it.pairing.sessionId, true) }
            }
        }
        node.acquireServer()
        nodes += node
        return node to clip
    }

    private suspend fun waitUntil(what: String, timeoutMs: Long = 15_000, condition: () -> Boolean) {
        val deadline = System.currentTimeMillis() + timeoutMs
        while (!condition()) {
            if (System.currentTimeMillis() > deadline) fail("Timed out waiting for: $what")
            delay(100)
        }
    }

    @AfterTest
    fun cleanup() {
        processes.forEach { p ->
            p.destroy()
            if (!p.waitFor(3, TimeUnit.SECONDS)) p.destroyForcibly()
        }
        nodes.forEach { it.close() }
        dir.deleteRecursively()
    }

    @Test
    fun kotlinPairsWithDotNetAndBothDirectionsWork() = runBlocking {
        if (devPeer == null) return@runBlocking println("SKIPPED: set KLIP_DOTNET_DEVPEER to run the interop test")
        val (k, clip) = kotlinNode("Android")
        val win = DotNetPeer("Windows")
        win.listen("--accept-pairing")
        waitUntil("DevPeer listening") { win.output.contains("Escuchando") }

        // Kotlin initiates, C# responds
        val p = k.startPairing("127.0.0.1", win.port).getOrElse { fail(it.message) }
        assertTrue(k.confirmPairing(p).ok)
        assertTrue(win.output.contains("PIN ${p.pin}"), "Same PIN on both sides")

        // Kotlin -> C#
        val text = "hola desde Kotlin: ñ, €, 😀\r\nsegunda línea"
        assertTrue(k.sendClipboard(text).anyOk)
        waitUntil("C# received the text") { win.output.contains("segunda línea") }

        // C# -> Kotlin
        win.type("hola desde C# 😀")
        waitUntil("Kotlin received the text") { clip.writes.contains("hola desde C# 😀") }

        // Test message and ping
        assertTrue(k.sendTest().anyOk)
        waitUntil("C# shows the test notification") { win.output.contains("Prueba recibida correctamente desde Android") }
        assertEquals("Windows", k.ping(p.deviceId).info?.deviceName)
    }

    @Test
    fun dotNetPairsWithKotlinAndGroupSpansLanguages() = runBlocking {
        if (devPeer == null) return@runBlocking println("SKIPPED: set KLIP_DOTNET_DEVPEER to run the interop test")
        val (k, _) = kotlinNode("Phone")
        val pc = DotNetPeer("PC")
        val tablet = DotNetPeer("Tablet")

        // C# initiates, Kotlin responds (twice): the Kotlin node introduces PC <-> Tablet.
        val (code1, out1) = pc.run("pair", "--host", "127.0.0.1", "--port", k.httpPort.toString(), "--yes")
        assertEquals(0, code1, out1)
        pc.listen()
        waitUntil("PC listening") { pc.output.contains("Escuchando") }

        val (code2, out2) = tablet.run("pair", "--host", "127.0.0.1", "--port", k.httpPort.toString(), "--yes")
        assertEquals(0, code2, out2)
        assertTrue(out2.contains("Nuevo miembro del grupo: PC"), "Tablet learned about PC from the Kotlin node:\n$out2")

        // Tablet (C#) -> PC (C#) with a secret generated by the Kotlin node.
        val (code3, out3) = tablet.run("send", "de la tablet al PC")
        assertEquals(0, code3, out3)
        waitUntil("PC received from Tablet") { pc.output.contains("de la tablet al PC") }
        assertEquals(2, k.peers.value.size)
    }
}
