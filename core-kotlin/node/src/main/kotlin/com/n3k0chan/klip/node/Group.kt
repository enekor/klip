package com.n3k0chan.klip.node

import com.n3k0chan.klip.core.CryptoException
import com.n3k0chan.klip.core.IntroduceRequest
import com.n3k0chan.klip.core.IntroduceResponse
import com.n3k0chan.klip.core.Introductions
import com.n3k0chan.klip.core.MemberInfo
import com.n3k0chan.klip.core.PairKeys
import com.n3k0chan.klip.core.Protocol
import com.n3k0chan.klip.core.ProtocolJson
import com.n3k0chan.klip.core.RemoveRequest
import com.n3k0chan.klip.core.SecretVersion
import com.n3k0chan.klip.core.base64ToBytes
import com.n3k0chan.klip.core.toBase64
import io.ktor.http.HttpMethod
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitAll
import kotlinx.serialization.SerializationException
import java.util.concurrent.ConcurrentHashMap

/** Group mesh: introductions, unpair and expel. See docs/protocol.md §8.3–8.4. */
internal class GroupManager(private val node: SyncNode) {
    private val deliveringTo = ConcurrentHashMap.newKeySet<String>()

    /** After pairing with [newId], introduce it to every other peer and vice versa. */
    suspend fun introduceNewPeer(newId: String) {
        val (newPeer, _) = node.peerWithKeys(newId) ?: return
        for ((member, _) in node.peersWithKeys { it.deviceId != newId }) {
            val secret = PairKeys.newRandomSecret()
            val issuedAt = node.now
            deliver(newId, member.toMemberInfo(), issuedAt, secret)
            deliver(member.deviceId, newPeer.toMemberInfo(), issuedAt, secret)
        }
    }

    /** Sends one introduction; queues it if the receiver is unreachable. */
    private suspend fun deliver(receiverId: String, member: MemberInfo, issuedAt: Long, secret: ByteArray, queueOnFailure: Boolean = true): Boolean {
        val (receiver, k) = node.peerWithKeys(receiverId) ?: return false
        val request = Introductions.seal(k.encKey, node.deviceId, member, issuedAt, secret)
        val body = ProtocolJson.encodeToString(IntroduceRequest.serializer(), request).toByteArray()
        val r = node.client.send(HttpMethod.Post, receiver.host, receiver.port, "/group/introduce", body, k, node.deviceId)
        if (r.isSuccess) {
            node.log("Presentado ${member.deviceName} a ${receiver.name}")
            return true
        }
        val status = r.status
        if (queueOnFailure && (status == null || status >= 500)) {
            node.update { cfg ->
                cfg.copy(pendingIntroductions = cfg.pendingIntroductions +
                    PendingIntroduction(receiverId, member, issuedAt, node.protector.protect(secret).toBase64(), node.now))
            }
            node.log("Presentación de ${member.deviceName} a ${receiver.name} pendiente (${r.error ?: "HTTP $status"})")
        } else {
            node.log("Presentación de ${member.deviceName} a ${receiver.name} rechazada (HTTP $status)")
        }
        return false
    }

    suspend fun deliverAllPending() {
        for (id in node.config.pendingIntroductions.map { it.receiverId }.distinct()) deliverPending(id)
    }

    suspend fun deliverPending(receiverId: String) {
        if (!deliveringTo.add(receiverId)) return
        try {
            node.update { cfg -> cfg.copy(pendingIntroductions = cfg.pendingIntroductions.filter { node.now - it.createdAt <= Protocol.INTRODUCTION_EXPIRY_MS }) }
            for (p in node.config.pendingIntroductions.filter { it.receiverId == receiverId }) {
                val secret = runCatching { node.protector.unprotect(p.protectedSecret.base64ToBytes()) }.getOrNull()
                val delivered = secret != null && secret.size == 32 && deliver(receiverId, p.member, p.issuedAt, secret, queueOnFailure = false)
                if (delivered || secret == null || secret.size != 32) {
                    node.update { cfg -> cfg.copy(pendingIntroductions = cfg.pendingIntroductions - p) }
                } else {
                    break // still unreachable: try again on next contact
                }
            }
        } finally {
            deliveringTo.remove(receiverId)
        }
    }

    fun handleIntroduce(introducerId: String, body: ByteArray): Reply {
        val k = node.keys[introducerId] ?: return Reply(403)
        val req: IntroduceRequest
        val secret: ByteArray
        try {
            req = ProtocolJson.decodeFromString(IntroduceRequest.serializer(), body.toString(Charsets.UTF_8))
            val memberId = req.member.deviceId
            if (memberId == node.deviceId || memberId.isBlank() || memberId == introducerId) return Reply.error(400, "invalid_request")
            secret = Introductions.open(k.encKey, introducerId, req)
        } catch (e: SerializationException) {
            return Reply.error(400, "invalid_request")
        } catch (e: IllegalArgumentException) {
            return Reply.error(400, "invalid_request")
        } catch (e: CryptoException) {
            return Reply.error(400, "invalid_request")
        }

        val version = SecretVersion(req.issuedAt, introducerId)
        if (!node.storePeer(req.member, secret, version, onlyIfAbsent = true)) return Reply(200, IntroduceResponse(false))
        node.log("Nuevo miembro del grupo: ${req.member.deviceName} (presentado por $introducerId)")
        return Reply(200, IntroduceResponse(true))
    }

    /** Breaks the pair with [deviceId] on both sides (best effort remotely). */
    suspend fun unpair(deviceId: String) {
        node.peerWithKeys(deviceId)?.let { (peer, k) ->
            node.client.send(HttpMethod.Post, peer.host, peer.port, "/unpair", "{}".toByteArray(), k, node.deviceId, 3_000)
        }
        node.removePeer(deviceId)
    }

    /** Removes [deviceId] from the whole group (best effort remotely). */
    suspend fun expel(deviceId: String) {
        val body = ProtocolJson.encodeToString(RemoveRequest.serializer(), RemoveRequest(deviceId)).toByteArray()
        node.peersWithKeys { true }.map { (peer, k) ->
            node.scope.async { node.client.send(HttpMethod.Post, peer.host, peer.port, "/group/remove", body, k, node.deviceId, 3_000) }
        }.awaitAll()
        node.removePeer(deviceId)
    }

    fun handleUnpair(senderId: String): Reply {
        node.removePeer(senderId)
        node.log("$senderId ha deshecho la vinculación")
        return Reply(200)
    }

    fun handleRemove(senderId: String, body: ByteArray): Reply {
        val req = try {
            ProtocolJson.decodeFromString(RemoveRequest.serializer(), body.toString(Charsets.UTF_8))
        } catch (e: SerializationException) {
            return Reply.error(400, "invalid_request")
        } catch (e: IllegalArgumentException) {
            return Reply.error(400, "invalid_request")
        }
        if (req.deviceId.isEmpty()) return Reply.error(400, "invalid_request")
        if (req.deviceId == node.deviceId) {
            node.log("Expulsado del grupo por $senderId: se eliminan todas las vinculaciones")
            node.config.peers.forEach { node.removePeer(it.deviceId) }
        } else {
            node.removePeer(req.deviceId)
        }
        return Reply(200)
    }
}
