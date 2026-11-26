**Klip 1.1.0** añade la opción de enviar también las contraseñas.

## Novedades

- **«Enviar también contraseñas»** (desactivado por defecto): un interruptor nuevo en la ventana principal de cada aplicación (Windows, Android y Linux). Al activarlo, el texto que los gestores de contraseñas (KeePass, KeePassXC, Bitwarden, 1Password…) marcan como sensible se envía como cualquier otro texto.
  - El ajuste es de cada dispositivo: solo afecta a lo que ese dispositivo envía.
  - Con el interruptor apagado todo sigue como en 1.0.0: el contenido sensible no sale del dispositivo.
  - Las imágenes marcadas como sensibles siguen sin enviarse.

Compatible con 1.0.0: no cambia el protocolo ni hace falta volver a vincular los dispositivos.

## Descargas

| Plataforma | Archivo |
| :--- | :--- |
| Windows 10/11 x64 (portable) | `Klip-1.1.0-windows-x64.exe` |
| Android 10+ | `Klip-1.1.0-android.apk` |
| Linux x64 (cualquier distro) | `Klip-1.1.0-x86_64.AppImage` |
| Debian / Ubuntu / Mint x64 | `klip_1.1.0_amd64.deb` |

Instrucciones y requisitos de cada plataforma en el [README](https://github.com/enekor/klip#readme).

## Limitaciones conocidas

- Texto, enlaces e imágenes; no archivos.
- Todos los dispositivos deben estar en la misma red local; las redes con aislamiento de clientes bloquean la comunicación.
- En GNOME (Wayland) y en Android el envío necesita un gesto, porque el sistema no deja leer el portapapeles en segundo plano.
- El ejecutable de Windows no está firmado: SmartScreen puede avisar la primera vez.
- Sin versión para macOS/iOS.
