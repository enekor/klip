package com.n3k0chan.klip.capture

import android.annotation.SuppressLint
import android.app.Activity
import android.app.PendingIntent
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.service.quicksettings.Tile
import android.service.quicksettings.TileService
import android.widget.Toast
import com.n3k0chan.klip.core.OutgoingDecision
import com.n3k0chan.klip.klip
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/** Shared sending logic for the three capture paths. Runs in the app scope so it survives finish(). */
internal object Sender {
    fun send(context: Context, text: String?, isSensitive: Boolean = false) {
        val app = context.klip
        if (text == null) {
            toast(app, "No hay texto que enviar")
            return
        }
        app.appScope.launch {
            val outcome = app.node.sendClipboard(text, isSensitive = isSensitive)
            val message = when (outcome.decision) {
                OutgoingDecision.SEND -> when {
                    outcome.results.isEmpty() -> "No hay dispositivos vinculados activos"
                    outcome.results.all { it.ok } -> "Enviado a ${outcome.results.joinToString { it.name }}"
                    outcome.anyOk -> "Enviado a ${outcome.results.filter { it.ok }.joinToString { it.name }}; " +
                        "falló: ${outcome.results.filter { !it.ok }.joinToString { it.name }}"
                    else -> "No se pudo enviar: ${outcome.results.first().error}"
                }
                OutgoingDecision.SKIP_ALREADY_SENT, OutgoingDecision.SKIP_JUST_RECEIVED -> "Sin cambios nuevos"
                OutgoingDecision.SKIP_SENSITIVE -> "Contenido sensible: no se envía"
                OutgoingDecision.SKIP_BLANK -> "El portapapeles está vacío"
                OutgoingDecision.SKIP_DISABLED -> "La sincronización está desactivada"
            }
            withContext(Dispatchers.Main) { toast(app, message) }
        }
    }

    private fun toast(context: Context, message: String) = Toast.makeText(context, message, Toast.LENGTH_SHORT).show()
}

private fun Activity.finishSilently() {
    finish()
    if (Build.VERSION.SDK_INT >= 34) {
        overrideActivityTransition(Activity.OVERRIDE_TRANSITION_CLOSE, 0, 0)
    } else {
        @Suppress("DEPRECATION")
        overridePendingTransition(0, 0)
    }
}

/**
 * Invisible activity launched by the Quick Settings tile. Android 10+ only lets the focused app read the
 * clipboard, so it waits for window focus, reads, hands the send to the app scope and closes (plan §3.1).
 */
class CaptureActivity : Activity() {
    private val fallback = Handler(Looper.getMainLooper())
    private var done = false

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        // Some OEMs never deliver focus to a translucent window: don't stay stuck on screen.
        fallback.postDelayed({ if (!done) { done = true; finishSilently() } }, 1_500)
    }

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (!hasFocus || done) return
        done = true
        val clip = getSystemService(ClipboardManager::class.java).primaryClip
        // Password managers mark copies as sensitive (plan §3.5). The key works on any API level.
        val sensitive = clip?.description?.extras?.getBoolean("android.content.extra.IS_SENSITIVE") == true
        val text = try {
            clip?.takeIf { it.itemCount > 0 }?.getItemAt(0)?.coerceToText(this)?.toString()
        } catch (e: RuntimeException) {
            null // e.g. a clip too large to read through Binder
        }
        Sender.send(this, text, sensitive)
        finishSilently()
    }

    override fun onDestroy() {
        fallback.removeCallbacksAndMessages(null)
        super.onDestroy()
    }
}

/** "Share" target (ACTION_SEND text/plain). Does not read the clipboard. */
class ShareActivity : Activity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val text = intent.getCharSequenceExtra(Intent.EXTRA_TEXT)?.toString()
            ?: intent.getCharSequenceExtra(Intent.EXTRA_SUBJECT)?.toString()
        Sender.send(this, text)
        finishSilently()
    }
}

/** Text-selection menu entry (ACTION_PROCESS_TEXT). Sends the selection and leaves it unchanged. */
class ProcessTextActivity : Activity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        Sender.send(this, intent.getCharSequenceExtra(Intent.EXTRA_PROCESS_TEXT)?.toString())
        setResult(RESULT_CANCELED)
        finishSilently()
    }
}

/** Quick Settings tile: copy -> pull down the shade -> tap. */
class QuickTileService : TileService() {
    override fun onStartListening() {
        qsTile?.apply {
            state = Tile.STATE_INACTIVE
            updateTile()
        }
    }

    @SuppressLint("StartActivityAndCollapseDeprecated") // the Intent overload is only used below API 34
    override fun onClick() {
        val intent = Intent(this, CaptureActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        if (Build.VERSION.SDK_INT >= 34) {
            startActivityAndCollapse(PendingIntent.getActivity(this, 0, intent, PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT))
        } else {
            @Suppress("DEPRECATION")
            startActivityAndCollapse(intent)
        }
    }
}
