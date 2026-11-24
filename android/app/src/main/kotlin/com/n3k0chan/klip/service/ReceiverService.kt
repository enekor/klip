package com.n3k0chan.klip.service

import android.app.Service
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.IBinder
import android.util.Log
import androidx.core.app.ServiceCompat
import androidx.core.content.ContextCompat
import com.n3k0chan.klip.KlipApp
import com.n3k0chan.klip.klip
import com.n3k0chan.klip.platform.Notifications
import kotlinx.coroutines.launch

/**
 * Foreground service (type specialUse, plan §3.1) that keeps the HTTP server alive so the clipboard can be
 * received with the app closed or the screen off. The server is reference-counted in the node, so the main
 * screen can also hold it open for pairing while this service is stopped.
 */
class ReceiverService : Service() {
    private var holdsServer = false

    override fun onCreate() {
        super.onCreate()
        ServiceCompat.startForeground(
            this,
            Notifications.ID_SERVICE,
            Notifications.service(this),
            if (Build.VERSION.SDK_INT >= 34) ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE else 0,
        )
        klip.appScope.launch {
            try {
                klip.node.acquireServer()
                holdsServer = true
            } catch (e: Exception) {
                Log.e(KlipApp.TAG, "No se pudo arrancar el servidor", e)
                stopSelf()
            }
        }
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int = START_STICKY

    override fun onDestroy() {
        if (holdsServer) {
            holdsServer = false
            klip.appScope.launch { klip.node.releaseServer() }
        }
        super.onDestroy()
    }

    override fun onBind(intent: Intent?): IBinder? = null

    companion object {
        fun start(context: Context) = ContextCompat.startForegroundService(context, Intent(context, ReceiverService::class.java))
        fun stop(context: Context) = context.stopService(Intent(context, ReceiverService::class.java))
    }
}

/** Restarts the receiver after a reboot or an app update, if the user enabled it. */
class BootReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        val prefs = context.klip.prefs
        val boot = intent.action == Intent.ACTION_BOOT_COMPLETED
        val update = intent.action == Intent.ACTION_MY_PACKAGE_REPLACED
        if (prefs.receiverEnabled && ((boot && prefs.startOnBoot) || update)) ReceiverService.start(context)
    }
}
