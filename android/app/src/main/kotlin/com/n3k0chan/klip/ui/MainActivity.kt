package com.n3k0chan.klip.ui

import android.Manifest
import android.content.Intent
import android.content.pm.PackageManager
import android.net.wifi.WifiManager
import android.os.Build
import android.os.Bundle
import android.os.PowerManager
import android.provider.Settings
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.activity.viewModels
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.dynamicDarkColorScheme
import androidx.compose.material3.dynamicLightColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.platform.LocalContext
import androidx.core.net.toUri
import androidx.lifecycle.lifecycleScope
import com.n3k0chan.klip.klip
import com.n3k0chan.klip.service.ReceiverService
import kotlinx.coroutines.launch

/**
 * Main screen (plan §3.1). While visible: pairing window open, UDP radar running (with a multicast lock)
 * and the HTTP server held open so other devices can pair with this one.
 */
class MainActivity : ComponentActivity() {
    private val vm: MainViewModel by viewModels()
    private var multicastLock: WifiManager.MulticastLock? = null
    private var holdsServer = false

    /** Bumped when returning from system settings so permission banners refresh. */
    private var refreshTick by mutableIntStateOf(0)

    private val notificationPermission = registerForActivityResult(ActivityResultContracts.RequestPermission()) { refreshTick++ }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        if (klip.prefs.receiverEnabled) ReceiverService.start(this)
        setContent {
            val dark = isSystemInDarkTheme()
            val context = LocalContext.current
            val colors = when {
                Build.VERSION.SDK_INT >= 31 && dark -> dynamicDarkColorScheme(context)
                Build.VERSION.SDK_INT >= 31 -> dynamicLightColorScheme(context)
                dark -> darkColorScheme()
                else -> lightColorScheme()
            }
            MaterialTheme(colorScheme = colors) {
                MainScreen(
                    vm = vm,
                    refreshTick = refreshTick,
                    needsNotificationPermission = ::needsNotificationPermission,
                    requestNotificationPermission = { notificationPermission.launch(Manifest.permission.POST_NOTIFICATIONS) },
                    batteryOptimized = ::isBatteryOptimized,
                    requestBatteryExemption = ::requestBatteryExemption,
                )
            }
        }
    }

    override fun onStart() {
        super.onStart()
        refreshTick++
        val node = klip.node
        node.pairingOpen = true
        multicastLock = getSystemService(WifiManager::class.java).createMulticastLock("klip-radar").apply {
            setReferenceCounted(false)
            acquire()
        }
        node.startDiscovery()
        lifecycleScope.launch {
            try {
                node.acquireServer()
                holdsServer = true
            } catch (e: Exception) {
                vm.show("No se pudo abrir el puerto ${node.httpPort}: ${e.message}")
            }
        }
    }

    override fun onStop() {
        val node = klip.node
        node.pairingOpen = false
        node.stopDiscovery()
        multicastLock?.release()
        multicastLock = null
        if (holdsServer) {
            holdsServer = false
            klip.appScope.launch { node.releaseServer() }
        }
        super.onStop()
    }

    private fun needsNotificationPermission() =
        Build.VERSION.SDK_INT >= 33 && checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED

    private fun isBatteryOptimized() = !getSystemService(PowerManager::class.java).isIgnoringBatteryOptimizations(packageName)

    @Suppress("BatteryLife") // the user asks for it explicitly: it keeps the receiver alive (plan §3.1)
    private fun requestBatteryExemption() {
        startActivity(Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS, "package:$packageName".toUri()))
    }
}
