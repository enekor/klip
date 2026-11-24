package com.n3k0chan.klip.node

import com.n3k0chan.klip.core.Envelope
import com.n3k0chan.klip.core.EnvelopeCodec
import com.n3k0chan.klip.core.PairKeys
import com.n3k0chan.klip.core.PingInfo
import com.n3k0chan.klip.core.ProtocolJson
import com.n3k0chan.klip.core.RequestSigner
import io.ktor.client.HttpClient
import io.ktor.client.engine.cio.CIO
import io.ktor.client.plugins.HttpTimeout
import io.ktor.client.plugins.timeout
import io.ktor.client.request.header
import io.ktor.client.request.request
import io.ktor.client.request.setBody
import io.ktor.client.statement.readRawBytes
import io.ktor.http.ContentType
import io.ktor.http.HttpMethod
import io.ktor.http.URLProtocol
import io.ktor.http.contentType
import io.ktor.http.encodedPath
import kotlinx.coroutines.CancellationException
import kotlinx.serialization.SerializationException

data class PeerSendResult(val deviceId: String, val name: String, val ok: Boolean, val httpStatus: Int?, val error: String?) {
    /** True when the peer could not be reached at all (no HTTP response). */
    val unreachable get() = httpStatus == null
}

data class PingResult(val ok: Boolean, val elapsedMs: Long, val info: PingInfo?, val httpStatus: Int?, val error: String?)

/** Raw HTTP outcome: status + body, or a transport error. */
class HttpOutcome(val status: Int?, val body: ByteArray, val error: String?) {
    val isSuccess get() = status != null && status in 200..299
}

/** Outgoing HTTP calls to other nodes. See docs/protocol.md §2.6 for timeouts. */
class PeerClient : AutoCloseable {
    private val http = HttpClient(CIO) {
        expectSuccess = false
        install(HttpTimeout) {
            connectTimeoutMillis = CONNECT_TIMEOUT_MS
        }
    }

    suspend fun sendClipboard(peer: PeerConfig, keys: PairKeys, localDeviceId: String, envelope: Envelope): PeerSendResult {
        val body = EnvelopeCodec.encode(envelope).toByteArray(Charsets.UTF_8)
        val r = send(HttpMethod.Post, peer.host, peer.port, "/clipboard", body, keys, localDeviceId)
        return PeerSendResult(peer.deviceId, peer.name, r.isSuccess, r.status, r.error ?: if (r.isSuccess) null else "HTTP ${r.status}")
    }

    suspend fun ping(host: String, port: Int, keys: PairKeys, localDeviceId: String): PingResult {
        val start = System.nanoTime()
        val r = send(HttpMethod.Get, host, port, "/ping", ByteArray(0), keys, localDeviceId)
        val elapsed = (System.nanoTime() - start) / 1_000_000
        if (!r.isSuccess) return PingResult(false, elapsed, null, r.status, r.error ?: "HTTP ${r.status}")
        return try {
            PingResult(true, elapsed, ProtocolJson.decodeFromString(PingInfo.serializer(), r.body.toString(Charsets.UTF_8)), r.status, null)
        } catch (e: SerializationException) {
            PingResult(false, elapsed, null, r.status, "Respuesta inválida")
        }
    }

    /**
     * Sends a request. Signed when [keys] is given (all routes except /pair/request).
     * Never throws for network errors: they are reported in [HttpOutcome.error].
     */
    suspend fun send(
        method: HttpMethod,
        host: String,
        port: Int,
        path: String,
        body: ByteArray,
        keys: PairKeys?,
        localDeviceId: String,
        timeoutMs: Long = totalTimeoutMs(body.size.toLong()),
    ): HttpOutcome = try {
        val response = http.request {
            this.method = method
            url {
                protocol = URLProtocol.HTTP
                this.host = host
                this.port = port
                encodedPath = path
            }
            timeout { requestTimeoutMillis = timeoutMs }
            if (keys != null) {
                RequestSigner.sign(keys.macKey, localDeviceId, method.value, path, body).toMap().forEach { (k, v) -> header(k, v) }
            }
            if (method != HttpMethod.Get) {
                contentType(ContentType.Application.Json)
                setBody(body)
            }
        }
        HttpOutcome(response.status.value, response.readRawBytes(), null)
    } catch (e: CancellationException) {
        if (e.javaClass.name.contains("Timeout")) HttpOutcome(null, ByteArray(0), "Timeout") else throw e
    } catch (e: Exception) {
        HttpOutcome(null, ByteArray(0), describe(e))
    }

    private fun describe(e: Exception): String = when {
        e.javaClass.simpleName.contains("Timeout") -> "Timeout"
        e is java.net.ConnectException -> "Error de conexión (${e.message ?: "rechazada"})"
        e is java.net.UnknownHostException -> "Error de conexión (host desconocido)"
        e is java.io.IOException -> "Error de conexión (${e.message ?: e.javaClass.simpleName})"
        else -> e.message ?: e.javaClass.simpleName
    }

    override fun close() = http.close()

    companion object {
        const val CONNECT_TIMEOUT_MS = 2_000L

        /** 10 s base + 1 s per MB of body. */
        fun totalTimeoutMs(bodyBytes: Long) = 10_000L + bodyBytes / (1024 * 1024) * 1000
    }
}
