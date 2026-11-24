package com.n3k0chan.klip.core

import java.util.Base64

fun ByteArray.toHex(): String {
    val chars = CharArray(size * 2)
    for (i in indices) {
        val v = this[i].toInt() and 0xFF
        chars[i * 2] = HEX[v ushr 4]
        chars[i * 2 + 1] = HEX[v and 0x0F]
    }
    return String(chars)
}

fun String.hexToBytes(): ByteArray {
    require(length % 2 == 0) { "Odd hex length" }
    return ByteArray(length / 2) { i -> substring(i * 2, i * 2 + 2).toInt(16).toByte() }
}

fun ByteArray.toBase64(): String = Base64.getEncoder().encodeToString(this)

fun String.base64ToBytes(): ByteArray = Base64.getDecoder().decode(this)

private val HEX = "0123456789abcdef".toCharArray()
