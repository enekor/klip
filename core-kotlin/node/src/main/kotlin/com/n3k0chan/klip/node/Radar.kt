package com.n3k0chan.klip.node

import com.n3k0chan.klip.core.Beacon
import com.n3k0chan.klip.core.Protocol
import com.n3k0chan.klip.core.ProtocolJson
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.serialization.SerializationException
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.Inet4Address
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.NetworkInterface
import java.net.SocketException
import java.net.SocketTimeoutException
import java.util.concurrent.ConcurrentHashMap

/** A device seen by the radar that is not paired yet. */
data class DiscoveredDevice(
    val deviceId: String,
    val deviceName: String,
    val platform: String,
    val host: String,
    val httpPort: Int,
    val pairingOpen: Boolean,
    val lastSeenMs: Long,
)

/** Radar settings. Defaults follow docs/protocol.md §8.6; tests override ports and targets. */
data class DiscoveryOptions(
    val listenPort: Int = Protocol.DISCOVERY_PORT,
    val sendPort: Int = Protocol.DISCOVERY_PORT,
    /** Destination addresses; null = 255.255.255.255 plus every interface's broadcast address. */
    val targets: (() -> List<InetAddress>)? = null,
    val intervalMs: Long = Protocol.BEACON_INTERVAL_MS,
)

/**
 * UDP broadcast radar (docs/protocol.md §8.6). Active only while the configuration UI is visible.
 * On Android the caller must hold a WifiManager.MulticastLock while it runs.
 */
internal class Radar(private val node: SyncNode) {
    private val found = ConcurrentHashMap<String, DiscoveredDevice>()
    private val verifying = ConcurrentHashMap.newKeySet<String>()
    private val _discovered = MutableStateFlow<List<DiscoveredDevice>>(emptyList())
    val discovered: StateFlow<List<DiscoveredDevice>> = _discovered.asStateFlow()

    private var socket: DatagramSocket? = null
    private var jobs: List<Job> = emptyList()

    @Synchronized
    fun start(options: DiscoveryOptions) {
        if (socket != null) return
        val s = try {
            DatagramSocket(null).apply {
                reuseAddress = true
                broadcast = true
                soTimeout = 500
                bind(InetSocketAddress(options.listenPort))
            }
        } catch (e: SocketException) {
            node.log("Radar: no se pudo abrir el puerto UDP: ${e.message}")
            return
        }
        socket = s
        jobs = listOf(
            node.scope.launch { sendLoop(s, options) },
            node.scope.launch { receiveLoop(s) },
            node.scope.launch { expireLoop() },
        )
    }

    @Synchronized
    fun stop() {
        jobs.forEach { it.cancel() }
        jobs = emptyList()
        socket?.close()
        socket = null
        found.clear()
        publish()
    }

    private suspend fun sendLoop(s: DatagramSocket, options: DiscoveryOptions) {
        while (node.scope.isActive && !s.isClosed) {
            val beacon = Beacon(1, node.deviceId, node.deviceName, node.platform, node.httpPort, node.pairingOpen)
            val bytes = ProtocolJson.encodeToString(Beacon.serializer(), beacon).toByteArray()
            for (target in options.targets?.invoke() ?: broadcastAddresses()) {
                try {
                    s.send(DatagramPacket(bytes, bytes.size, target, options.sendPort))
                } catch (_: Exception) {
                    // interface went away or socket closed: ignore
                }
            }
            delay(options.intervalMs)
        }
    }

    private fun receiveLoop(s: DatagramSocket) {
        val buffer = ByteArray(2048)
        while (!s.isClosed) {
            val packet = DatagramPacket(buffer, buffer.size)
            try {
                s.receive(packet)
            } catch (_: SocketTimeoutException) {
                continue
            } catch (_: Exception) {
                return
            }
            val beacon = try {
                ProtocolJson.decodeFromString(Beacon.serializer(), String(packet.data, packet.offset, packet.length, Charsets.UTF_8))
            } catch (_: SerializationException) {
                continue
            } catch (_: IllegalArgumentException) {
                continue
            }
            if (beacon.klip != 1 || beacon.deviceId.isBlank() || beacon.httpPort !in 1..65535) continue
            onBeacon(beacon, packet.address.hostAddress ?: continue)
        }
    }

    private suspend fun expireLoop() {
        while (true) {
            delay(1_000)
            val now = node.now
            if (found.entries.removeIf { now - it.value.lastSeenMs > Protocol.DISCOVERY_EXPIRY_MS }) publish()
        }
    }

    private fun onBeacon(b: Beacon, ip: String) {
        if (b.deviceId == node.deviceId) return
        val known = node.peerWithKeys(b.deviceId)
        if (known != null) {
            if (found.remove(b.deviceId) != null) publish()
            val (peer, k) = known
            if ((peer.host != ip || peer.port != b.httpPort) && verifying.add(b.deviceId)) {
                // A beacon is not authenticated: confirm the new address with a signed ping first.
                node.scope.launch {
                    try {
                        val ping = node.client.ping(ip, b.httpPort, k, node.deviceId)
                        if (ping.ok && ping.info?.deviceId == b.deviceId) node.updateHost(b.deviceId, ip, b.httpPort)
                    } finally {
                        verifying.remove(b.deviceId)
                    }
                }
            }
            return
        }
        val device = DiscoveredDevice(b.deviceId, b.deviceName, b.platform, ip, b.httpPort, b.pairingOpen, node.now)
        val old = found.put(b.deviceId, device)
        if (old == null || old.copy(lastSeenMs = device.lastSeenMs) != device) publish()
    }

    private fun publish() {
        _discovered.value = found.values.sortedBy { it.deviceName.lowercase() }
    }

    companion object {
        /** 255.255.255.255 plus the directed broadcast address of each active IPv4 interface. */
        fun broadcastAddresses(): List<InetAddress> {
            val result = linkedSetOf<InetAddress>(InetAddress.getByName("255.255.255.255"))
            try {
                for (nic in NetworkInterface.getNetworkInterfaces()) {
                    if (!nic.isUp || nic.isLoopback) continue
                    for (ia in nic.interfaceAddresses) {
                        if (ia.address is Inet4Address) ia.broadcast?.let { result += it }
                    }
                }
            } catch (_: SocketException) {
            }
            return result.toList()
        }
    }
}
