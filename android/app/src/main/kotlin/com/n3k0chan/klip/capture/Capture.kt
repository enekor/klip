package com.n3k0chan.klip.capture

import android.annotation.SuppressLint
import android.app.Activity
import android.app.PendingIntent
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.service.quicksettings.Tile
import android.service.quicksettings.TileService
import android.util.Log
import android.widget.Toast
import com.n3k0chan.klip.KlipApp
import com.n3k0chan.klip.core.OutgoingDecision
import com.n3k0chan.klip.klip
import com.n3k0chan.klip.node.SendOutcome
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.ByteArrayOutputStream

/** Shared sending logic for the capture paths. Runs in the app scope so it survives finish(). */
internal object Sender {
    fun send(context: Context, text: String?, isSensitive: Boolean = false) {
        val app = context.klip
        if (text == null) {
            toast(app, "El portapapeles no contiene texto ni imagen")
            return
        }
        app.appScope.launch {
            val outcome = app.node.sendClipboard(text, isSensitive = isSensitive)
            withContext(Dispatchers.Main) { toast(app, describe(outcome, "Enviado")) }
        }
    }

    /**
     * Sends the image behind [uri]. PNG and JPEG travel as they are; other formats (WebP, HEIC, GIF…) are
     * converted to PNG. The bytes are read right away: the permission to read another app's URI is temporary.
     */
    fun sendImage(context: Context, uri: Uri) {
        val app = context.klip
        app.appScope.launch {
            val message = try {
                val resolver = app.contentResolver
                val type = resolver.getType(uri) ?: "image/png"
                val raw = resolver.openInputStream(uri)?.use { it.readBytes() } ?: error("No se pudo abrir la imagen")
                val (bytes, mime) = if (type == "image/png" || type == "image/jpeg") raw to type else toPng(raw) to "image/png"
                describe(app.node.sendImage(bytes, mime), "Imagen enviada")
            } catch (e: Exception) {
                Log.w(KlipApp.TAG, "No se pudo enviar la imagen", e)
                "No se pudo leer la imagen: ${e.message}"
            }
            withContext(Dispatchers.Main) { toast(app, message) }
        }
    }

    private fun toPng(bytes: ByteArray): ByteArray {
        val bitmap = BitmapFactory.decodeByteArray(bytes, 0, bytes.size) ?: error("Formato de imagen no soportado")
        return ByteArrayOutputStream().use { out ->
            bitmap.compress(Bitmap.CompressFormat.PNG, 100, out)
            bitmap.recycle()
            out.toByteArray()
        }
    }

    private fun describe(outcome: SendOutcome, sent: String): String = when (outcome.decision) {
        OutgoingDecision.SEND -> when {
            outcome.results.isEmpty() -> "No hay dispositivos vinculados activos"
            outcome.results.all { it.ok } -> "$sent a ${outcome.results.joinToString { it.name }}"
            outcome.anyOk -> "$sent a ${outcome.results.filter { it.ok }.joinToString { it.name }}; " +
                "falló: ${outcome.results.filter { !it.ok }.joinToString { it.name }}"
            else -> "No se pudo enviar: ${outcome.results.first().error}"
        }
        OutgoingDecision.SKIP_ALREADY_SENT, OutgoingDecision.SKIP_JUST_RECEIVED -> "Sin cambios nuevos"
        OutgoingDecision.SKIP_SENSITIVE -> "Contenido sensible: no se envía"
        OutgoingDecision.SKIP_BLANK -> "El portapapeles está vacío"
        OutgoingDecision.SKIP_DISABLED -> "La sincronización está desactivada"
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
        try {
            val clip = getSystemService(ClipboardManager::class.java).primaryClip
            val item = clip?.takeIf { it.itemCount > 0 }?.getItemAt(0)
            // Password managers mark copies as sensitive (plan §3.5). The key works on any API level.
            val sensitive = clip?.description?.extras?.getBoolean("android.content.extra.IS_SENSITIVE") == true
            val uri = item?.uri
            when {
                // An image: its content URI (never send the "content://…" address as if it were text).
                uri != null && clip.description.hasMimeType("image/*") -> Sender.sendImage(this, uri)
                item?.text != null -> Sender.send(this, item.text.toString(), sensitive)
                item?.htmlText != null -> Sender.send(this, item.coerceToText(this).toString(), sensitive)
                else -> Sender.send(this, null)
            }
        } catch (e: RuntimeException) {
            Log.w(KlipApp.TAG, "No se pudo leer el portapapeles", e) // e.g. a clip too large for Binder
            Sender.send(this, null)
        }
        finishSilently()
    }

    override fun onDestroy() {
        fallback.removeCallbacksAndMessages(null)
        super.onDestroy()
    }
}

/** "Share" target: text (ACTION_SEND text/plain) or an image (ACTION_SEND with an image MIME type). */
class ShareActivity : Activity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        if (intent.type?.startsWith("image/") == true) {
            val uri = if (Build.VERSION.SDK_INT >= 33) {
                intent.getParcelableExtra(Intent.EXTRA_STREAM, Uri::class.java)
            } else {
                @Suppress("DEPRECATION")
                intent.getParcelableExtra(Intent.EXTRA_STREAM)
            }
            if (uri != null) Sender.sendImage(this, uri) else Sender.send(this, null)
        } else {
            val text = intent.getCharSequenceExtra(Intent.EXTRA_TEXT)?.toString()
                ?: intent.getCharSequenceExtra(Intent.EXTRA_SUBJECT)?.toString()
            Sender.send(this, text)
        }
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
