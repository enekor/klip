package com.n3k0chan.klip.node

import com.n3k0chan.klip.core.ErrorBody
import com.n3k0chan.klip.core.IntroduceResponse
import com.n3k0chan.klip.core.PairConfirmResponse
import com.n3k0chan.klip.core.PairResponse
import com.n3k0chan.klip.core.PingInfo
import com.n3k0chan.klip.core.Protocol
import com.n3k0chan.klip.core.ProtocolJson
import com.n3k0chan.klip.core.RequestVerifier
import com.n3k0chan.klip.core.VerifyResult
import io.ktor.http.ContentType
import io.ktor.http.HttpStatusCode
import io.ktor.server.application.ApplicationCall
import io.ktor.server.cio.CIO
import io.ktor.server.engine.EmbeddedServer
import io.ktor.server.engine.embeddedServer
import io.ktor.server.request.httpMethod
import io.ktor.server.request.receive
import io.ktor.server.response.respond
import io.ktor.server.response.respondBytes
import io.ktor.server.routing.get
import io.ktor.server.routing.post
import io.ktor.server.routing.routing
import kotlinx.serialization.KSerializer

/** Embedded HTTP server (Ktor CIO). Transport only; the logic lives in [SyncNode] and its managers. */
internal class KlipServer(private val node: SyncNode) {
    private var server: EmbeddedServer<*, *>? = null

    suspend fun start(port: Int) {
        if (server != null) return
        val s = embeddedServer(CIO, port = port, host = "0.0.0.0") {
            routing {
                post("/clipboard") { call.signed("/clipboard", node.verifier) { id, body, ip -> node.receiveClipboard(id, body, ip) } }
                get("/ping") {
                    call.signed("/ping", node.verifier) { id, _, ip ->
                        node.markSeen(id, ip)
                        Reply(200, node.pingInfo)
                    }
                }
                post("/pair/request") { call.reply(node.pairing.handleRequest(call.receive<ByteArray>(), call.remoteIp())) }
                post("/pair/confirm") { call.signed("/pair/confirm", node.pairing.sessionVerifier) { id, _, ip -> node.pairing.handleConfirm(id, ip) } }
                post("/pair/cancel") { call.signed("/pair/cancel", node.pairing.sessionVerifier) { id, _, _ -> node.pairing.handleCancel(id) } }
                post("/group/introduce") {
                    call.signed("/group/introduce", node.verifier) { id, body, ip ->
                        node.markSeen(id, ip)
                        node.group.handleIntroduce(id, body)
                    }
                }
                post("/unpair") { call.signed("/unpair", node.verifier) { id, _, _ -> node.group.handleUnpair(id) } }
                post("/group/remove") { call.signed("/group/remove", node.verifier) { id, body, _ -> node.group.handleRemove(id, body) } }
            }
        }
        s.startSuspend(wait = false)
        server = s
    }

    suspend fun stop() {
        val s = server ?: return
        server = null
        s.stopSuspend(gracePeriodMillis = 200, timeoutMillis = 1_000)
    }

    fun stopBlocking() {
        val s = server ?: return
        server = null
        s.stop(200, 1_000)
    }

    private suspend fun ApplicationCall.signed(path: String, verifier: RequestVerifier, handler: suspend (String, ByteArray, String?) -> Reply) {
        val body = receive<ByteArray>()
        val headers = request.headers.entries().associate { (k, v) -> k to v.firstOrNull() }
        val auth = verifier.verify(headers, request.httpMethod.value, path, body)
        if (auth != VerifyResult.OK) {
            node.log("${request.httpMethod.value} $path rechazado desde ${remoteIp()}: $auth")
            respond(HttpStatusCode.fromValue(auth.httpStatus))
            return
        }
        reply(handler(request.headers[Protocol.HEADER_DEVICE]!!, body, remoteIp()))
    }

    private suspend fun ApplicationCall.reply(reply: Reply) {
        val status = HttpStatusCode.fromValue(reply.status)
        val body = reply.body ?: return respond(status)
        respondBytes(encode(body).toByteArray(Charsets.UTF_8), ContentType.Application.Json, status)
    }

    private fun ApplicationCall.remoteIp(): String? = request.local.remoteAddress.removePrefix("::ffff:").ifEmpty { null }

    @Suppress("UNCHECKED_CAST")
    private fun encode(body: Any): String {
        val serializer: KSerializer<Any> = when (body) {
            is ErrorBody -> ErrorBody.serializer()
            is PingInfo -> PingInfo.serializer()
            is PairResponse -> PairResponse.serializer()
            is PairConfirmResponse -> PairConfirmResponse.serializer()
            is IntroduceResponse -> IntroduceResponse.serializer()
            else -> error("No serializer for ${body::class}")
        } as KSerializer<Any>
        return ProtocolJson.encodeToString(serializer, body)
    }
}
