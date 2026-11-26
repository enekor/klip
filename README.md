# Klip

Sincronizador de portapapeles **100 % local** entre **Windows, Android y Linux**: copias texto, enlaces o **imágenes** en un dispositivo y los pegas en otro, sin nube, sin cuentas y sin root.

- **Cifrado de extremo a extremo:** cada mensaje va firmado (HMAC-SHA256) y cifrado (AES-256-GCM) con una clave distinta por cada pareja de dispositivos.
- **Emparejamiento por PIN:** los dispositivos se descubren solos en la red; confirmas un código de 6 dígitos en ambas pantallas.
- **Grupo automático:** vincula la tablet con el móvil y quedará vinculada también con el PC.
- **Respeta los gestores de contraseñas:** lo que KeePass, KeePassXC, Bitwarden, 1Password… marcan como sensible no sale del dispositivo, salvo que actives «Enviar también contraseñas» en la ventana principal de ese dispositivo (desactivado por defecto).
- **Antibucle:** lo recibido no se reenvía.

## Descargas

En [Releases](https://github.com/enekor/klip/releases):

| Plataforma | Archivo | Notas |
| :--- | :--- | :--- |
| Windows 10/11 (x64) | `Klip-<versión>-windows-x64.exe` | Portable, no requiere instalar .NET |
| Android 10+ | `Klip-<versión>-android.apk` | Instalación manual (APK) |
| Linux (cualquier distro, x64) | `Klip-<versión>-x86_64.AppImage` | `chmod +x` y ejecutar |
| Debian / Ubuntu / Mint (x64) | `klip_<versión>_amd64.deb` | `sudo apt install ./klip_<versión>_amd64.deb` |

`SHA256SUMS.txt` contiene las sumas de comprobación.

## Primeros pasos

1. Instala Klip en todos tus dispositivos, conectados a la **misma red Wi-Fi**.
2. Abre Klip en dos de ellos (la ventana o pantalla principal debe estar visible para vincular).
3. Cada uno aparecerá en **"Dispositivos detectados"** del otro. Pulsa **Vincular**, comprueba que ambos muestran el **mismo PIN** y confirma en los dos.
4. Pulsa **Enviar prueba**: el otro dispositivo mostrará el texto en **"Última recepción"** y en un aviso.

Si el radar no encuentra al otro dispositivo (redes de invitados, aislamiento de clientes…), usa **Vincular por IP**.

## Cómo se envía en cada plataforma

| Plataforma | Envío | Recepción |
| :--- | :--- | :--- |
| Windows | Automático al copiar | Automática |
| Linux X11, KDE Plasma (Wayland), Sway/Hyprland | Automático al copiar | Automática |
| Linux GNOME (Wayland) | Manual: menú **Enviar portapapeles ahora** o un atajo de teclado con `klip --send-clipboard` | Automática |
| Android | Tile de Ajustes rápidos, menú **Compartir** (texto o imagen) o menú de **selección de texto** | Automática |

Las imágenes (PNG y JPEG; otros formatos se convierten a PNG) siguen el mismo camino que el texto: una captura de pantalla en Windows (`Win + Mayús + S`) aparece en el portapapeles del móvil, y una foto compartida desde la galería del móvil se pega en el PC.

Android y GNOME (Wayland) no permiten a una app leer el portapapeles en segundo plano; por eso el envío necesita un gesto.

## Notas por plataforma

### Windows
- La primera vez Klip ofrece crear las reglas del Firewall (TCP 8765 y UDP 8766, solo redes **privadas**). Asegúrate de que tu Wi-Fi está marcada como red privada.
- El ejecutable no está firmado con un certificado de Windows: SmartScreen puede avisar la primera vez (**Más información → Ejecutar de todas formas**).
- Icono en la bandeja: interruptor de sincronización, enviar prueba, inicio con Windows, registros.
- Datos y registros en `%APPDATA%\Klip\`.

### Android
- Acepta las notificaciones y excluye a Klip de la optimización de batería para que el receptor siga activo con la pantalla apagada. Algunos fabricantes (Samsung, Xiaomi…) tienen ajustes extra: [dontkillmyapp.com](https://dontkillmyapp.com).
- Añade el Tile **"Enviar portapapeles"** a los Ajustes rápidos: copia → baja la cortina → tócalo.

### Linux
- Dependencias: `xclip` (X11) o `wl-clipboard` (Wayland). Recomendadas: `avahi-utils` (mDNS), `libnotify-bin` (avisos), `libsecret-tools` (guardar claves en el llavero). El `.deb` las instala o recomienda; con la AppImage, instálalas tú: `sudo apt install xclip wl-clipboard avahi-utils libnotify-bin libsecret-tools`.
- **Bandeja del sistema:** funciona en KDE, XFCE, Cinnamon y Ubuntu. En GNOME sin Ubuntu necesitas la extensión [AppIndicator](https://extensions.gnome.org/extension/615/appindicator-support/); sin ella Klip funciona como ventana normal (al cerrarla sigue activo; ábrelo de nuevo desde el menú de aplicaciones).
- **Atajo en GNOME:** Configuración → Teclado → Atajos personalizados → comando `klip --send-clipboard` (con la AppImage, la ruta completa del archivo `.AppImage` seguida de `--send-clipboard`).
- **Cortafuegos:** si usas `ufw`: `sudo ufw allow 8765/tcp && sudo ufw allow 8766/udp`.
- Configuración en `~/.config/klip/`, registros en `~/.local/state/klip/klip.log`.

## Seguridad

- Las claves se intercambian con ECDH P-256 y se verifican con el PIN (evita ataques de intermediario en la red).
- Cada petición lleva firma HMAC con marca de tiempo y nonce (no se puede reenviar una petición capturada).
- El contenido viaja cifrado con AES-256-GCM; ni el texto ni su huella van en claro.
- Las claves se guardan protegidas: DPAPI (Windows), Android Keystore, llavero del escritorio o archivo `0600` (Linux).
- Todo funciona solo en la red local; no hay servidores externos.

Detalles en la [especificación del protocolo](docs/protocol.md) y en el [plan de diseño](docs/plan.md).

## Compilar desde el código

Requisitos: .NET 10 SDK, JDK 21, Android SDK (API 37.2) y Python 3 con `cryptography` (vectores de prueba).

```sh
tools/build-release.sh          # tests + todos los artefactos en out/release/
linux/test-clipboard.sh         # tests del portapapeles Linux con Xvfb y sway headless
```

| Carpeta | Contenido |
| :--- | :--- |
| `docs/` | Plan de diseño y especificación del protocolo |
| `protocol/`, `tools/gen_test_vectors.py` | Vectores de prueba compartidos (implementación de referencia en Python) |
| `core-dotnet/` | Núcleo del protocolo y nodo de red en C# (Windows, Linux, DevPeer) |
| `core-kotlin/` | Núcleo del protocolo y nodo de red en Kotlin (Android) |
| `windows/` | App de bandeja para Windows (WinForms) |
| `linux/` | App de bandeja para Linux (Avalonia) y su capa de plataforma |
| `android/` | App Android (Compose) |
| `tools/Klip.DevPeer/` | Dispositivo Klip de consola para pruebas e interoperabilidad |

## Licencia

Klip es software libre bajo la [GNU General Public License v3.0](LICENSE) (`GPL-3.0-only`): puedes usarlo, estudiarlo, modificarlo y redistribuirlo, siempre que las versiones derivadas sigan publicándose bajo la misma licencia. Se distribuye sin ninguna garantía.
