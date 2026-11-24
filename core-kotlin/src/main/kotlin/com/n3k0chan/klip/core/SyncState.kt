package com.n3k0chan.klip.core

import kotlinx.serialization.Serializable

enum class OutgoingDecision {
    SEND,
    SKIP_DISABLED,
    SKIP_SENSITIVE,
    SKIP_BLANK,
    SKIP_ALREADY_SENT,
    SKIP_JUST_RECEIVED,
}

enum class IncomingDecision(val httpStatus: Int) {
    ACCEPT(200),
    IGNORE_SELF(409),
    IGNORE_DUPLICATE(409),
}

/** Persistable copy of [SyncState]. */
@Serializable
data class SyncSnapshot(
    val lastSentHash: String? = null,
    val lastReceivedHash: String? = null,
    val processedMessageIds: List<String> = emptyList(),
)

/** Anti-loop and anti-duplicate rules. See docs/protocol.md §7. Thread-safe. */
class SyncState(private val localDeviceId: String, snapshot: SyncSnapshot = SyncSnapshot()) {
    private var lastSentHash: String? = snapshot.lastSentHash
    private var lastReceivedHash: String? = snapshot.lastReceivedHash
    private val processedIds = ArrayDeque(snapshot.processedMessageIds.takeLast(Protocol.PROCESSED_IDS_CAPACITY))

    @Synchronized
    fun evaluateOutgoing(content: String, isTest: Boolean = false, isSensitive: Boolean = false, syncEnabled: Boolean = true): OutgoingDecision {
        if (!syncEnabled) return OutgoingDecision.SKIP_DISABLED
        if (isSensitive) return OutgoingDecision.SKIP_SENSITIVE
        if (Content.isBlank(content)) return OutgoingDecision.SKIP_BLANK
        if (isTest) return OutgoingDecision.SEND
        val hash = Content.hash(content)
        if (hash == lastSentHash) return OutgoingDecision.SKIP_ALREADY_SENT
        if (hash == lastReceivedHash) return OutgoingDecision.SKIP_JUST_RECEIVED
        return OutgoingDecision.SEND
    }

    /** Call after a non-test send. */
    @Synchronized
    fun markSent(contentHash: String) {
        lastSentHash = contentHash
    }

    /**
     * Applies the incoming rules. On [IncomingDecision.ACCEPT] the message is recorded and
     * lastReceivedHash updated, so call this BEFORE writing to the clipboard.
     */
    @Synchronized
    fun acceptIncoming(originDeviceId: String, messageId: String, contentHash: String): IncomingDecision {
        if (originDeviceId == localDeviceId) return IncomingDecision.IGNORE_SELF
        if (messageId in processedIds) return IncomingDecision.IGNORE_DUPLICATE
        processedIds.addLast(messageId)
        while (processedIds.size > Protocol.PROCESSED_IDS_CAPACITY) processedIds.removeFirst()
        lastReceivedHash = contentHash
        return IncomingDecision.ACCEPT
    }

    @Synchronized
    fun snapshot() = SyncSnapshot(lastSentHash, lastReceivedHash, processedIds.toList())
}
