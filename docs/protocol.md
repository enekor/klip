# Especificación del Protocolo Klip — v1

Documento normativo. Las implementaciones de Kotlin (`core-kotlin`) y C# (`core-dotnet`) deben comportarse exactamente como aquí se describe y superar los vectores de prueba de [`protocol/test-vectors.json`](../protocol/test-vectors.json), generados por una tercera implementación de referencia en Python ([`tools/gen_test_vectors.py`](../tools/gen_test_vectors.py)).

Las decisiones de diseño y su justificación están en [`plan.md`](plan.md).

## 1. Convenciones

* **Texto:** siempre UTF-8, sin BOM.
* **Binario en JSON y cabeceras:** Base64 estándar (RFC 4648 §4) **con** relleno `=`.
* **Hex:** minúsculas.
* **Tiempos:** milisegundos desde la época Unix (UTC), como entero de 64 bits.
* `||` indica concatenación de bytes.
* `SHA256`, `HMAC` = HMAC-SHA256, `HKDF` = HKDF-SHA256 (RFC 5869).

## 2. Constantes

| Nombre | Valor |
| :--- | :--- |
| `PROTOCOL_VERSION` | `1` |
| `HTTP_PORT` | `8765` (TCP) |
| `DISCOVERY_PORT` | `8766` (UDP) |
| `MDNS_SERVICE_TYPE` | `_klip._tcp` |
| `TIMESTAMP_WINDOW_MS` | `30000` |
| `NONCE_TTL_MS` | `60000` |
| `PROCESSED_IDS_CAPACITY` | `50` |
| `PAIRING_NONCE_BYTES` | `32` |
| `REQUEST_NONCE_BYTES` | `16` |
| `AES_GCM_IV_BYTES` | `12` |
| `AES_GCM_TAG_BYTES` | `16` |
| `PAIRING_TIMEOUT_MS` | `60000` |
| `PAIR_REQUESTS_PER_MINUTE` | `5` (por IP de origen) |
| `BEACON_INTERVAL_MS` | `1500` |
| `DISCOVERY_EXPIRY_MS` | `5000` |
| `INTRODUCTION_EXPIRY_MS` | `604800000` (7 días) |

## 3. Normalización de Contenido

```
normalize(s):
    s = replace(s, "\r\n", "\n")
    s = replace(s, "\r",   "\n")
    s = NFC(s)
    return s

contentHash(s) = hex(SHA256(UTF8(normalize(s))))      # texto
imageHash(b)   = hex(SHA256(b))                         # imagen: bytes exactos, sin recodificar

isBlank(s) = s está vacía o todos sus caracteres son espacio en blanco Unicode
```

* No se recortan espacios para el hash.
* Al escribir en el portapapeles de Windows, el receptor convierte `\n` en `\r\n`. Como el hash se calcula sobre el texto normalizado, sigue coincidiendo.

## 4. Claves

### 4.1. Pares de Claves de Emparejamiento

* Curva **NIST P-256** (`secp256r1`).
* Clave pública en formato SEC1 **sin comprimir**: `0x04 || X(32 bytes) || Y(32 bytes)` = 65 bytes.
* El receptor de una clave pública **debe** comprobar que tiene 65 bytes, empieza por `0x04` y que el punto está en la curva. Si no, rechaza el emparejamiento.
* Los pares de claves son **efímeros**: uno nuevo por cada intento de emparejamiento.

### 4.2. Secreto de Pareja

Emparejamiento por PIN (I = iniciador, R = receptor):

```
shared     = ECDH(priv_I, pub_R) = ECDH(priv_R, pub_I)    # coordenada X, 32 bytes
pairSecret = HKDF(ikm = shared, salt = nonce_I || nonce_R, info = "klip-pair-v1", L = 32)
```

Presentación en grupo (`/group/introduce`): `pairSecret` = 32 bytes aleatorios generados por el presentador.

### 4.3. Claves de Trabajo

A partir de `pairSecret`, sea cual sea su origen:

```
ZERO_SALT = 32 bytes a 0x00
macKey = HKDF(ikm = pairSecret, salt = ZERO_SALT, info = "klip-mac", L = 32)
encKey = HKDF(ikm = pairSecret, salt = ZERO_SALT, info = "klip-enc", L = 32)
```

Se persiste solo `pairSecret` (protegido por Keystore / DPAPI); `macKey` y `encKey` se derivan al cargar.

### 4.4. PIN de Verificación

```
h   = SHA256(pub_I || pub_R || nonce_I || nonce_R)
n   = entero sin signo big-endian de h[0..3]
PIN = n mod 1000000, con ceros a la izquierda hasta 6 dígitos
```

Para mostrarlo se recomienda agruparlo como `482 913`.

## 5. Firma de Peticiones

Todas las peticiones HTTP salvo `POST /pair/request` llevan:

| Cabecera | Contenido |
| :--- | :--- |
| `X-Klip-Device` | `deviceId` del emisor |
| `X-Klip-Timestamp` | tiempo en ms (decimal) |
| `X-Klip-Nonce` | Base64 de 16 bytes aleatorios |
| `X-Klip-Signature` | `Base64(HMAC(macKey, UTF8(canonical)))` |

```
canonical = METHOD + "\n" + PATH + "\n" + TIMESTAMP + "\n" + NONCE + "\n" + hex(SHA256(body))
```

* `METHOD` en mayúsculas (`POST`, `GET`).
* `PATH` es la ruta sin esquema, host ni query (`/clipboard`).
* `TIMESTAMP` y `NONCE`: exactamente las cadenas de las cabeceras.
* Cuerpo vacío: `hex(SHA256(""))` = `e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855`.

### 5.1. Verificación (en este orden)

1. Faltan cabeceras o tienen formato inválido -> `MALFORMED` (`400`).
2. `X-Klip-Device` no es una pareja conocida -> `UNKNOWN_DEVICE` (`403`).
3. `|now - timestamp| > TIMESTAMP_WINDOW_MS` -> `EXPIRED` (`401`).
4. La firma no coincide (comparación en tiempo constante) -> `BAD_SIGNATURE` (`401`).
5. El par `(deviceId, nonce)` ya se vio en los últimos `NONCE_TTL_MS` -> `REPLAY` (`401`).
6. En otro caso -> `OK`; se registra el nonce.

El nonce solo se registra tras validar la firma, para que un atacante no pueda llenar la caché.

## 6. Sobre de Portapapeles (`POST /clipboard`)

### 6.1. Sobre (cuerpo HTTP, JSON)

```json
{
  "protocolVersion": 1,
  "messageId": "uuid-v4",
  "originDeviceId": "id-del-emisor",
  "timestamp": 1763930000000,
  "iv": "base64(12 bytes)",
  "ciphertext": "base64(ct || tag)"
}
```

### 6.2. Carga Cifrada

```json
{
  "type": "text",
  "content": "Texto o enlace",
  "contentHash": "hex sha256 del contenido normalizado",
  "originDeviceName": "Pixel",
  "isTest": false
}
```

Imagen (`type = "image"`):

```json
{
  "type": "image",
  "content": "",
  "contentHash": "hex sha256 de los bytes de la imagen",
  "originDeviceName": "PC",
  "isTest": false,
  "mimeType": "image/png",
  "data": "base64(bytes de la imagen)"
}
```

* `mimeType` es `image/png` o `image/jpeg`. Otros formatos se convierten a PNG antes de enviar.
* Los bytes viajan sin recodificar: el receptor los coloca tal cual en el portapapeles (en Windows, además, como mapa de bits).
* Las mismas reglas antibucle de §7 se aplican con `imageHash`.

```
plaintext  = UTF8(JSON(carga))
aad        = UTF8(messageId + "\n" + originDeviceId)
ciphertext = AES-256-GCM(encKey, iv, aad, plaintext)   # ct || tag de 16 bytes
```

* `iv`: 12 bytes aleatorios nuevos por mensaje.
* `content` se envía **tal como se capturó** (sin normalizar); `contentHash` se calcula sobre la versión normalizada.
* El orden de campos y los espacios del JSON no son significativos: el receptor parsea el JSON descifrado.
* Campos desconocidos se ignoran (compatibilidad hacia delante).

### 6.3. Apertura

1. `protocolVersion != 1` -> rechazar (`400`).
2. Descifrar con `encKey` y la `aad` reconstruida; si falla la autenticación GCM -> rechazar (`400`).
3. Parsear la carga; `type` distinto de `"text"` o `"image"` -> rechazar (`400`).
4. Texto: recalcular `contentHash(content)`. Imagen: `mimeType` admitido, `data` no vacío y `imageHash(data)`. Si no coincide -> rechazar (`400`).
5. Además, `originDeviceId` del sobre debe coincidir con `X-Klip-Device` -> si no, `400`.

## 7. Reglas Antibucle

Estado por dispositivo: `lastSentHash`, `lastReceivedHash`, `processedMessageIds` (cola circular de `PROCESSED_IDS_CAPACITY`).

**Saliente** (evaluar en este orden; la primera regla que aplica decide):

| # | Condición | Resultado |
| :--- | :--- | :--- |
| 1 | sincronización desactivada | `SKIP_DISABLED` |
| 2 | contenido marcado como sensible, salvo que el usuario haya activado «Enviar también contraseñas» en este dispositivo (ajuste local `sendSensitive`, desactivado por defecto; solo texto) | `SKIP_SENSITIVE` |
| 3 | `isBlank(content)` | `SKIP_BLANK` |
| 4 | no es prueba y `hash == lastSentHash` | `SKIP_ALREADY_SENT` |
| 5 | no es prueba y `hash == lastReceivedHash` | `SKIP_JUST_RECEIVED` |
| — | en otro caso | `SEND`; tras enviarlo, `lastSentHash = hash` (no se actualiza para pruebas) |

**Entrante** (tras verificar firma y abrir el sobre):

| # | Condición | Resultado |
| :--- | :--- | :--- |
| 1 | `originDeviceId == localDeviceId` | `IGNORE_SELF` (`409`) |
| 2 | `messageId` en `processedMessageIds` | `IGNORE_DUPLICATE` (`409`) |
| — | en otro caso | `ACCEPT`: añadir `messageId`, `lastReceivedHash = hash` **antes** de escribir en el portapapeles |

## 8. Emparejamiento, Grupo y Descubrimiento

Todos los cuerpos son JSON UTF-8. Los errores devuelven `{"error": "<código>"}`. Salvo `/pair/request`, todas las peticiones van firmadas (§5). Los campos desconocidos se ignoran.

Objeto `DeviceInfo` (se repite en varios mensajes):

```json
{ "deviceId": "...", "deviceName": "...", "platform": "android|windows|devpeer", "httpPort": 8765 }
```

### 8.1. `POST /pair/request` (sin firma)

Solo se acepta mientras la **ventana de emparejamiento** del receptor (R) está abierta (`MainActivity` o `ConfigWindow` visible).

Petición del iniciador (I):

```json
{ "protocolVersion": 1, "deviceId": "...", "deviceName": "...", "platform": "...", "httpPort": 8765,
  "publicKey": "base64(65 bytes)", "nonce": "base64(32 bytes)" }
```

R valida, en este orden:

| Condición | Respuesta |
| :--- | :--- |
| Ventana cerrada | `403 {"error":"pairing_closed"}` |
| Más de `PAIR_REQUESTS_PER_MINUTE` desde la misma IP | `429 {"error":"rate_limited"}` |
| Versión no soportada, `deviceId` vacío o igual al propio, clave pública o nonce inválidos | `400 {"error":"invalid_request"}` |
| Hay otra sesión pendiente no caducada con **otro** dispositivo | `409 {"error":"busy"}` |

Si todo es correcto, R genera su par efímero y su nonce, deriva `pairSecret` y el PIN (§4), crea una sesión pendiente que caduca en `PAIRING_TIMEOUT_MS` (sustituye a una anterior del mismo I) y muestra al usuario *"¿Vincular con [deviceName]? PIN 482 913"*. Responde:

```json
200 { "protocolVersion": 1, "deviceId": "...", "deviceName": "...", "platform": "...", "httpPort": 8765,
      "publicKey": "base64", "nonce": "base64", "issuedAt": 1763930000000 }
```

`issuedAt` (reloj de R) es la versión inicial del secreto de pareja en ambos lados (§8.3).

### 8.2. `POST /pair/confirm` y `POST /pair/cancel` (firmadas con la sesión)

Firmadas con la `macKey` derivada de la sesión pendiente (`X-Klip-Device` = I). R acepta estas dos rutas también para dispositivos que solo tienen una sesión pendiente.

* I envía `/pair/confirm` (cuerpo `{}`) cuando **su** usuario confirma que el PIN coincide. R **retiene la respuesta** hasta que su propio usuario decide o la sesión caduca:
  * `200 {"accepted": true}`: ambos guardan la pareja. R toma como `host` la IP de origen de la petición y como puerto el `httpPort` declarado; I usa el host al que se conectó.
  * `403 {"error":"rejected"}`: el usuario de R rechazó.
  * `408 {"error":"timeout"}`: la sesión caducó.
  * El cliente de I usa un timeout de `PAIRING_TIMEOUT_MS + 5 s` para esta petición.
* I envía `/pair/cancel` (cuerpo `{}`) si su usuario rechaza el PIN o cierra el diálogo. R descarta la sesión y cierra su diálogo. Responde `200`.
* Si el usuario de R rechaza antes de recibir `/pair/confirm`, la sesión queda marcada como rechazada hasta que caduca.

Tras un emparejamiento correcto, **cada** lado ejecuta las presentaciones de grupo (§8.3) hacia el nuevo dispositivo.

### 8.3. `POST /group/introduce`

Tras emparejarse A y B, A presenta a B cada uno de sus otros dispositivos vinculados M (y viceversa). Para cada M, el presentador X genera un secreto aleatorio `s` (32 bytes) y un `issuedAt` (su reloj), y envía **la misma** presentación a ambos extremos: a B con los datos de M, y a M con los datos de B.

```json
{ "member": { DeviceInfo, "host": "192.168.1.20" },
  "issuedAt": 1763930000000,
  "iv": "base64(12 bytes)",
  "ciphertext": "base64(AES-256-GCM(encKey(X,receptor), iv, aad, s))" }
```

`aad = UTF8("/group/introduce" + "\n" + X.deviceId + "\n" + member.deviceId + "\n" + issuedAt)`

El receptor:

| Condición | Respuesta |
| :--- | :--- |
| `member.deviceId` es el propio, o el cifrado no se puede abrir | `400 {"error":"invalid_request"}` |
| Ya existe una pareja con `member` | `200 {"applied": false}` (se conserva la existente) |
| En otro caso | Crea la pareja con `s` -> `200 {"applied": true}` |

**Una presentación nunca sustituye una pareja existente.** Si lo hiciera, dos vinculaciones solapadas podrían cambiar a la vez los canales por los que viajan otras presentaciones y dejar parejas descuadradas sin forma de repararse solas. Para renovar el secreto de una pareja se vuelve a vincular por PIN (§8.1–8.2), que siempre sustituye porque es un intercambio directo entre los dos extremos. Cada pareja guarda además, como información, la versión de su secreto `(issuedAt, issuerId)`: `(issuedAt de §8.1, "pin")` o `(issuedAt, presentador)`.

Si un extremo no responde, X guarda la presentación como **pendiente** y la reintenta cuando vuelve a tener contacto verificado con él (petición firmada recibida o `/ping` correcto) y al arrancar. Las pendientes caducan a los `INTRODUCTION_EXPIRY_MS`.

### 8.4. `POST /unpair` y `POST /group/remove`

* `/unpair` (cuerpo `{}`): el receptor elimina su pareja con el emisor. `200`. El emisor la elimina localmente aunque falle el envío.
* `/group/remove` (cuerpo `{"deviceId": "<expulsado>"}`): el emisor lo envía a todos sus dispositivos (incluido el expulsado) y luego lo elimina localmente. El receptor elimina la pareja con el expulsado; si el expulsado es él mismo, elimina **todas** sus parejas. `200`.

### 8.5. `GET /ping`

Firmada, cuerpo vacío. Responde `200` con `DeviceInfo` (sin `httpPort`): `{"deviceId", "deviceName", "platform"}`.

### 8.6. Beacon UDP (radar)

Solo mientras la ventana de configuración está visible. Cada `BEACON_INTERVAL_MS` se envía un datagrama a `255.255.255.255:8766` y a la dirección de broadcast de cada interfaz IPv4 activa:

```json
{ "klip": 1, "deviceId": "...", "deviceName": "...", "platform": "...", "httpPort": 8765, "pairingOpen": true }
```

El receptor ignora los suyos. Si el `deviceId` es de un dispositivo vinculado y la IP de origen es distinta de la guardada, comprueba la nueva IP con un `/ping` firmado antes de actualizarla (un beacon no está autenticado). Si es desconocido, lo muestra en *"Dispositivos detectados"* y lo retira tras `DISCOVERY_EXPIRY_MS` sin beacons.

### 8.7. mDNS / DNS-SD

Mientras su servidor HTTP está activo, cada nodo anuncia `_klip._tcp.local.` con nombre de instancia = `deviceId`, puerto = `httpPort` y TXT `id=<deviceId>`. Si un envío falla por error de conexión, el emisor resuelve la instancia del destino (timeout 1,5 s) y, si la IP ha cambiado, reintenta **una vez** y actualiza la IP guardada.

## 9. Versionado

Un cambio incompatible incrementa `PROTOCOL_VERSION`. Un receptor responde `400` con `{"error": "unsupported_version", "supported": [1]}` a versiones que no conoce.
