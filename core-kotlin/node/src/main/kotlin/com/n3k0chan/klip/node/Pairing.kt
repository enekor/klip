package com.n3k0chan.klip.node

import com.n3k0chan.klip.core.CryptoException
import com.n3k0chan.klip.core.ErrorBody
import com.n3k0chan.klip.core.MemberInfo
import com.n3k0chan.klip.core.PairConfirmResponse
import com.n3k0chan.klip.core.PairKeys
import com.n3k0chan.klip.core.PairRequest
import com.n3k0chan.klip.core.PairResponse
import com.n3k0chan.klip.core.Pairing
import com.n3k0chan.klip.core.Protocol
import com.n3k0chan.klip.core.ProtocolJson
import com.n3k0chan.klip.core.RequestVerifier
import com.n3k0chan.klip.core.SecretVersion
import com.n3k0chan.klip.core.base64ToBytes
import com.n3k0chan.klip.core.toBase64
import io.ktor.http.HttpMethod
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withTimeoutOrNull
import kotlinx.serialization.SerializationException
import java.util.UUID

/** Incoming pairing request shown to the user (docs/protocol.md §8.1). */
data class IncomingPairing(val sessionId: String, val deviceId: String, val deviceName: String, val platform: String, val pin: String, val expiresAt: Long)

/** Outgoing pairing in progress: show [pin] and ask the user to confirm. */
class OutgoingPairing internal constructor(
    val host: String,
    val deviceId: String,
    val deviceName: String,
    val platform: String,
    val httpPort: Int,
    val pin: String,
    internal val keys: PairKeys,
    internal val secret: ByteArray,
    internal val issuedAt: Long,
)

enum class PairingStatus { ACCEPTED, REJECTED, CANCELLED, TIMED_OUT, FAILED }

data class PairingOutcome(val status: PairingStatus, val message: String? = null) {
    val ok get() = status == PairingStatus.ACCEPTED
}

/** Both sides of PIN pairing. See docs/protocol.md §8.1–8.2. */
internal class PairingManager(private val node: SyncNode) {
    private class Session(
        val id: String,
        val request: PairRequest,
        val keys: PairKeys,
        val secret: ByteArray,
        val pin: String,
        val issuedAt: Long,
        val expiresAt: Long,
    ) {
        val decision = CompletableDeferred<Boolean>()
    }

    private val lock = Any()
    private val requestsByIp = HashMap<String, ArrayDeque<Long>>()
    private var session: Session? = null

    @Volatile
    var open = false

    /** Verifies /pair/confirm and /pair/cancel, signed with the pending session keys. */
    val sessionVerifier = RequestVerifier({ id ->
        synchronized(lock) { session?.takeIf { it.request.deviceId == id && node.now <= it.expiresAt }?.keys?.macKey }
    }, node.clock)

    // ---------- Responder side ----------

    fun handleRequest(body: ByteArray, remoteIp: String?): Reply {
        if (!open) return Reply.error(403, "pairing_closed")
        if (!allow(remoteIp ?: "?")) return Reply.error(429, "rate_limited")

        val req: PairRequest
        val peerPub: ByteArray
        val peerNonce: ByteArray
        try {
            req = ProtocolJson.decodeFromString(PairRequest.serializer(), body.toString(Charsets.UTF_8))
            peerPub = req.publicKey.base64ToBytes()
            peerNonce = req.nonce.base64ToBytes()
        } catch (e: SerializationException) {
            return Reply.error(400, "invalid_request")
        } catch (e: IllegalArgumentException) {
            return Reply.error(400, "invalid_request")
        }
        if (req.protocolVersion != Protocol.VERSION || req.deviceId.isBlank() || req.deviceId == node.deviceId ||
            peerNonce.size != Protocol.PAIRING_NONCE_BYTES || !Pairing.isValidPublicKey(peerPub) || req.httpPort !in 1..65535
        ) {
            return Reply.error(400, "invalid_request")
        }

        val s: Session
        val response: PairResponse
        synchronized(lock) {
            val current = session
            if (current != null && node.now <= current.expiresAt && current.request.deviceId != req.deviceId) return Reply.error(409, "busy")
            current?.decision?.complete(false) // replaced by a new request from the same device

            val kp = Pairing.generateKeyPair()
            val nonce = Pairing.newNonce()
            val secret = Pairing.derivePairSecret(Pairing.sharedSecret(kp, peerPub), peerNonce, nonce)
            s = Session(
                id = UUID.randomUUID().toString(),
                request = req,
                keys = PairKeys.derive(secret),
                secret = secret,
                pin = Pairing.pin(peerPub, kp.publicKey, peerNonce, nonce),
                issuedAt = node.now,
                expiresAt = node.now + Protocol.PAIRING_TIMEOUT_MS,
            )
            session = s
            response = PairResponse(
                Protocol.VERSION, node.deviceId, node.deviceName, node.platform, node.httpPort,
                kp.publicKey.toBase64(), nonce.toBase64(), s.issuedAt,
            )
        }
        node.scope.launch { expireLater(s) }
        node.log("Solicitud de vinculación de ${req.deviceName} ($remoteIp)")
        node.emit(NodeEvent.PairingRequested(IncomingPairing(s.id, req.deviceId, req.deviceName, req.platform, s.pin, s.expiresAt)))
        return Reply(200, response)
    }

    /** The local user accepted or rejected the incoming request. */
    fun respond(sessionId: String, accept: Boolean) {
        val s = synchronized(lock) {
            val current = session?.takeIf { it.id == sessionId } ?: return
            if (!accept) session = null
            current
        }
        s.decision.complete(accept)
        if (!accept) node.emit(NodeEvent.IncomingPairingFinished(sessionId, PairingStatus.REJECTED, s.request.deviceName))
    }

    suspend fun handleConfirm(deviceId: String, remoteIp: String?): Reply {
        val s = synchronized(lock) { session?.takeIf { it.request.deviceId == deviceId } } ?: return Reply.error(403, "rejected")
        val remaining = (s.expiresAt - node.now).coerceAtLeast(0)
        val accepted = withTimeoutOrNull(remaining) { s.decision.await() }
        synchronized(lock) { if (session === s) session = null }
        val name = s.request.deviceName
        if (accepted == null) {
            node.emit(NodeEvent.IncomingPairingFinished(s.id, PairingStatus.TIMED_OUT, name))
            return Reply.error(408, "timeout")
        }
        if (!accepted) return Reply.error(403, "rejected")

        val r = s.request
        node.storePeer(MemberInfo(r.deviceId, r.deviceName, r.platform, r.httpPort, remoteIp ?: ""), s.secret, SecretVersion(s.issuedAt, SecretVersion.PIN_ISSUER))
        node.log("Vinculado con $name")
        node.emit(NodeEvent.IncomingPairingFinished(s.id, PairingStatus.ACCEPTED, name))
        node.scope.launch { node.group.introduceNewPeer(r.deviceId) }
        return Reply(200, PairConfirmResponse(true))
    }

    fun handleCancel(deviceId: String): Reply {
        val s = synchronized(lock) {
            session?.takeIf { it.request.deviceId == deviceId }?.also { session = null }
        } ?: return Reply(200)
        s.decision.complete(false)
        node.emit(NodeEvent.IncomingPairingFinished(s.id, PairingStatus.CANCELLED, s.request.deviceName))
        return Reply(200)
    }

    private suspend fun expireLater(s: Session) {
        delay(Protocol.PAIRING_TIMEOUT_MS + 500)
        val expired = synchronized(lock) { (session === s).also { if (it) session = null } }
        if (expired) {
            s.decision.complete(false)
            node.emit(NodeEvent.IncomingPairingFinished(s.id, PairingStatus.TIMED_OUT, s.request.deviceName))
        }
    }

    private fun allow(ip: String): Boolean = synchronized(lock) {
        val q = requestsByIp.getOrPut(ip) { ArrayDeque() }
        while (q.isNotEmpty() && node.now - q.first() > 60_000) q.removeFirst()
        if (q.size >= Protocol.PAIR_REQUESTS_PER_MINUTE) return false
        q.addLast(node.now)
        true
    }

    // ---------- Initiator side ----------

    /** Step 1: contacts the other device. On success, show [OutgoingPairing.pin]. Returns the pairing or an error message. */
    suspend fun start(host: String, port: Int): Result<OutgoingPairing> {
        val kp = Pairing.generateKeyPair()
        val nonce = Pairing.newNonce()
        val request = PairRequest(Protocol.VERSION, node.deviceId, node.deviceName, node.platform, node.httpPort, kp.publicKey.toBase64(), nonce.toBase64())
        val body = ProtocolJson.encodeToString(PairRequest.serializer(), request).toByteArray()
        val r = node.client.send(HttpMethod.Post, host, port, "/pair/request", body, null, node.deviceId)
        if (!r.isSuccess) return Result.failure(PairingException(describe(r)))
        return try {
            val resp = ProtocolJson.decodeFromString(PairResponse.serializer(), r.body.toString(Charsets.UTF_8))
            val peerPub = resp.publicKey.base64ToBytes()
            val peerNonce = resp.nonce.base64ToBytes()
            if (resp.deviceId == node.deviceId || peerNonce.size != Protocol.PAIRING_NONCE_BYTES || !Pairing.isValidPublicKey(peerPub)) {
                return Result.failure(PairingException("Respuesta de vinculación inválida"))
            }
            val secret = Pairing.derivePairSecret(Pairing.sharedSecret(kp, peerPub), nonce, peerNonce)
            Result.success(
                OutgoingPairing(
                    host, resp.deviceId, resp.deviceName, resp.platform, resp.httpPort,
                    Pairing.pin(kp.publicKey, peerPub, nonce, peerNonce), PairKeys.derive(secret), secret, resp.issuedAt,
                )
            )
        } catch (e: SerializationException) {
            Result.failure(PairingException("Respuesta de vinculación inválida"))
        } catch (e: IllegalArgumentException) {
            Result.failure(PairingException("Respuesta de vinculación inválida"))
        } catch (e: CryptoException) {
            Result.failure(PairingException("Respuesta de vinculación inválida"))
        }
    }

    /** Step 2: the local user confirmed the PIN. Waits for the other user's decision. */
    suspend fun confirm(p: OutgoingPairing): PairingOutcome {
        val r = node.client.send(HttpMethod.Post, p.host, p.httpPort, "/pair/confirm", "{}".toByteArray(), p.keys, node.deviceId,
            Protocol.PAIRING_TIMEOUT_MS + 5_000)
        return when (r.status) {
            200 -> {
                node.storePeer(MemberInfo(p.deviceId, p.deviceName, p.platform, p.httpPort, p.host), p.secret, SecretVersion(p.issuedAt, SecretVersion.PIN_ISSUER))
                node.log("Vinculado con ${p.deviceName}")
                node.scope.launch { node.group.introduceNewPeer(p.deviceId) }
                PairingOutcome(PairingStatus.ACCEPTED)
            }
            403 -> PairingOutcome(PairingStatus.REJECTED, "${p.deviceName} rechazó la vinculación")
            408 -> PairingOutcome(PairingStatus.TIMED_OUT, "Se agotó el tiempo de espera")
            else -> PairingOutcome(PairingStatus.FAILED, describe(r))
        }
    }

    /** The local user rejected the PIN or closed the dialog. */
    suspend fun cancel(p: OutgoingPairing) {
        node.client.send(HttpMethod.Post, p.host, p.httpPort, "/pair/cancel", "{}".toByteArray(), p.keys, node.deviceId, 3_000)
    }

    private fun describe(r: HttpOutcome): String {
        if (r.status == null) return r.error ?: "Error de conexión"
        val code = runCatching { ProtocolJson.decodeFromString(ErrorBody.serializer(), r.body.toString(Charsets.UTF_8)).error }.getOrNull()
        return when (code) {
            "pairing_closed" -> "El otro dispositivo debe tener abierta la ventana de Klip"
            "busy" -> "El otro dispositivo está atendiendo otra vinculación"
            "rate_limited" -> "Demasiados intentos; espera un minuto"
            "invalid_request" -> "El otro dispositivo rechazó la solicitud (¿versión distinta?)"
            else -> "HTTP ${r.status}"
        }
    }
}

class PairingException(message: String) : Exception(message)
