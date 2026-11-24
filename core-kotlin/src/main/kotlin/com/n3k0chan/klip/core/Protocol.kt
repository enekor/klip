package com.n3k0chan.klip.core

/** Protocol constants. See docs/protocol.md §2. */
object Protocol {
    const val VERSION = 1
    const val HTTP_PORT = 8765
    const val DISCOVERY_PORT = 8766
    const val MDNS_SERVICE_TYPE = "_klip._tcp"
    const val TIMESTAMP_WINDOW_MS = 30_000L
    const val NONCE_TTL_MS = 60_000L
    const val PROCESSED_IDS_CAPACITY = 50
    const val PAIRING_NONCE_BYTES = 32
    const val REQUEST_NONCE_BYTES = 16
    const val AES_GCM_IV_BYTES = 12
    const val AES_GCM_TAG_BYTES = 16

    const val HEADER_DEVICE = "X-Klip-Device"
    const val HEADER_TIMESTAMP = "X-Klip-Timestamp"
    const val HEADER_NONCE = "X-Klip-Nonce"
    const val HEADER_SIGNATURE = "X-Klip-Signature"
}
