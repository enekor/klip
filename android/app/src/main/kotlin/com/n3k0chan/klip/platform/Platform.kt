package com.n3k0chan.klip.platform

import android.content.ClipData
import android.content.ClipboardManager
import android.content.ComponentName
import android.content.Context
import android.content.pm.PackageManager
import android.net.ConnectivityManager
import android.os.Handler
import android.os.Looper
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Log
import androidx.core.content.FileProvider
import androidx.core.content.edit
import com.n3k0chan.klip.KlipApp
import com.n3k0chan.klip.node.ClipboardWriter
import com.n3k0chan.klip.node.SecretProtector
import java.net.Inet4Address
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

/** Pair secrets at rest: AES-GCM with a non-exportable key in the Android Keystore. Output = iv(12) || ct. */
class KeystoreProtector : SecretProtector {
    private val key: SecretKey by lazy {
        val ks = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        (ks.getKey(ALIAS, null) as? SecretKey) ?: KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore").run {
            init(
                KeyGenParameterSpec.Builder(ALIAS, KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
                    .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                    .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                    .setKeySize(256)
                    .build()
            )
            generateKey()
        }
    }

    override fun protect(data: ByteArray): ByteArray {
        val cipher = Cipher.getInstance("AES/GCM/NoPadding").apply { init(Cipher.ENCRYPT_MODE, key) }
        return cipher.iv + cipher.doFinal(data)
    }

    override fun unprotect(data: ByteArray): ByteArray {
        val cipher = Cipher.getInstance("AES/GCM/NoPadding").apply {
            init(Cipher.DECRYPT_MODE, key, GCMParameterSpec(128, data, 0, 12))
        }
        return cipher.doFinal(data, 12, data.size - 12)
    }

    private companion object {
        const val ALIAS = "klip-pair-secrets"
    }
}

/**
 * Writes received text to the clipboard. Android allows writing from the background (only reading is restricted).
 * Very large texts may not fit through Binder: then the user gets a notification instead (plan §3.6).
 */
class AndroidClipboardWriter(private val context: Context) : ClipboardWriter {
    private val main = Handler(Looper.getMainLooper())

    /**
     * Images are saved in the cache and placed on the clipboard as a content:// URI (FileProvider). Only the
     * latest few are kept; the system grants read access to whichever app pastes.
     */
    override fun writeImage(data: ByteArray, mimeType: String) {
        val dir = java.io.File(context.cacheDir, "clips").apply { mkdirs() }
        dir.listFiles()?.sortedByDescending { it.lastModified() }?.drop(KEEP_IMAGES - 1)?.forEach { it.delete() }
        val file = java.io.File(dir, "klip-${System.currentTimeMillis()}.${if (mimeType == "image/jpeg") "jpg" else "png"}")
        file.writeBytes(data)
        val uri = FileProvider.getUriForFile(context, "${context.packageName}.clips", file)
        main.post {
            try {
                context.getSystemService(ClipboardManager::class.java)
                    .setPrimaryClip(ClipData.newUri(context.contentResolver, "Klip", uri))
            } catch (e: RuntimeException) {
                Log.w(KlipApp.TAG, "Clipboard image write failed", e)
            }
        }
    }

    private companion object {
        const val KEEP_IMAGES = 5
    }

    override fun writeText(text: String) {
        main.post {
            try {
                context.getSystemService(ClipboardManager::class.java).setPrimaryClip(ClipData.newPlainText("Klip", text))
            } catch (e: RuntimeException) {
                // TransactionTooLargeException arrives wrapped in a RuntimeException.
                Log.w(KlipApp.TAG, "Clipboard write failed (${text.length} chars)", e)
                Notifications.tooLarge(context, text.length)
            }
        }
    }
}

/** Small persistent settings that are Android-only (the node keeps its own config file). */
class AppPrefs(context: Context) {
    private val sp = context.getSharedPreferences("klip_prefs", Context.MODE_PRIVATE)

    var receiverEnabled: Boolean
        get() = sp.getBoolean("receiverEnabled", true)
        set(v) = sp.edit { putBoolean("receiverEnabled", v) }

    var startOnBoot: Boolean
        get() = sp.getBoolean("startOnBoot", true)
        set(v) = sp.edit { putBoolean("startOnBoot", v) }
}

/** Enables or disables a manifest component; disabled capture paths disappear from the system UI. */
object Components {
    fun isEnabled(context: Context, cls: Class<*>): Boolean =
        context.packageManager.getComponentEnabledSetting(ComponentName(context, cls)) != PackageManager.COMPONENT_ENABLED_STATE_DISABLED

    fun setEnabled(context: Context, cls: Class<*>, enabled: Boolean) {
        context.packageManager.setComponentEnabledSetting(
            ComponentName(context, cls),
            if (enabled) PackageManager.COMPONENT_ENABLED_STATE_ENABLED else PackageManager.COMPONENT_ENABLED_STATE_DISABLED,
            PackageManager.DONT_KILL_APP,
        )
    }
}

object NetworkInfo {
    /** IPv4 addresses of the active network (normally Wi-Fi). */
    fun localIPv4(context: Context): List<String> {
        val cm = context.getSystemService(ConnectivityManager::class.java)
        val props = cm.getLinkProperties(cm.activeNetwork) ?: return emptyList()
        return props.linkAddresses.mapNotNull { (it.address as? Inet4Address)?.hostAddress }
    }
}
