package com.n3k0chan.klip.node

import com.n3k0chan.klip.core.ClipboardPayload
import com.n3k0chan.klip.core.Envelope
import com.n3k0chan.klip.core.EnvelopeCodec
import com.n3k0chan.klip.core.EnvelopeException
import com.n3k0chan.klip.core.IncomingDecision
import com.n3k0chan.klip.core.MemberInfo
import com.n3k0chan.klip.core.OutgoingDecision
import com.n3k0chan.klip.core.PairKeys
import com.n3k0chan.klip.core.PingInfo
import com.n3k0chan.klip.core.RequestVerifier
import com.n3k0chan.klip.core.SecretVersion
import com.n3k0chan.klip.core.SyncState
import com.n3k0chan.klip.core.base64ToBytes
import com.n3k0chan.klip.core.toBase64
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitAll
import kotlinx.coroutines.cancel
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withTimeoutOrNull
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap

/** Writes received text to the local clipboard. */
fun interface ClipboardWriter {
    fun writeText(text: String)
}

/** mDNS/DNS-SD integration (docs/protocol.md §8.7). Optional. */
interface PeerResolver {
    fun register(deviceId: String, port: Int)
    fun unregister()

    /** Current IP of [deviceId], or null if not found in time. */
    suspend fun resolve(deviceId: String): String?
}

data class SendOutcome(val decision: OutgoingDecision, val results: List<PeerSendResult>) {
    val anyOk get() = results.any { it.ok }
}

/** Read-only view of a peer for the UI. */
data class PeerView(
    val deviceId: String,
    val name: String,
    val platform: String,
    val host: String,
    val port: Int,
    val enabled: Boolean,
    val lastSeenMs: Long?,
    val lastStatus: String?,
)

/** Server reply produced by a node handler. */
internal data class Reply(val status: Int, val body: Any? = null) {
    companion object {
        fun error(status: Int, code: String) = Reply(status, com.n3k0chan.klip.core.ErrorBody(code))
    }
}

/** Something received from another device. */
data class ReceivedItem(val from: String, val content: String, val isTest: Boolean, val atMs: Long) {
    /** Single-line preview for the UI. */
    fun preview(max: Int = 120): String {
        val oneLine = content.replace("\r\n", "\n").replace('\r', '\n').replace("\n", " ⏎ ")
        return if (oneLine.length <= max) oneLine else oneLine.take(max) + "…"
    }
}

sealed interface NodeEvent {
    data class TestReceived(val from: String, val content: String) : NodeEvent
    data class ContentReceived(val from: String, val content: String) : NodeEvent
    data class PairingRequested(val pairing: IncomingPairing) : NodeEvent
    data class IncomingPairingFinished(val sessionId: String, val status: PairingStatus, val deviceName: String) : NodeEvent
    data class Log(val message: String) : NodeEvent
}

/**
 * One Klip node: config, pairs, anti-loop state, HTTP server and client, pairing, group and radar.
 * Mirrors core-dotnet/Klip.Node/SyncNode. Platform-independent; the platform supplies clipboard
 * access, secret protection and (optionally) mDNS.
 */
class SyncNode(
    private val store: ConfigStore,
    internal val protector: SecretProtector,
    private val clipboard: ClipboardWriter,
    defaults: () -> NodeConfig = { NodeConfig() },
    internal val clock: () -> Long = System::currentTimeMillis,
) : AutoCloseable {
    internal val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    internal val client = PeerClient()
    internal val lock = Any()

    @Volatile
    internal var config: NodeConfig = store.loadOrCreate(defaults)
    private val state = SyncState(config.deviceId, config.sync ?: com.n3k0chan.klip.core.SyncSnapshot())
    internal val keys = ConcurrentHashMap<String, PairKeys>()
    private val lastStatus = ConcurrentHashMap<String, String>()
    private val server = KlipServer(this)
    private val serverMutex = Mutex()
    private var serverUsers = 0

    internal val verifier = RequestVerifier({ keys[it]?.macKey }, clock)
    internal val pairing = PairingManager(this)
    internal val group = GroupManager(this)
    internal val radar = Radar(this)

    /** Optional mDNS resolver. */
    var resolver: PeerResolver? = null

    private val _events = MutableSharedFlow<NodeEvent>(extraBufferCapacity = 64)
    val events: SharedFlow<NodeEvent> = _events.asSharedFlow()

    private val _peers = MutableStateFlow<List<PeerView>>(emptyList())
    val peers: StateFlow<List<PeerView>> = _peers.asStateFlow()

    private val _serverRunning = MutableStateFlow(false)
    val serverRunning: StateFlow<Boolean> = _serverRunning.asStateFlow()

    /** Last thing received (test or real), shown in the UI so the user can see what arrived. */
    private val _lastReceived = MutableStateFlow<ReceivedItem?>(null)
    val lastReceived: StateFlow<ReceivedItem?> = _lastReceived.asStateFlow()

    val discovered: StateFlow<List<DiscoveredDevice>> get() = radar.discovered

    init {
        for (peer in config.peers) {
            val secret = peer.protectedSecret ?: continue
            try {
                keys[peer.deviceId] = PairKeys.derive(protector.unprotect(secret.base64ToBytes()))
            } catch (e: Exception) {
                log("No se pudo descifrar el secreto de ${peer.name}: ${e.message}")
            }
        }
        publishPeers()
    }

    val deviceId: String get() = config.deviceId
    val platform: String get() = config.platform
    val httpPort: Int get() = config.httpPort
    val isServerRunning get() = _serverRunning.value
    internal val now get() = clock()

    var deviceName: String
        get() = config.deviceName
        set(value) = update { it.copy(deviceName = value) }

    val syncEnabled: Boolean get() = config.syncEnabled

    /** True while the configuration UI is visible: only then are pairing requests accepted. */
    var pairingOpen: Boolean
        get() = pairing.open
        set(value) { pairing.open = value }

    // ---------- Server lifecycle ----------

    /**
     * Reference-counted server start. Android keeps the server alive while the receiver service runs
     * OR the main screen is visible (pairing needs it). Also retries pending introductions.
     */
    suspend fun acquireServer() {
        serverMutex.withLock {
            serverUsers++
            if (serverUsers == 1) {
                try {
                    server.start(httpPort)
                } catch (e: Exception) {
                    serverUsers--
                    throw e
                }
                _serverRunning.value = true
                runCatching { resolver?.register(deviceId, httpPort) }.onFailure { log("mDNS: ${it.message}") }
                log("Servidor escuchando en el puerto $httpPort")
            }
        }
        scope.launch { group.deliverAllPending() }
    }

    suspend fun releaseServer() {
        serverMutex.withLock {
            if (serverUsers == 0) return
            serverUsers--
            if (serverUsers == 0) {
                runCatching { resolver?.unregister() }
                server.stop()
                _serverRunning.value = false
                log("Servidor detenido")
            }
        }
    }

    /** Single on/off switch (Windows-style). Disabling also blocks sending. */
    suspend fun setSyncEnabled(enabled: Boolean) {
        update { it.copy(syncEnabled = enabled) }
        if (enabled) acquireServer() else releaseServer()
    }

    // ---------- Peers ----------

    /** Adds or replaces a pair with a known secret (manual pairing in development mode). */
    fun addOrUpdatePeer(deviceId: String, name: String, host: String, port: Int, pairSecret: ByteArray, platform: String = "unknown") {
        require(deviceId != this.deviceId) { "No puedes vincular este mismo dispositivo" }
        storePeer(MemberInfo(deviceId, name, platform, port, host), pairSecret, SecretVersion(now, "manual"))
    }

    /**
     * Creates or replaces a pair, keeping the enabled flag of an existing one. With [onlyIfAbsent] (group
     * introductions, docs/protocol.md §8.3) an existing pair is never replaced; the check and the store are atomic.
     */
    internal fun storePeer(info: MemberInfo, secret: ByteArray, version: SecretVersion, onlyIfAbsent: Boolean = false): Boolean {
        val derived = PairKeys.derive(secret)
        var stored = false
        update { cfg ->
            val existing = cfg.peers.firstOrNull { it.deviceId == info.deviceId }
            if (onlyIfAbsent && existing != null) return@update cfg
            stored = true
            val peer = PeerConfig(
                deviceId = info.deviceId,
                name = info.deviceName,
                platform = info.platform,
                host = info.host,
                port = info.httpPort,
                enabled = existing?.enabled ?: true,
                protectedSecret = protector.protect(secret).toBase64(),
                lastSeenMs = existing?.lastSeenMs,
                secretIssuedAt = version.issuedAt,
                secretIssuer = version.issuerId,
            )
            keys[info.deviceId] = derived
            cfg.copy(peers = cfg.peers.filter { it.deviceId != info.deviceId } + peer)
        }
        return stored
    }

    /** Removes a pair locally without notifying the other side. */
    fun removePeer(deviceId: String) {
        update { cfg ->
            keys.remove(deviceId)
            lastStatus.remove(deviceId)
            cfg.copy(
                peers = cfg.peers.filter { it.deviceId != deviceId },
                pendingIntroductions = cfg.pendingIntroductions.filter { it.receiverId != deviceId && it.member.deviceId != deviceId },
            )
        }
    }

    fun setPeerEnabled(deviceId: String, enabled: Boolean) = update { cfg ->
        cfg.copy(peers = cfg.peers.map { if (it.deviceId == deviceId) it.copy(enabled = enabled) else it })
    }

    internal fun peerWithKeys(deviceId: String): Pair<PeerConfig, PairKeys>? {
        val peer = config.peers.firstOrNull { it.deviceId == deviceId } ?: return null
        val k = keys[deviceId] ?: return null
        return peer to k
    }

    internal fun peersWithKeys(filter: (PeerConfig) -> Boolean): List<Pair<PeerConfig, PairKeys>> =
        config.peers.filter(filter).mapNotNull { p -> keys[p.deviceId]?.let { p to it } }

    internal fun updateHost(deviceId: String, host: String, port: Int? = null) = update { cfg ->
        cfg.copy(peers = cfg.peers.map { p ->
            if (p.deviceId == deviceId && (p.host != host || (port != null && p.port != port))) {
                log("Dirección de ${p.name} actualizada: ${p.host}:${p.port} -> $host:${port ?: p.port}")
                p.copy(host = host, port = port ?: p.port)
            } else p
        })
    }

    /** Atomically updates and persists the config, then publishes the peer list. */
    internal fun update(transform: (NodeConfig) -> NodeConfig) {
        synchronized(lock) {
            config = transform(config).copy(sync = state.snapshot())
            store.save(config)
        }
        publishPeers()
    }

    internal fun publishPeers() {
        _peers.value = config.peers.map {
            PeerView(it.deviceId, it.name, it.platform, it.host, it.port, it.enabled, it.lastSeenMs, lastStatus[it.deviceId])
        }
    }

    // ---------- Outgoing clipboard ----------

    /** Applies the outgoing rules and sends to every enabled peer (or only [onlyDeviceId]). */
    suspend fun sendClipboard(content: String, isSensitive: Boolean = false, isTest: Boolean = false, onlyDeviceId: String? = null): SendOutcome {
        val decision = state.evaluateOutgoing(content, isTest, isSensitive, syncEnabled)
        if (decision != OutgoingDecision.SEND) return SendOutcome(decision, emptyList())

        val targets = peersWithKeys { if (onlyDeviceId == null) it.enabled else it.deviceId == onlyDeviceId }
        if (targets.isEmpty()) return SendOutcome(decision, emptyList())

        val payload = ClipboardPayload.text(content, deviceName, isTest)
        val messageId = UUID.randomUUID().toString()
        val results = targets.map { (peer, k) -> scope.async { sendWithResolve(peer, k, payload, messageId) } }.awaitAll()

        for (r in results) lastStatus[r.deviceId] = if (r.ok) "Enviado (${r.httpStatus} OK)" else r.error ?: "Error"
        if (!isTest && results.any { it.ok }) {
            state.markSent(payload.contentHash)
            update { it }
        } else {
            publishPeers()
        }
        log("Envío ${if (isTest) "de prueba " else ""}a ${results.count { it.ok }}/${results.size} dispositivos")
        return SendOutcome(decision, results)
    }

    /** Sends; if the peer is unreachable and mDNS finds a new IP, retries once there (§8.7). */
    private suspend fun sendWithResolve(peer: PeerConfig, k: PairKeys, payload: ClipboardPayload, messageId: String): PeerSendResult {
        val result = client.sendClipboard(peer, k, deviceId, EnvelopeCodec.seal(k.encKey, deviceId, payload, messageId))
        val r = resolver
        if (result.ok || !result.unreachable || r == null) return result
        val ip = withTimeoutOrNull(1_500) { runCatching { r.resolve(peer.deviceId) }.getOrNull() }
        if (ip == null || ip == peer.host) return result
        updateHost(peer.deviceId, ip)
        // Same messageId: if the first attempt actually arrived, the receiver ignores the duplicate.
        return client.sendClipboard(peer.copy(host = ip), k, deviceId, EnvelopeCodec.seal(k.encKey, deviceId, payload, messageId))
    }

    suspend fun sendTest(onlyDeviceId: String? = null): SendOutcome {
        val time = SimpleDateFormat("HH:mm:ss", Locale.ROOT).format(Date(now))
        return sendClipboard("Prueba de conexión desde $deviceName - $time", isTest = true, onlyDeviceId = onlyDeviceId)
    }

    suspend fun ping(deviceId: String): PingResult {
        val (peer, k) = peerWithKeys(deviceId) ?: return PingResult(false, 0, null, null, "Dispositivo desconocido")
        val result = client.ping(peer.host, peer.port, k, this.deviceId)
        lastStatus[deviceId] = if (result.ok) "Conectado (${result.elapsedMs} ms)" else result.error ?: "Error"
        if (result.ok) markSeen(deviceId, null) else publishPeers()
        return result
    }

    // ---------- Incoming clipboard (called by KlipServer after signature verification) ----------

    internal fun receiveClipboard(deviceId: String, body: ByteArray, remoteIp: String?): Reply {
        val k = keys[deviceId] ?: return Reply(403)
        val envelope: Envelope
        val payload: ClipboardPayload
        try {
            envelope = EnvelopeCodec.decode(body.toString(Charsets.UTF_8))
            if (envelope.originDeviceId != deviceId) return Reply(400)
            payload = EnvelopeCodec.open(k.encKey, envelope)
        } catch (e: EnvelopeException) {
            log("Mensaje rechazado de $deviceId: ${e.reason}")
            return Reply(400)
        }

        if (state.acceptIncoming(envelope.originDeviceId, envelope.messageId, payload.contentHash) != IncomingDecision.ACCEPT) return Reply(409)

        markSeen(deviceId, remoteIp)
        try {
            clipboard.writeText(payload.content)
        } catch (e: Exception) {
            log("No se pudo escribir en el portapapeles: ${e.message}")
        }
        update { it }

        log("Recibido de ${payload.originDeviceName}${if (payload.isTest) " (prueba)" else ""}: ${payload.content.length} caracteres")
        _lastReceived.value = ReceivedItem(payload.originDeviceName, payload.content, payload.isTest, now)
        emit(
            if (payload.isTest) NodeEvent.TestReceived(payload.originDeviceName, payload.content)
            else NodeEvent.ContentReceived(payload.originDeviceName, payload.content)
        )
        return Reply(200)
    }

    internal val pingInfo get() = PingInfo(deviceId, deviceName, platform)

    /**
     * Records verified contact with a peer and, if given, its current IP (passive IP update, plan §5.3).
     * Also retries pending group introductions for it.
     */
    internal fun markSeen(deviceId: String, remoteIp: String?) {
        if (config.peers.none { it.deviceId == deviceId }) return
        update { cfg ->
            cfg.copy(peers = cfg.peers.map { p ->
                if (p.deviceId != deviceId) p
                else {
                    if (!remoteIp.isNullOrEmpty() && p.host != remoteIp) log("IP de ${p.name} actualizada: ${p.host} -> $remoteIp")
                    p.copy(lastSeenMs = now, host = if (remoteIp.isNullOrEmpty()) p.host else remoteIp)
                }
            })
        }
        if (config.pendingIntroductions.any { it.receiverId == deviceId }) scope.launch { group.deliverPending(deviceId) }
    }

    internal fun emit(event: NodeEvent) {
        _events.tryEmit(event)
    }

    internal fun log(message: String) = emit(NodeEvent.Log(message))

    // ---------- Facade over the managers ----------

    fun startDiscovery(options: DiscoveryOptions = DiscoveryOptions()) = radar.start(options)
    fun stopDiscovery() = radar.stop()
    suspend fun startPairing(host: String, port: Int) = pairing.start(host, port)
    suspend fun confirmPairing(p: OutgoingPairing) = pairing.confirm(p)
    suspend fun cancelPairing(p: OutgoingPairing) = pairing.cancel(p)
    fun respondToPairing(sessionId: String, accept: Boolean) = pairing.respond(sessionId, accept)
    suspend fun unpair(deviceId: String) = group.unpair(deviceId)
    suspend fun expel(deviceId: String) = group.expel(deviceId)

    override fun close() {
        radar.stop()
        runCatching { server.stopBlocking() }
        _serverRunning.value = false
        client.close()
        scope.cancel()
    }
}
