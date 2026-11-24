package com.n3k0chan.klip.platform

import android.content.Context
import android.net.nsd.NsdManager
import android.net.nsd.NsdServiceInfo
import android.os.Build
import android.util.Log
import com.n3k0chan.klip.KlipApp
import com.n3k0chan.klip.core.Protocol
import com.n3k0chan.klip.node.PeerResolver
import kotlinx.coroutines.CompletableDeferred
import java.net.Inet4Address
import java.util.concurrent.Executors

/**
 * mDNS/DNS-SD via NsdManager (docs/protocol.md §8.7): announces `_klip._tcp` while the server runs and
 * resolves a peer's current IP when a send fails because its address changed.
 */
class NsdResolver(private val context: Context) : PeerResolver {
    private val nsd = context.getSystemService(NsdManager::class.java)

    /** A PC may answer with several IPs (Wi-Fi, Hyper-V, WSL…): prefer one in our own /24. */
    private fun pickSameSubnet(candidates: List<String>): String? {
        val prefixes = NetworkInfo.localIPv4(context).map { it.substringBeforeLast('.') + "." }
        return candidates.firstOrNull { c -> prefixes.any { c.startsWith(it) } } ?: candidates.firstOrNull()
    }
    private val executor = Executors.newSingleThreadExecutor()
    private var registration: NsdManager.RegistrationListener? = null

    @Synchronized
    override fun register(deviceId: String, port: Int) {
        if (registration != null) return
        val info = NsdServiceInfo().apply {
            serviceName = deviceId
            serviceType = Protocol.MDNS_SERVICE_TYPE
            this.port = port
            setAttribute("id", deviceId)
        }
        val listener = object : NsdManager.RegistrationListener {
            override fun onServiceRegistered(info: NsdServiceInfo) {
                Log.i(KlipApp.TAG, "mDNS registrado: ${info.serviceName}")
            }

            override fun onRegistrationFailed(info: NsdServiceInfo, error: Int) {
                Log.w(KlipApp.TAG, "mDNS registro falló: $error")
            }
            override fun onServiceUnregistered(info: NsdServiceInfo) = Unit
            override fun onUnregistrationFailed(info: NsdServiceInfo, error: Int) = Unit
        }
        nsd.registerService(info, NsdManager.PROTOCOL_DNS_SD, listener)
        registration = listener
    }

    @Synchronized
    override fun unregister() {
        registration?.let { runCatching { nsd.unregisterService(it) } }
        registration = null
    }

    /** The caller applies the timeout (1.5 s); discovery is always stopped when this returns or is cancelled. */
    override suspend fun resolve(deviceId: String): String? {
        val result = CompletableDeferred<String?>()
        val discovery = object : NsdManager.DiscoveryListener {
            override fun onDiscoveryStarted(serviceType: String) = Unit
            override fun onDiscoveryStopped(serviceType: String) = Unit
            override fun onStartDiscoveryFailed(serviceType: String, errorCode: Int) { result.complete(null) }
            override fun onStopDiscoveryFailed(serviceType: String, errorCode: Int) = Unit
            override fun onServiceLost(info: NsdServiceInfo) = Unit
            override fun onServiceFound(info: NsdServiceInfo) {
                if (info.serviceName == deviceId) resolveHost(info) { result.complete(it) }
            }
        }
        nsd.discoverServices(Protocol.MDNS_SERVICE_TYPE, NsdManager.PROTOCOL_DNS_SD, discovery)
        return try {
            result.await()
        } finally {
            runCatching { nsd.stopServiceDiscovery(discovery) }
        }
    }

    private fun resolveHost(info: NsdServiceInfo, done: (String?) -> Unit) {
        if (Build.VERSION.SDK_INT >= 34) {
            val callback = object : NsdManager.ServiceInfoCallback {
                override fun onServiceInfoCallbackRegistrationFailed(errorCode: Int) = done(null)
                override fun onServiceUpdated(resolved: NsdServiceInfo) {
                    done(pickSameSubnet(resolved.hostAddresses.filterIsInstance<Inet4Address>().mapNotNull { it.hostAddress }))
                    runCatching { nsd.unregisterServiceInfoCallback(this) }
                }
                override fun onServiceLost() = Unit
                override fun onServiceInfoCallbackUnregistered() = Unit
            }
            nsd.registerServiceInfoCallback(info, executor, callback)
        } else {
            @Suppress("DEPRECATION")
            nsd.resolveService(info, object : NsdManager.ResolveListener {
                override fun onResolveFailed(info: NsdServiceInfo, errorCode: Int) = done(null)
                override fun onServiceResolved(resolved: NsdServiceInfo) = done((resolved.host as? Inet4Address)?.hostAddress)
            })
        }
    }
}
