Primera versión estable de **Klip**: sincroniza el portapapeles entre Windows, Android y Linux dentro de tu red local, sin nube.

## Novedades

- **Tres plataformas:** app de bandeja para Windows, app para Android y app de bandeja para Linux (X11 y Wayland).
- **Emparejamiento por PIN** con descubrimiento automático en la red (o por IP), y **grupo automático**: todos tus dispositivos quedan vinculados entre sí.
- **Cifrado de extremo a extremo** (ECDH P-256, HMAC-SHA256, AES-256-GCM) con una clave por pareja.
- **Envío automático** al copiar en Windows y en Linux (X11, KDE, wlroots); en Android desde el Tile de Ajustes rápidos, el menú Compartir o la selección de texto; en GNOME (Wayland) con un atajo de teclado.
- **"Última recepción"** y avisos con el texto recibido, para comprobar de un vistazo que todo funciona.
- **Contenido sensible** de gestores de contraseñas que nunca se envía.
- Cambios de IP resueltos solos (radar, mDNS y actualización pasiva).

## Descargas

| Plataforma | Archivo |
| :--- | :--- |
| Windows 10/11 x64 (portable) | `Klip-1.0.0-windows-x64.exe` |
| Android 10+ | `Klip-1.0.0-android.apk` |
| Linux x64 (cualquier distro) | `Klip-1.0.0-x86_64.AppImage` |
| Debian / Ubuntu / Mint x64 | `klip_1.0.0_amd64.deb` |

Instrucciones y requisitos de cada plataforma en el [README](https://github.com/enekor/klip#readme).

## Limitaciones conocidas

- Solo texto y enlaces (no imágenes ni archivos).
- Todos los dispositivos deben estar en la misma red local; las redes con aislamiento de clientes bloquean la comunicación.
- En GNOME (Wayland) y en Android el envío necesita un gesto, porque el sistema no deja leer el portapapeles en segundo plano.
- El ejecutable de Windows no está firmado: SmartScreen puede avisar la primera vez.
- Sin versión para macOS/iOS.
