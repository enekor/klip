package com.n3k0chan.klip.ui

import android.app.Application
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.n3k0chan.klip.klip
import com.n3k0chan.klip.node.IncomingPairing
import com.n3k0chan.klip.node.NodeEvent
import com.n3k0chan.klip.node.OutgoingPairing
import com.n3k0chan.klip.node.PairingStatus
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch

/** Pairing dialogs and transient messages for [MainActivity]. */
class MainViewModel(app: Application) : AndroidViewModel(app) {
    val node = app.klip.node

    sealed interface Dialog {
        data class Connecting(val name: String) : Dialog
        data class ConfirmPin(val pairing: OutgoingPairing) : Dialog
        data class WaitingOther(val name: String) : Dialog
        data class Incoming(val pairing: IncomingPairing, val accepted: Boolean = false) : Dialog
    }

    private val _dialog = MutableStateFlow<Dialog?>(null)
    val dialog = _dialog.asStateFlow()

    private val _message = MutableStateFlow<String?>(null)
    val message = _message.asStateFlow()

    init {
        viewModelScope.launch {
            node.events.collect { e ->
                when (e) {
                    is NodeEvent.PairingRequested -> _dialog.value = Dialog.Incoming(e.pairing)
                    is NodeEvent.IncomingPairingFinished -> {
                        if ((_dialog.value as? Dialog.Incoming)?.pairing?.sessionId == e.sessionId) _dialog.value = null
                        _message.value = when (e.status) {
                            PairingStatus.ACCEPTED -> "Vinculado con ${e.deviceName}"
                            PairingStatus.REJECTED -> "Vinculación con ${e.deviceName} rechazada"
                            PairingStatus.CANCELLED -> "${e.deviceName} canceló la vinculación"
                            PairingStatus.TIMED_OUT -> "La vinculación con ${e.deviceName} caducó"
                            PairingStatus.FAILED -> "La vinculación con ${e.deviceName} falló"
                        }
                    }
                    is NodeEvent.TestReceived -> _message.value = "Prueba recibida de ${e.from}: «${e.content}»"
                    else -> Unit
                }
            }
        }
    }

    fun consumeMessage() {
        _message.value = null
    }

    fun show(message: String) {
        _message.value = message
    }

    // ---------- Outgoing pairing ----------

    fun startPairing(name: String, host: String, port: Int) {
        _dialog.value = Dialog.Connecting(name)
        viewModelScope.launch {
            node.startPairing(host, port)
                .onSuccess { _dialog.value = Dialog.ConfirmPin(it) }
                .onFailure {
                    _dialog.value = null
                    _message.value = it.message
                }
        }
    }

    fun confirmPin(pairing: OutgoingPairing) {
        _dialog.value = Dialog.WaitingOther(pairing.deviceName)
        viewModelScope.launch {
            val outcome = node.confirmPairing(pairing)
            _dialog.value = null
            _message.value = if (outcome.ok) "Vinculado con ${pairing.deviceName}" else outcome.message
        }
    }

    fun rejectPin(pairing: OutgoingPairing) {
        _dialog.value = null
        viewModelScope.launch { node.cancelPairing(pairing) }
    }

    // ---------- Incoming pairing ----------

    fun answerIncoming(pairing: IncomingPairing, accept: Boolean) {
        node.respondToPairing(pairing.sessionId, accept)
        // Accepted: keep the dialog open ("waiting for the other device") until IncomingPairingFinished.
        _dialog.value = if (accept) Dialog.Incoming(pairing, accepted = true) else null
    }

    // ---------- Peers ----------

    fun sendTest(deviceId: String? = null) = viewModelScope.launch {
        val outcome = node.sendTest(deviceId)
        _message.value = when {
            outcome.results.isEmpty() -> "No hay dispositivos activos"
            else -> outcome.results.joinToString("\n") { if (it.ok) "✔ ${it.name}: enviado" else "✖ ${it.name}: ${it.error}" }
        }
    }

    fun ping(deviceId: String) = viewModelScope.launch {
        val r = node.ping(deviceId)
        _message.value = if (r.ok) "Conectado (${r.elapsedMs} ms)" else "Sin conexión: ${r.error}"
    }

    fun unpair(deviceId: String) = viewModelScope.launch { node.unpair(deviceId) }
    fun expel(deviceId: String) = viewModelScope.launch { node.expel(deviceId) }
}
