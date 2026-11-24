# Plan de Acción: Klip — Sincronizador Local de Portapapeles (Android y Windows)
`com.n3k0chan.klip`

## 0. Resumen de Decisiones

| Tema | Decisión |
| :--- | :--- |
| Seguridad del transporte | HTTP en LAN con **firma HMAC-SHA256** de cada petición (timestamp + nonce antireplay) y **contenido cifrado con AES-256-GCM**. El secreto nunca viaja por la red. |
| Emparejamiento | Intercambio de claves **ECDH P-256** + **PIN de 6 dígitos** mostrado en ambas pantallas que el usuario confirma. |
| Modelo de secretos | **Un secreto por pareja** de dispositivos. |
| Topología | **Grupo automático en malla**: al vincular un nodo nuevo, se le presentan los demás miembros y quedan todos vinculados entre sí. |
| Distribución Android | **APK por sideload** (uso personal). Foreground Service de tipo **`specialUse`**. |
| Versión mínima Android | **Android 10 (API 29)**. `targetSdk` = última estable. |
| Servidor HTTP Android | **Ktor (motor CIO)**. |
| Stack Windows | **.NET 10 LTS + WinForms + Kestrel**. |
| Contenido sensible | Lo marcado como sensible por gestores de contraseñas **nunca se envía**. |
| Destino desconectado | **Se descarta** el envío y se informa del fallo. Sin colas ni reintentos. |
| Vías de captura Android | **Tile de Ajustes Rápidos**, **menú Compartir** (`ACTION_SEND`) y **menú de selección de texto** (`ACTION_PROCESS_TEXT`). |
| Descubrimiento | **Radar UDP** solo con la UI abierta (para emparejar) + **mDNS/NSD permanente** (para resolver cambios de IP). |
| Daemon Windows | **Interruptor único en el system tray** que activa/desactiva toda la sincronización (monitor + servidor). Estado persistente entre reinicios. |
| Pausa en Android | Solo desde `MainActivity` (interruptor del servicio receptor). |
| Tamaño máximo de texto | **Sin límite a nivel de aplicación** (ver limitación de plataforma en Android, §3.6). |

### Fuera de alcance
Archivos, historial del portapapeles, sincronización fuera de la LAN (Internet / nube), y otras plataformas (macOS, iOS).

---

## 1. Objetivo y Arquitectura General

Construir un ecosistema personal y 100 % local (sin nube de terceros, sin root, sin ADB/Shizuku y sin servicios de accesibilidad que drenen batería) para compartir contenido del portapapeles (texto y enlaces) entre dispositivos propios: teléfono Android, tablet Android y PC con Windows.

### Principios de Diseño y Limitaciones del Sistema

* **Asimetría de Android 10+:** Android permite **escribir** en el portapapeles (`ClipboardManager.setPrimaryClip`) desde segundo plano, pero bloquea **leer** (`getPrimaryClip`) a cualquier app que no esté en primer plano con foco o que no sea el teclado activo.
* **Captura explícita en Android (tres vías configurables):**
  1. **Tile de Ajustes Rápidos:** Copiar -> bajar panel -> tocar el Tile. Lanza una actividad transparente que lee el portapapeles, entrega el envío a un ámbito de aplicación y se cierra.
  2. **Menú Compartir (`ACTION_SEND`):** Seleccionar texto/enlace -> Compartir -> Klip. No lee el portapapeles.
  3. **Menú de selección de texto (`ACTION_PROCESS_TEXT`):** Seleccionar texto -> "Klip" en el menú flotante. Un toque menos que Compartir.
* **Automatización total desde Windows:** El cliente de Windows escucha `WM_CLIPBOARDUPDATE` y envía automáticamente cualquier texto nuevo al grupo.
* **Red local (LAN):** HTTP sobre Wi-Fi local, con mensajes firmados y cifrados. Los equipos se localizan por radar UDP (emparejamiento) y mDNS/NSD (uso diario).

---

## 2. Protocolo de Comunicación

Se redactará una **especificación compartida** (`docs/protocol.md`) con **vectores de prueba** (normalización, hash, HKDF, HMAC, AES-GCM, PIN) que ambas implementaciones (Kotlin y C#) deben superar en sus tests unitarios.

### 2.1. Puertos y Nombres

| Uso | Puerto / Nombre |
| :--- | :--- |
| HTTP (portapapeles, emparejamiento, control) | TCP `8765` |
| Radar de descubrimiento | UDP `8766` |
| Servicio mDNS/NSD | `_klip._tcp.local.`, instancia = `deviceId` |

### 2.2. Criptografía

* **Emparejamiento:** ECDH sobre **P-256** (soportado de forma nativa en Android API 29+ y en .NET sin librerías externas).
* **Derivación:** `pairSecret = HKDF-SHA256(sharedECDH, salt = nonceA || nonceB, info = "klip-pair-v1")` -> de él se derivan con HKDF:
  * `macKey` (32 bytes, info `"klip-mac"`) -> firma HMAC-SHA256 de peticiones.
  * `encKey` (32 bytes, info `"klip-enc"`) -> cifrado AES-256-GCM del contenido.
* **PIN de verificación:** `PIN = uint32(SHA-256(pubKey_iniciador || pubKey_receptor || nonceA || nonceB)[0..3]) mod 1 000 000`, mostrado con 6 dígitos en ambas pantallas.
* **Almacenamiento de secretos:**
  * Android: claves de pareja cifradas con una clave AES del **Android Keystore** y guardadas en DataStore (no usar `EncryptedSharedPreferences`, está obsoleto).
  * Windows: `%APPDATA%\Klip\config.json` con los secretos protegidos por **DPAPI** (`ProtectedData`, ámbito `CurrentUser`).

### 2.3. Autenticación de Peticiones (todas salvo `/pair/request`)

Cabeceras:

```
X-Klip-Device:    <deviceId del emisor>
X-Klip-Timestamp: <epoch ms>
X-Klip-Nonce:     <16 bytes aleatorios, base64url>
X-Klip-Signature: base64(HMAC-SHA256(macKey, METHOD \n PATH \n TIMESTAMP \n NONCE \n hex(SHA-256(body))))
```

El receptor:
1. Busca la pareja por `X-Klip-Device`; si no existe -> `403`.
2. Rechaza si `|ahora - timestamp| > 30 s` -> `401` (los relojes de los dispositivos se asumen sincronizados por NTP).
3. Rechaza si el nonce ya se vio en los últimos 60 s (caché en memoria) -> `401`.
4. Verifica la firma en tiempo constante -> `401` si no coincide.

### 2.4. Formato del Mensaje de Portapapeles

Sobre (en claro, firmado vía cabeceras):

```json
{
  "protocolVersion": 1,
  "messageId": "uuid-v4",
  "originDeviceId": "id-del-emisor",
  "timestamp": 1763930000000,
  "iv": "base64 (12 bytes)",
  "ciphertext": "base64 (AES-256-GCM, incluye tag)"
}
```

Contenido descifrado (JSON dentro de `ciphertext`; AAD = `messageId || originDeviceId`):

```json
{
  "type": "text",
  "content": "Texto o enlace copiado",
  "contentHash": "sha256-hex del contenido normalizado",
  "originDeviceName": "Pixel / Tablet / PC-Windows",
  "isTest": false
}
```

El hash va **dentro** del cifrado para no filtrar huellas del contenido en la red.

### 2.5. Normalización de Contenido

Antes de calcular `contentHash` y comparar, ambos lados:
1. Convierten saltos de línea `\r\n` y `\r` a `\n`.
2. Aplican normalización Unicode **NFC**.
3. Codifican en UTF-8.

No se recortan espacios para el hash (solo para la comprobación de "vacío"). Al escribir en el portapapeles de Windows, `\n` se convierte a `\r\n`; la normalización garantiza que el hash siga coincidiendo.

### 2.6. Endpoints HTTP

| Método y ruta | Firma | Uso | Respuestas |
| :--- | :--- | :--- | :--- |
| `POST /pair/request` | No (solo con radar activo y límite de 5 peticiones/min por IP) | Inicio del emparejamiento ECDH | `200`, `403` (radar inactivo), `429` |
| `POST /pair/confirm` | Sí (con la clave recién derivada) | Confirmación del PIN por el iniciador | `200`, `401`, `408` (expirado) |
| `POST /clipboard` | Sí | Entrega de contenido | `200`, `400`, `401`, `403`, `409` (duplicado) |
| `GET /ping` | Sí | Estado de conexión ("última conexión" en la UI y botón de prueba ligero) | `200 {deviceId, deviceName, platform}` |
| `POST /group/introduce` | Sí | Un miembro presenta a otro nodo nuevo del grupo (ver §2.8) | `200`, `401`, `403` |
| `POST /unpair` | Sí | Revocar la pareja con el emisor | `200` |
| `POST /group/remove` | Sí | Expulsar a un nodo del grupo (se propaga a todos los miembros) | `200` |

Sin límite de tamaño de cuerpo: Kestrel con `MaxRequestBodySize = null`; en Ktor sin límite de recepción. Timeouts: conexión **2 s**; total **10 s + 1 s por MB** de cuerpo.

### 2.7. Reglas Antiduplicados y Antibucle

Cada dispositivo guarda (en memoria, con copia persistente):
1. `lastSentHash`: hash del último contenido enviado por este dispositivo.
2. `lastReceivedHash`: hash del último contenido recibido y escrito en el portapapeles local.
3. `processedMessageIds`: lista circular de los últimos 50 `messageId`.

**Antes de enviar:**
* Contenido vacío o solo espacios -> **Abortar**.
* Contenido marcado como **sensible** (ver §3.5 y §4.4) -> **Abortar** sin aviso.
* `currentHash == lastSentHash` -> **Abortar** (salvo `isTest`).
* `currentHash == lastReceivedHash` -> **Abortar** (salvo `isTest`).
* Si la sincronización está desactivada (interruptor del tray en Windows) -> **Abortar**.

**Al recibir:**
* Autenticación según §2.3.
* `originDeviceId == localDeviceId` o `messageId` ya procesado -> `409`, ignorar.
* Descifrar y verificar que `contentHash` coincide con el hash recalculado -> si no, `400`.
* Guardar `lastReceivedHash` **antes** de escribir en el portapapeles.
* Escribir en el portapapeles nativo.
* Si `isTest`, mostrar notificación: *"Prueba recibida correctamente desde [originDeviceName]"*.
* Actualizar la **última IP conocida** del emisor con la IP de origen del socket (actualización pasiva).

**Sin reenvío:** un nodo **nunca** reenvía lo recibido. Gracias a la malla del grupo (§2.8), el emisor original entrega directamente a todos los miembros activos.

**Destino desconectado:** si un envío falla (timeout o conexión rechazada), se descarta y se informa (Toast en Android, globo en Windows si fue envío manual; silencioso en el monitor automático de Windows, solo se refleja en el estado del dispositivo).

### 2.8. Grupo Automático (Malla)

1. El nodo nuevo **N** se empareja con un miembro **A** mediante PIN (§5.2).
2. Para cada otro miembro **B** del grupo de A, A genera un secreto aleatorio de 256 bits `secret(N,B)` y envía `POST /group/introduce` a **N** y a **B** (cada uno por su canal cifrado con A) con la identidad del otro (`deviceId`, nombre, plataforma, puerto, última IP) y el secreto común.
3. N y B registran la nueva pareja sin pedir PIN (la confianza se delega en A, ya verificado).
4. Si B está desconectado, A guarda la presentación pendiente y la reintenta cuando vuelva a ver a B (por mDNS o al recibir algo de B).
5. **Expulsar** un nodo (`/group/remove`) lo elimina de todos los miembros; **desvincular** (`/unpair`) solo rompe esa pareja concreta.

---

## 3. Aplicación Android (Kotlin)

### 3.1. Componentes

1. **`KlipApplication`:** Expone un `CoroutineScope` de aplicación (`SupervisorJob() + Dispatchers.IO`) para los envíos. Las actividades efímeras entregan el trabajo a este ámbito para que **no se cancele al llamar a `finish()`**.

2. **`MainActivity` (configuración y emparejamiento):**
   * **Identidad local:** nombre editable, `deviceId` (UUID generado en la instalación), IP local actual y puerto.
   * **Radar:** con la actividad en primer plano (`onResume`/`onPause`) se activa `LocalDiscoveryService` y se lista *"Dispositivos detectados en la red"*.
   * **Emparejamiento:** botón **"Vincular"** -> flujo con PIN (§5.2). Alta manual (nombre, IP, puerto) como alternativa: también termina en verificación por PIN.
   * **Grupo:** lista de miembros con interruptor activo/inactivo (a quién se envía), estado de última conexión (vía `GET /ping`), y acciones *Desvincular* / *Expulsar del grupo*.
   * **"Enviar prueba"** por dispositivo y global, con resultado inmediato (*Enviado (200 OK)*, *Error de conexión*, *Timeout*, *Firma rechazada*).
   * **Ajustes:**
     * Interruptor: *Activar envío desde Tile de Ajustes Rápidos*.
     * Interruptor: *Activar envío desde menú Compartir*.
     * Interruptor: *Activar envío desde menú de selección de texto*.
     * Interruptor: *Activar servicio receptor en segundo plano* (es la pausa de Android: lo detiene y lo arranca).
     * Interruptor: *Arrancar al iniciar el dispositivo*.
     * Las vías de envío se activan/desactivan con `PackageManager.setComponentEnabledSetting` (desaparecen del sistema cuando están desactivadas).
   * **Asistente inicial:** pedir `POST_NOTIFICATIONS` (API 33+), solicitar exención de optimización de batería y mostrar instrucciones específicas del fabricante (Samsung: *"Apps que nunca se suspenden"*; Xiaomi: *Inicio automático*; etc., ver dontkillmyapp.com).

3. **`ClipboardReceiverService` (Foreground Service, tipo `specialUse`):**
   * Notificación persistente de baja prioridad (`IMPORTANCE_LOW`).
   * Arranca Ktor CIO en `0.0.0.0:8765` con los endpoints de §2.6.
   * Registra el servicio NSD `_klip._tcp` mientras está activo (necesario para que los demás resuelvan su IP tras cambios DHCP).
   * Escribe en el portapapeles con `setPrimaryClip(ClipData.newPlainText("Klip", content))` en el hilo principal.
   * `ServiceCompat.startForeground(...)` con tipo `FOREGROUND_SERVICE_TYPE_SPECIAL_USE` solo en API 34+; en API 29–33 sin tipo especial.
   * Declarar en el manifiesto la propiedad `android.app.PROPERTY_SPECIAL_USE_FGS_SUBTYPE` con la justificación ("servidor LAN de sincronización de portapapeles").

4. **`QuickCopyTileService` (`TileService`):**
   * `onClick()` lanza `TransparentCaptureActivity`:
     * API 34+: `startActivityAndCollapse(PendingIntent)`.
     * API 29–33: `startActivityAndCollapse(Intent)` con `FLAG_ACTIVITY_NEW_TASK`.

5. **`TransparentCaptureActivity`:**
   * Tema translúcido, `excludeFromRecents="true"`, `noHistory="true"`, sin animaciones.
   * En `onWindowFocusChanged(hasFocus = true)`:
     1. Lee `getPrimaryClip()` y comprueba la marca de sensible (§3.5).
     2. Extrae el texto (`coerceToText`).
     3. Aplica las reglas de §2.7.
     4. Entrega el envío al ámbito de `KlipApplication`.
     5. Muestra un `Toast` breve (*"Portapapeles enviado"* / *"Sin cambios nuevos"* / *"Contenido sensible, no enviado"*) y llama a `finish()`.
   * Nota de UX: en Android 12+ el sistema mostrará su propio aviso *"Klip ha pegado desde el portapapeles"*; es inevitable.

6. **`ShareReceiverActivity` (`ACTION_SEND`, `text/plain`):**
   * Translúcida. Extrae `EXTRA_TEXT` (si existe `EXTRA_SUBJECT` y el texto es solo una URL, se envía la URL), aplica §2.7, entrega el envío, confirma brevemente y `finish()`.

7. **`ProcessTextActivity` (`ACTION_PROCESS_TEXT`, `text/plain`):**
   * Translúcida. Extrae `EXTRA_PROCESS_TEXT`, aplica §2.7, entrega el envío y `finish()` sin devolver resultado (no modifica el texto seleccionado).

8. **`LocalDiscoveryService`:** radar UDP (§5) activo solo mientras `MainActivity` está visible.

9. **`BootReceiver`:** si el usuario lo habilitó, arranca `ClipboardReceiverService` tras `BOOT_COMPLETED` (permitido para `specialUse`).

### 3.2. Configuración de Red

* **Tráfico HTTP sin TLS:** Android 9+ lo bloquea por defecto. Añadir `res/xml/network_security_config.xml` con `<base-config cleartextTrafficPermitted="true">` y referenciarlo en el manifiesto. Es aceptable porque el contenido va cifrado y firmado a nivel de aplicación.
* **Multicast/broadcast:** adquirir `WifiManager.MulticastLock` mientras el radar UDP está activo; liberarlo en `onPause`.
* **Permiso de red local (Android 17):** verificar el estado del nuevo permiso de acceso a la red local (`ACCESS_LOCAL_NETWORK`) para apps cuyo `targetSdk` sea Android 17; si aplica, solicitarlo en el asistente inicial.
* **Wi-Fi en reposo:** si el test de reposo (§7) falla, valorar un `WifiLock` (`WIFI_MODE_FULL_LOW_LATENCY`) mientras el servicio esté activo, como opción desactivada por defecto.

### 3.3. Permisos en `AndroidManifest.xml`

* `INTERNET`, `ACCESS_NETWORK_STATE`, `ACCESS_WIFI_STATE`.
* `CHANGE_WIFI_MULTICAST_STATE` (radar UDP).
* `FOREGROUND_SERVICE`, `FOREGROUND_SERVICE_SPECIAL_USE`.
* `POST_NOTIFICATIONS` (petición en tiempo de ejecución en API 33+).
* `RECEIVE_BOOT_COMPLETED`.
* `REQUEST_IGNORE_BATTERY_OPTIMIZATIONS`.
* (Si aplica en Android 17) permiso de acceso a la red local.

### 3.4. Persistencia

DataStore para configuración, grupo y estado antibucle; secretos de pareja cifrados con Android Keystore (§2.2).

### 3.5. Contenido Sensible en Android

* Al leer el portapapeles (vía Tile), si `clip.description.extras` contiene `ClipDescription.EXTRA_IS_SENSITIVE` (clave `"android.content.extra.IS_SENSITIVE"`, comprobada como cadena para funcionar también en API < 33) con valor `true`, **no se envía**.
* Las vías Compartir y selección de texto son acciones explícitas del usuario y no traen esa marca; se envían.

### 3.6. Limitación de Tamaño en Android

Aunque la aplicación no impone límite, el portapapeles de Android pasa por Binder (límite práctico inferior a ~1 MB por transacción). Al recibir textos muy grandes, `setPrimaryClip` puede fallar con `TransactionTooLargeException`: se captura, se responde `200` (el mensaje llegó) y se muestra una notificación *"Texto demasiado grande para el portapapeles de Android"*. Lo mismo al leer en el Tile: si falla, se informa con un Toast.

---

## 4. Aplicación Windows (.NET 10 + WinForms + Kestrel)

### 4.1. Componentes

1. **`TrayHost` (`NotifyIcon`) — control del daemon:**
   * Menú contextual:
     * ☑ **Sincronización activada** — interruptor único. Al desactivarlo:
       * se deja de procesar `WM_CLIPBOARDUPDATE` (`RemoveClipboardFormatListener`),
       * se detiene Kestrel (puerto 8765 cerrado),
       * se retira el anuncio mDNS.
       Al reactivarlo se restauran los tres.
     * Abrir configuración…
     * Enviar prueba (a todos)
     * ☑ Iniciar con Windows
     * Salir
   * **Icono con dos estados** (activo / en pausa en gris) y tooltip *"Klip — Sincronización activada/pausada"*.
   * El estado del interruptor **se guarda** en la configuración y se respeta al reiniciar.
   * Doble clic en el icono abre `ConfigWindow`.

2. **`ConfigWindow`:** equivalente a `MainActivity`. Muestra la IP local, el puerto, la lista del grupo con interruptores por dispositivo, el estado de última conexión, "Enviar prueba" por dispositivo, el radar de dispositivos detectados con "Vincular" y el diálogo del PIN. El radar UDP se activa solo mientras la ventana está visible.

3. **`ClipboardMonitorService`:**
   * Ventana oculta (`NativeWindow`) registrada con `AddClipboardFormatListener`.
   * **Antirrebote:** al llegar `WM_CLIPBOARDUPDATE`, esperar ~150 ms y comparar `GetClipboardSequenceNumber()` para procesar una sola vez cada copia.
   * **Ignorar escrituras propias:** si `GetClipboardOwner()` es la ventana de Klip, ignorar (además de la regla `lastReceivedHash`).
   * **Reintentos de acceso:** si el portapapeles está bloqueado por otra app (`CLIPBRD_E_CANT_OPEN`), reintentar hasta 5 veces cada 50 ms.
   * Lee solo texto (`CF_UNICODETEXT`), aplica §2.7 y envía al grupo.

4. **`HttpReceiverService` (Kestrel):**
   * Escucha en `0.0.0.0:8765` (Kestrel no requiere reserva `netsh http add urlacl`, a diferencia de `HttpListener`).
   * `MaxRequestBodySize = null` (sin límite).
   * Al recibir: aplica §2.7, actualiza `lastReceivedHash` y escribe con `Clipboard.SetText` **en el hilo STA de la UI** (vía `SynchronizationContext`), con los mismos reintentos.
   * Si `isTest`, notificación mediante `NotifyIcon.ShowBalloonTip` (en Windows 11 se muestra como notificación del sistema, sin necesidad de registrar un AUMID).

5. **`LocalDiscoveryWorker`:** radar UDP (§5), activo solo con `ConfigWindow` visible.

6. **`MdnsService`:** registro y resolución de `_klip._tcp` con la API nativa de Windows `DnsServiceRegister` / `DnsServiceBrowse` / `DnsServiceResolve` (Windows 10 1809+) vía P/Invoke, para no competir por el puerto 5353 con el servicio mDNS del propio Windows.

### 4.2. Aspectos de Sistema

* **Instancia única:** `Mutex` con nombre global; una segunda ejecución trae al frente la instancia existente.
* **Inicio con Windows:** entrada en `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` (sin permisos de administrador).
* **Selección de interfaz de red:** usar las interfaces activas con puerta de enlace predeterminada y excluir adaptadores virtuales (Hyper-V, WSL, VPN, VirtualBox). El broadcast UDP se envía a la dirección de broadcast de cada subred válida.
* **Firewall:** reglas de entrada solo para el perfil **Privado**:
  * TCP `8765` (HTTP).
  * UDP `8766` (radar).
  * La red Wi-Fi debe estar marcada como **Privada** en Windows; la UI avisa si el perfil activo es Público.
  * Creación: en la primera ejecución, si faltan las reglas, se ofrece crearlas lanzando un proceso elevado (UAC) con `netsh advfirewall firewall add rule ...`.
* **Equipos corporativos:** si el PC está gestionado por la empresa, las políticas de grupo pueden ignorar las reglas de firewall locales; la UI debe detectar que el puerto no es accesible y explicarlo.

### 4.3. Persistencia

`%APPDATA%\Klip\config.json` (identidad, grupo, ajustes, estado del interruptor del tray, estado antibucle). Secretos protegidos con DPAPI.

### 4.4. Contenido Sensible en Windows

No se envía el contenido si el portapapeles contiene alguno de estos formatos registrados (los usan KeePass, 1Password, Bitwarden, etc.):
* `ExcludeClipboardContentFromMonitorProcessing` (presente).
* `CanIncludeInClipboardHistory` con valor `0`.
* `CanUploadToCloudClipboard` con valor `0`.
* `Clipboard Viewer Ignore` (presente).

---

## 5. Descubrimiento y Emparejamiento (Zero-Config LAN)

### 5.1. Radar UDP (solo con la UI abierta)

* Puerto UDP `8766`, separado del HTTP `8765`.
* Se activa con `MainActivity` / `ConfigWindow` visibles y se apaga por completo al ocultarlas.
* Cada nodo emite cada 1,5 s un beacon a `255.255.255.255:8766` y al broadcast de su subred:

```json
{ "klip": 1, "deviceId": "...", "deviceName": "...", "platform": "android|windows", "httpPort": 8765, "groupId": "hash-del-grupo-o-null" }
```

* El receptor toma la IP de origen del socket y lista el equipo en *"Dispositivos detectados en la red"*. Si el `deviceId` ya es de su grupo, actualiza su última IP conocida y no lo muestra como nuevo.

### 5.2. Flujo de Emparejamiento con PIN

1. El usuario pulsa **"Vincular"** en el nodo **I** (iniciador) junto al nodo detectado **R**.
2. I envía `POST /pair/request` a R con `{deviceId, deviceName, platform, httpPort, pubKey_I, nonce_I}`.
3. R (que debe tener su UI abierta con el radar activo; si no, responde `403`) genera su par ECDH y responde `{deviceId, deviceName, platform, httpPort, pubKey_R, nonce_R}`.
4. Ambos calculan el secreto ECDH, derivan las claves (§2.2) y muestran el **mismo PIN de 6 dígitos**: *"¿Vincular con [Nombre]? Comprueba que el código es 482 913"*.
5. El usuario confirma en **ambos** dispositivos:
   * R guarda la confirmación local.
   * I envía `POST /pair/confirm` firmado con la nueva `macKey`.
   * R solo acepta `/pair/confirm` si su usuario también confirmó; la pareja queda creada cuando ambos lados han confirmado.
6. La solicitud expira a los 60 s sin confirmación. Si el usuario rechaza en cualquiera de los dos lados, se descarta el material criptográfico.
7. Tras vincular, se ejecuta la **presentación al grupo** (§2.8).

### 5.3. Cambios de IP (DHCP)

1. **Actualización pasiva:** cualquier petición firmada válida o beacon de un `deviceId` conocido actualiza su última IP.
2. **mDNS/NSD:** cada receptor activo anuncia `_klip._tcp` con instancia `deviceId`. Si un envío a la última IP conocida falla por conexión, el emisor resuelve por mDNS (timeout 1,5 s) y reintenta **una sola vez** con la nueva IP.
3. **Redes problemáticas:** en redes de invitados o con aislamiento de clientes (AP isolation), ni el broadcast ni mDNS funcionan; la UI lo indica cuando no se detecta ningún dispositivo y sugiere el alta manual.

---

## 6. Hoja de Ruta por Fases

| Fase | Entregable | Objetivo y Verificación |
| :--- | :--- | :--- |
| **Fase 1** | **Especificación del protocolo y núcleo compartido** | `docs/protocol.md` + vectores de prueba. Librería núcleo en Kotlin (módulo JVM) y en C# (normalización, hash, HKDF, HMAC, AES-GCM, ECDH, PIN, sobres). Tests unitarios cruzados con los mismos vectores. |
| **Fase 2** | **Windows: daemon base** | Tray con interruptor de sincronización persistente, Kestrel receptor, monitor del portapapeles con antirrebote y antibucle, filtro de contenido sensible, `config.json` + DPAPI. Pareja configurada a mano (modo desarrollo). Verificación con un script de pruebas (o colección Postman con firma en pre-request) que firme y cifre peticiones. |
| **Fase 3** | **Android: base** | `MainActivity`, `ClipboardReceiverService` (`specialUse`), Ktor, configuración de cleartext, botón "Enviar prueba", asistente de permisos y batería. Prueba bidireccional con Windows usando pareja manual. |
| **Fase 4** | **Descubrimiento, emparejamiento y grupo** | Radar UDP (con `MulticastLock`), flujo ECDH + PIN, `/group/introduce`, `/unpair`, `/group/remove`, mDNS/NSD en ambas plataformas, actualización pasiva de IP, `/ping` y estado de conexión. |
| **Fase 5** | **Captura rápida en Android** | `QuickCopyTileService`, `TransparentCaptureActivity`, `ShareReceiverActivity`, `ProcessTextActivity`, interruptores con `setComponentEnabledSetting`, filtro de sensibles y manejo de textos grandes. |
| **Fase 6** | **Empaquetado y pulido** | APK firmado (release) para sideload; ejecutable de Windows autocontenido en un solo archivo (`dotnet publish -r win-x64 --self-contained -p:PublishSingleFile=true`); creación de reglas de firewall; inicio con Windows; registro de logs rotativo en ambas plataformas; ronda completa de QA. |

---

## 7. Lista de Verificación de Pruebas Manuales (QA)

**Emparejamiento y grupo**
1. Abrir la UI en móvil, tablet y PC -> se detectan sin escribir IPs.
2. Vincular móvil con PC -> el PIN coincide en ambas pantallas -> confirmar en los dos.
3. Vincular la tablet con el móvil -> la tablet queda vinculada también con el PC automáticamente (grupo).
4. Rechazar el PIN en un lado -> no se crea la pareja.
5. Expulsar la tablet del grupo -> desaparece en móvil y PC.

**Funcionamiento**
6. "Enviar prueba" desde cada dispositivo -> llega la notificación de prueba a cada destino.
7. Antibucle: copiar en Windows -> llega al móvil y la tablet -> ninguno lo reenvía.
8. Tile: copiar un enlace en Chrome Android -> Tile -> cierre instantáneo -> `Ctrl + V` en Windows y pegado en la tablet.
9. Compartir: desde WhatsApp o el navegador -> llega a Windows y la tablet.
10. Selección de texto: seleccionar texto -> "Klip" -> llega a todos.
11. Unicode y saltos de línea: texto con emojis, acentos y varias líneas -> llega idéntico y no se reenvía en bucle.

**Daemon de Windows**
12. Desactivar la sincronización en el tray -> copiar en Windows no envía nada; enviar desde Android falla con conexión rechazada; el icono aparece en gris.
13. Reiniciar Windows con la sincronización desactivada -> arranca desactivada. Reactivar -> todo vuelve a funcionar.
14. Lanzar Klip dos veces -> solo una instancia.

**Seguridad**
15. Copiar una contraseña desde KeePass / Bitwarden en Windows -> **no** se envía.
16. Copiar una contraseña desde un gestor en Android y pulsar el Tile -> **no** se envía.
17. Enviar una petición con firma incorrecta, timestamp antiguo o nonce repetido -> `401`.
18. Capturar tráfico con Wireshark -> el contenido no aparece en claro.

**Robustez**
19. Reposo: bloquear el móvil 15 min -> copiar en Windows -> desbloquear y pegar -> el texto está.
20. Reiniciar el móvil con "arrancar al iniciar" activo -> el receptor vuelve a funcionar sin abrir la app.
21. Cambio de IP: renovar DHCP en el PC (o reiniciar el router) -> el siguiente envío llega (mDNS / actualización pasiva).
22. Destino apagado -> el envío falla rápido con aviso y no bloquea al emisor.
23. Texto muy grande (5 MB) de Windows a Android -> Windows lo envía; Android muestra el aviso de "demasiado grande" sin bloquearse.
24. Red con perfil Público en Windows -> la UI avisa del problema de firewall.
