package com.n3k0chan.klip.ui

import android.graphics.BitmapFactory
import androidx.compose.foundation.Image
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.n3k0chan.klip.capture.ProcessTextActivity
import com.n3k0chan.klip.capture.QuickTileService
import com.n3k0chan.klip.capture.ShareActivity
import com.n3k0chan.klip.core.Protocol
import com.n3k0chan.klip.klip
import com.n3k0chan.klip.node.DiscoveredDevice
import com.n3k0chan.klip.node.PeerView
import com.n3k0chan.klip.node.ReceivedItem
import com.n3k0chan.klip.platform.Components
import com.n3k0chan.klip.platform.NetworkInfo
import com.n3k0chan.klip.service.ReceiverService
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun MainScreen(
    vm: MainViewModel,
    refreshTick: Int,
    needsNotificationPermission: () -> Boolean,
    requestNotificationPermission: () -> Unit,
    batteryOptimized: () -> Boolean,
    requestBatteryExemption: () -> Unit,
) {
    val context = LocalContext.current
    val node = vm.node
    val peers by node.peers.collectAsState()
    val discovered by node.discovered.collectAsState()
    val serverRunning by node.serverRunning.collectAsState()
    val lastReceived by node.lastReceived.collectAsState()
    val dialog by vm.dialog.collectAsState()
    val message by vm.message.collectAsState()
    val snackbar = remember { SnackbarHostState() }

    LaunchedEffect(message) {
        message?.let {
            snackbar.showSnackbar(it)
            vm.consumeMessage()
        }
    }

    Scaffold(
        topBar = { TopAppBar(title = { Text("Klip") }) },
        snackbarHost = { SnackbarHost(snackbar) },
    ) { padding ->
        LazyColumn(
            modifier = Modifier.fillMaxSize().padding(padding),
            contentPadding = PaddingValues(16.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            item {
                // Recomposed on refreshTick (returning from settings).
                val ips = remember(refreshTick, serverRunning) { NetworkInfo.localIPv4(context) }
                IdentityCard(node.deviceName, ips, node.httpPort, serverRunning, onRename = { node.deviceName = it })
            }
            item { LastReceivedCard(lastReceived) }
            item {
                val showNotif = remember(refreshTick) { needsNotificationPermission() }
                val showBattery = remember(refreshTick) { batteryOptimized() }
                if (showNotif) Banner("Permite las notificaciones para ver el aviso del receptor y las pruebas recibidas.", "Permitir", requestNotificationPermission)
                if (showBattery) Banner(
                    "Excluye a Klip de la optimización de batería para que el receptor siga activo con la pantalla apagada.",
                    "Excluir", requestBatteryExemption,
                )
            }

            item { SectionTitle("Dispositivos detectados en la red") }
            if (discovered.isEmpty()) {
                item { Hint("Abre Klip en tus otros dispositivos para que aparezcan aquí.") }
            }
            items(discovered, key = { "d-" + it.deviceId }) { d -> DiscoveredRow(d) { vm.startPairing(d.deviceName, d.host, d.httpPort) } }
            item { ManualPairing { host, port -> vm.startPairing(host, host, port) } }

            item {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    SectionTitle("Dispositivos vinculados", Modifier.weight(1f))
                    if (peers.isNotEmpty()) TextButton(onClick = { vm.sendTest() }) { Text("Probar todos") }
                }
            }
            if (peers.isEmpty()) item { Hint("Aún no hay dispositivos vinculados.") }
            items(peers, key = { "p-" + it.deviceId }) { p ->
                PeerRow(
                    p,
                    onEnabled = { node.setPeerEnabled(p.deviceId, it) },
                    onTest = { vm.sendTest(p.deviceId) },
                    onPing = { vm.ping(p.deviceId) },
                    onUnpair = { vm.unpair(p.deviceId) },
                    onExpel = { vm.expel(p.deviceId) },
                )
            }

            item { SectionTitle("Ajustes") }
            item { Settings(refreshTick) }
        }
    }

    when (val d = dialog) {
        is MainViewModel.Dialog.Connecting -> ProgressDialog("Contactando con ${d.name}…")
        is MainViewModel.Dialog.WaitingOther -> ProgressDialog("Esperando la confirmación en ${d.name}…")
        is MainViewModel.Dialog.ConfirmPin -> PinDialog(
            title = "Vincular con ${d.pairing.deviceName}",
            pin = d.pairing.pin,
            text = "Comprueba que ${d.pairing.deviceName} muestra el mismo código.",
            onAccept = { vm.confirmPin(d.pairing) },
            onReject = { vm.rejectPin(d.pairing) },
        )
        is MainViewModel.Dialog.Incoming ->
            if (d.accepted) ProgressDialog("Esperando la confirmación en ${d.pairing.deviceName}…")
            else PinDialog(
                title = "${d.pairing.deviceName} quiere vincularse",
                pin = d.pairing.pin,
                text = "Acepta solo si ${d.pairing.deviceName} muestra el mismo código.",
                onAccept = { vm.answerIncoming(d.pairing, true) },
                onReject = { vm.answerIncoming(d.pairing, false) },
            )
        null -> Unit
    }
}

@Composable
private fun IdentityCard(name: String, ips: List<String>, port: Int, serverRunning: Boolean, onRename: (String) -> Unit) {
    var editing by remember { mutableStateOf(false) }
    Card(Modifier.fillMaxWidth()) {
        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text(name, style = MaterialTheme.typography.titleLarge, modifier = Modifier.weight(1f))
                TextButton(onClick = { editing = true }) { Text("Cambiar") }
            }
            Text("Dirección: ${if (ips.isEmpty()) "sin Wi-Fi" else ips.joinToString()} · puerto $port", style = MaterialTheme.typography.bodyMedium)
            Text(
                if (serverRunning) "Receptor activo" else "Receptor detenido",
                color = if (serverRunning) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.error,
                style = MaterialTheme.typography.bodyMedium,
            )
        }
    }
    if (editing) {
        var value by remember { mutableStateOf(name) }
        AlertDialog(
            onDismissRequest = { editing = false },
            title = { Text("Nombre del dispositivo") },
            text = { OutlinedTextField(value, { value = it }, singleLine = true) },
            confirmButton = {
                TextButton(onClick = {
                    if (value.isNotBlank()) onRename(value.trim())
                    editing = false
                }) { Text("Guardar") }
            },
            dismissButton = { TextButton(onClick = { editing = false }) { Text("Cancelar") } },
        )
    }
}

/** What arrived last (test or real), so the user can check at a glance that sync works. */
@Composable
private fun LastReceivedCard(item: ReceivedItem?) {
    Card(Modifier.fillMaxWidth()) {
        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
            Text("Última recepción", style = MaterialTheme.typography.titleSmall)
            if (item == null) {
                Hint("Nada todavía. Pulsa «Enviar prueba» en otro dispositivo.")
            } else {
                Text(
                    "${formatTime(item.atMs, "HH:mm:ss")} · de ${item.from}${if (item.isTest) " · prueba" else ""}",
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
                Text(if (item.isImage) item.preview() else "«${item.preview(300)}»", style = MaterialTheme.typography.bodyMedium)
                item.image?.let { bytes ->
                    val thumbnail = remember(bytes) { decodeThumbnail(bytes) }
                    thumbnail?.let {
                        Image(
                            bitmap = it,
                            contentDescription = "Imagen recibida",
                            modifier = Modifier.padding(top = 8.dp).heightIn(max = 180.dp),
                        )
                    }
                }
            }
        }
    }
}

@Composable
private fun Banner(text: String, action: String, onClick: () -> Unit) {
    Card(
        Modifier.fillMaxWidth().padding(bottom = 8.dp),
        colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.tertiaryContainer),
    ) {
        Row(Modifier.padding(12.dp), verticalAlignment = Alignment.CenterVertically) {
            Text(text, Modifier.weight(1f), style = MaterialTheme.typography.bodyMedium)
            Spacer(Modifier.width(8.dp))
            Button(onClick = onClick) { Text(action) }
        }
    }
}

@Composable
private fun SectionTitle(text: String, modifier: Modifier = Modifier) =
    Text(text, modifier.padding(top = 8.dp), style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold)

@Composable
private fun Hint(text: String) = Text(text, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)

@Composable
private fun DiscoveredRow(d: DiscoveredDevice, onPair: () -> Unit) {
    Card(Modifier.fillMaxWidth()) {
        Row(Modifier.padding(12.dp), verticalAlignment = Alignment.CenterVertically) {
            Column(Modifier.weight(1f)) {
                Text(d.deviceName, style = MaterialTheme.typography.titleSmall)
                Text("${platformLabel(d.platform)} · ${d.host}", style = MaterialTheme.typography.bodySmall)
                if (!d.pairingOpen) Text("Abre su ventana de Klip para vincular", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.error)
            }
            Button(onClick = onPair, enabled = d.pairingOpen) { Text("Vincular") }
        }
    }
}

@Composable
private fun ManualPairing(onPair: (String, Int) -> Unit) {
    var open by remember { mutableStateOf(false) }
    TextButton(onClick = { open = true }) { Text("¿No aparece? Vincular por IP…") }
    if (!open) return
    var host by remember { mutableStateOf("") }
    var port by remember { mutableStateOf(Protocol.HTTP_PORT.toString()) }
    AlertDialog(
        onDismissRequest = { open = false },
        title = { Text("Vincular por IP") },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                Text("El otro dispositivo debe tener Klip abierto.", style = MaterialTheme.typography.bodySmall)
                OutlinedTextField(host, { host = it.trim() }, label = { Text("IP") }, singleLine = true,
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri))
                OutlinedTextField(port, { port = it.filter(Char::isDigit) }, label = { Text("Puerto") }, singleLine = true,
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number))
            }
        },
        confirmButton = {
            TextButton(enabled = host.isNotEmpty() && port.toIntOrNull() in 1..65535, onClick = {
                open = false
                onPair(host, port.toInt())
            }) { Text("Vincular") }
        },
        dismissButton = { TextButton(onClick = { open = false }) { Text("Cancelar") } },
    )
}

@Composable
private fun PeerRow(
    p: PeerView,
    onEnabled: (Boolean) -> Unit,
    onTest: () -> Unit,
    onPing: () -> Unit,
    onUnpair: () -> Unit,
    onExpel: () -> Unit,
) {
    var menu by remember { mutableStateOf(false) }
    var confirm by remember { mutableStateOf<String?>(null) }
    Card(Modifier.fillMaxWidth()) {
        Column(Modifier.padding(12.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Column(Modifier.weight(1f)) {
                    Text(p.name, style = MaterialTheme.typography.titleSmall)
                    Text("${platformLabel(p.platform)} · ${p.host}:${p.port}", style = MaterialTheme.typography.bodySmall)
                    Text("Última conexión: ${p.lastSeenMs?.let(::formatTime) ?: "nunca"}", style = MaterialTheme.typography.bodySmall)
                    p.lastStatus?.let { Text(it, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.primary) }
                }
                Switch(checked = p.enabled, onCheckedChange = onEnabled)
            }
            HorizontalDivider(Modifier.padding(vertical = 6.dp))
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                OutlinedButton(onClick = onTest) { Text("Enviar prueba") }
                OutlinedButton(onClick = onPing) { Text("Comprobar") }
                Box {
                    TextButton(onClick = { menu = true }) { Text("Más") }
                    DropdownMenu(expanded = menu, onDismissRequest = { menu = false }) {
                        DropdownMenuItem(text = { Text("Desvincular") }, onClick = { menu = false; confirm = "unpair" })
                        DropdownMenuItem(text = { Text("Expulsar del grupo") }, onClick = { menu = false; confirm = "expel" })
                    }
                }
            }
        }
    }
    confirm?.let { action ->
        AlertDialog(
            onDismissRequest = { confirm = null },
            title = { Text(if (action == "unpair") "Desvincular ${p.name}" else "Expulsar ${p.name}") },
            text = {
                Text(
                    if (action == "unpair") "Este dispositivo y ${p.name} dejarán de sincronizarse entre sí."
                    else "${p.name} se eliminará de todos los dispositivos del grupo."
                )
            },
            confirmButton = { TextButton(onClick = { confirm = null; if (action == "unpair") onUnpair() else onExpel() }) { Text("Aceptar") } },
            dismissButton = { TextButton(onClick = { confirm = null }) { Text("Cancelar") } },
        )
    }
}

@Composable
private fun Settings(refreshTick: Int) {
    val context = LocalContext.current
    val prefs = context.klip.prefs
    val node = context.klip.node
    var sensitive by remember(refreshTick) { mutableStateOf(node.sendSensitive) }
    var receiver by remember(refreshTick) { mutableStateOf(prefs.receiverEnabled) }
    var boot by remember(refreshTick) { mutableStateOf(prefs.startOnBoot) }
    var tile by remember(refreshTick) { mutableStateOf(Components.isEnabled(context, QuickTileService::class.java)) }
    var share by remember(refreshTick) { mutableStateOf(Components.isEnabled(context, ShareActivity::class.java)) }
    var select by remember(refreshTick) { mutableStateOf(Components.isEnabled(context, ProcessTextActivity::class.java)) }

    Card(Modifier.fillMaxWidth()) {
        Column(Modifier.padding(vertical = 4.dp)) {
            SettingRow("Receptor en segundo plano", "Recibe copias con la app cerrada (aviso permanente)", receiver) {
                receiver = it
                prefs.receiverEnabled = it
                if (it) ReceiverService.start(context) else ReceiverService.stop(context)
            }
            SettingRow("Arrancar al iniciar el dispositivo", null, boot) { boot = it; prefs.startOnBoot = it }
            SettingRow("Envío desde Ajustes rápidos", "Copia → baja la cortina → toca «Enviar portapapeles»", tile) {
                tile = it; Components.setEnabled(context, QuickTileService::class.java, it)
            }
            SettingRow("Envío desde el menú Compartir", null, share) {
                share = it; Components.setEnabled(context, ShareActivity::class.java, it)
            }
            SettingRow("Envío desde la selección de texto", "Selecciona texto → «Enviar con Klip»", select) {
                select = it; Components.setEnabled(context, ProcessTextActivity::class.java, it)
            }
            SettingRow("Enviar también contraseñas", "Texto que los gestores de contraseñas marcan como sensible (Ajustes rápidos)", sensitive) {
                sensitive = it; node.sendSensitive = it
            }
        }
    }
}

@Composable
private fun SettingRow(title: String, subtitle: String?, checked: Boolean, onChange: (Boolean) -> Unit) {
    Row(Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 8.dp), verticalAlignment = Alignment.CenterVertically) {
        Column(Modifier.weight(1f)) {
            Text(title, style = MaterialTheme.typography.bodyLarge)
            subtitle?.let { Text(it, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant) }
        }
        Switch(checked = checked, onCheckedChange = onChange)
    }
}

@Composable
private fun ProgressDialog(text: String) {
    AlertDialog(
        onDismissRequest = {},
        confirmButton = {},
        text = {
            Row(verticalAlignment = Alignment.CenterVertically) {
                CircularProgressIndicator()
                Spacer(Modifier.width(16.dp))
                Text(text)
            }
        },
    )
}

@Composable
private fun PinDialog(title: String, pin: String, text: String, onAccept: () -> Unit, onReject: () -> Unit) {
    AlertDialog(
        onDismissRequest = onReject,
        title = { Text(title) },
        text = {
            Column(horizontalAlignment = Alignment.CenterHorizontally, modifier = Modifier.fillMaxWidth()) {
                Text(
                    "${pin.take(3)} ${pin.drop(3)}",
                    fontSize = 40.sp,
                    fontFamily = FontFamily.Monospace,
                    fontWeight = FontWeight.Bold,
                    textAlign = TextAlign.Center,
                    modifier = Modifier.padding(vertical = 16.dp),
                )
                Text(text, textAlign = TextAlign.Center)
            }
        },
        confirmButton = { Button(onClick = onAccept) { Text("Coincide") } },
        dismissButton = { TextButton(onClick = onReject) { Text("No coincide") } },
    )
}

private fun platformLabel(p: String) = when (p) {
    "android" -> "Android"
    "windows" -> "Windows"
    "linux" -> "Linux"
    "devpeer" -> "DevPeer"
    else -> p
}

/** Downsampled preview: a received photo can be large. */
private fun decodeThumbnail(bytes: ByteArray): ImageBitmap? {
    val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
    BitmapFactory.decodeByteArray(bytes, 0, bytes.size, bounds)
    var sample = 1
    while (bounds.outWidth / sample > 1024 || bounds.outHeight / sample > 1024) sample *= 2
    return BitmapFactory.decodeByteArray(bytes, 0, bytes.size, BitmapFactory.Options().apply { inSampleSize = sample })?.asImageBitmap()
}

private fun formatTime(ms: Long, pattern: String = "dd/MM HH:mm") = SimpleDateFormat(pattern, Locale.getDefault()).format(Date(ms))
