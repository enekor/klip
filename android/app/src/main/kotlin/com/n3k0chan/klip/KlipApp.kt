package com.n3k0chan.klip

import android.app.Application
import android.content.Context
import android.os.Build
import android.provider.Settings
import android.util.Log
import com.n3k0chan.klip.node.ConfigStore
import com.n3k0chan.klip.node.NodeConfig
import com.n3k0chan.klip.node.NodeEvent
import com.n3k0chan.klip.node.SyncNode
import com.n3k0chan.klip.platform.AndroidClipboardWriter
import com.n3k0chan.klip.platform.AppPrefs
import com.n3k0chan.klip.platform.KeystoreProtector
import com.n3k0chan.klip.platform.Notifications
import com.n3k0chan.klip.platform.NsdResolver
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch
import java.io.File

/** Owns the single [SyncNode] shared by the receiver service, the main screen and the capture activities. */
class KlipApp : Application() {
    /** Outlives activities: capture activities hand their sends to this scope before calling finish(). */
    val appScope = CoroutineScope(SupervisorJob() + Dispatchers.Default)

    lateinit var node: SyncNode
        private set
    lateinit var prefs: AppPrefs
        private set

    override fun onCreate() {
        super.onCreate()
        prefs = AppPrefs(this)
        Notifications.createChannels(this)
        node = SyncNode(
            store = ConfigStore(File(filesDir, "klip-config.json")),
            protector = KeystoreProtector(),
            clipboard = AndroidClipboardWriter(this),
            defaults = { NodeConfig(deviceName = defaultDeviceName(), platform = "android") },
        )
        node.resolver = NsdResolver(this)
        appScope.launch {
            node.events.collect { event ->
                when (event) {
                    is NodeEvent.TestReceived -> Notifications.testReceived(this@KlipApp, event.from)
                    is NodeEvent.Log -> Log.i(TAG, event.message)
                    else -> Unit
                }
            }
        }
    }

    private fun defaultDeviceName(): String =
        Settings.Global.getString(contentResolver, Settings.Global.DEVICE_NAME)?.takeIf { it.isNotBlank() }
            ?: "${Build.MANUFACTURER.replaceFirstChar { it.uppercase() }} ${Build.MODEL}"

    companion object {
        const val TAG = "Klip"
    }
}

val Context.klip: KlipApp get() = applicationContext as KlipApp
