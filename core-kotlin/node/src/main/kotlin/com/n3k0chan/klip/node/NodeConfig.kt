package com.n3k0chan.klip.node

import com.n3k0chan.klip.core.MemberInfo
import com.n3k0chan.klip.core.Protocol
import com.n3k0chan.klip.core.SecretVersion
import com.n3k0chan.klip.core.SyncSnapshot
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json
import java.io.File
import java.nio.file.Files
import java.nio.file.StandardCopyOption
import java.util.UUID

/** A paired device as stored on disk. The pair secret is kept protected. */
@Serializable
data class PeerConfig(
    val deviceId: String,
    val name: String,
    val platform: String = "unknown",
    val host: String,
    val port: Int = Protocol.HTTP_PORT,
    val enabled: Boolean = true,
    /** Base64 of the pair secret after [SecretProtector.protect]. */
    val protectedSecret: String? = null,
    val lastSeenMs: Long? = null,
    val secretIssuedAt: Long = 0,
    val secretIssuer: String = "manual",
) {
    val secretVersion get() = SecretVersion(secretIssuedAt, secretIssuer)

    fun toMemberInfo() = MemberInfo(deviceId, name, platform, port, host)
}

/** A group introduction that could not be delivered yet (docs/protocol.md §8.3). */
@Serializable
data class PendingIntroduction(
    val receiverId: String,
    val member: MemberInfo,
    val issuedAt: Long,
    val protectedSecret: String,
    val createdAt: Long,
)

@Serializable
data class NodeConfig(
    val deviceId: String = UUID.randomUUID().toString(),
    val deviceName: String = "Klip",
    val platform: String = "android",
    val httpPort: Int = Protocol.HTTP_PORT,
    val syncEnabled: Boolean = true,
    val peers: List<PeerConfig> = emptyList(),
    val pendingIntroductions: List<PendingIntroduction> = emptyList(),
    val sync: SyncSnapshot? = null,
)

/** Protects secrets at rest (Android Keystore on the phone). */
interface SecretProtector {
    fun protect(data: ByteArray): ByteArray
    fun unprotect(data: ByteArray): ByteArray
}

/** No protection. Only for development tools and tests. */
class PlainSecretProtector : SecretProtector {
    override fun protect(data: ByteArray) = data.copyOf()
    override fun unprotect(data: ByteArray) = data.copyOf()
}

/** Loads and atomically saves [NodeConfig] as JSON. */
class ConfigStore(val file: File) {
    private val json = Json {
        ignoreUnknownKeys = true
        encodeDefaults = true
        prettyPrint = true
    }

    @Synchronized
    fun loadOrCreate(defaults: () -> NodeConfig = { NodeConfig() }): NodeConfig {
        if (file.exists()) return json.decodeFromString(NodeConfig.serializer(), file.readText())
        return defaults().also { save(it) }
    }

    @Synchronized
    fun save(config: NodeConfig) {
        file.absoluteFile.parentFile?.mkdirs()
        val tmp = File(file.path + ".tmp")
        tmp.writeText(json.encodeToString(NodeConfig.serializer(), config))
        Files.move(tmp.toPath(), file.toPath(), StandardCopyOption.REPLACE_EXISTING, StandardCopyOption.ATOMIC_MOVE)
    }
}
