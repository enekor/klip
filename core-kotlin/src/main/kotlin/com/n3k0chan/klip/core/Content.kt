package com.n3k0chan.klip.core

import java.text.Normalizer

/** Content normalization and hashing. See docs/protocol.md §3. */
object Content {
    fun normalize(s: String): String =
        Normalizer.normalize(s.replace("\r\n", "\n").replace("\r", "\n"), Normalizer.Form.NFC)

    fun hash(s: String): String = Crypto.sha256(normalize(s).toByteArray(Charsets.UTF_8)).toHex()

    /** Hash of an image: SHA-256 of its exact bytes (docs/protocol.md §3). */
    fun hashBytes(data: ByteArray): String = Crypto.sha256(data).toHex()

    /** Image formats carried by the protocol. */
    val IMAGE_MIME_TYPES = listOf("image/png", "image/jpeg")

    /** True if empty or made only of Unicode white space. */
    fun isBlank(s: String): Boolean = s.isBlank()
}
