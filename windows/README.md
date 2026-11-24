# Klip para Windows

App de bandeja del sistema (.NET 10 + WinForms + Kestrel). La lógica de red y sincronización vive en
[`core-dotnet/Klip.Node`](../core-dotnet/Klip.Node); este proyecto solo aporta la parte específica de Windows:

| Archivo | Responsabilidad |
| :--- | :--- |
| `TrayApp.cs` | Icono de bandeja, interruptor único de sincronización (persistente), avisos |
| `ClipboardMonitor.cs` | `WM_CLIPBOARDUPDATE`, antirrebote, ignorar escrituras propias, reintentos, contenido sensible; escritura con CRLF |
| `ConfigWindow.cs` | Ventana de configuración y alta manual de dispositivos (modo desarrollo) |
| `Platform.cs` | DPAPI, log, inicio con Windows, firewall, IPs locales |

Datos en `%APPDATA%\Klip\` (`config.json`, `logs\klip.log`).

## Compilar

Desde la raíz del repo (funciona también en Linux gracias a `EnableWindowsTargeting`):

```sh
dotnet publish windows/Klip.Windows -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true -p:DebugType=none -o out/windows
```

## Probar sin Android (Fase 2): `KlipDevPeer.exe`

`KlipDevPeer` es un dispositivo Klip falso de consola ([`tools/Klip.DevPeer`](../tools/Klip.DevPeer)).

En el mismo PC:

1. Ejecuta `Klip.exe`. Aparece la "K" azul en la bandeja; acepta crear las reglas de firewall si lo pide.
2. Doble clic en el icono -> copia el **ID de dispositivo**.
3. En una terminal:
   ```
   KlipDevPeer init --name DevPeer --port 8770
   KlipDevPeer new-secret
   KlipDevPeer add-peer --id <ID de Klip> --host 127.0.0.1 --port 8765 --name PC --secret <secreto>
   ```
   `init` muestra el **Device ID** de DevPeer.
4. En Klip: **Añadir manualmente…** -> Nombre `DevPeer`, ID = Device ID de DevPeer, host `127.0.0.1`, puerto `8770`,
   plataforma `unknown`, mismo secreto.
5. `KlipDevPeer listen` y:
   * Copia cualquier texto en Windows -> aparece en la consola de DevPeer.
   * Escribe una línea en DevPeer y pulsa Enter -> `Ctrl + V` en Windows la pega.
   * "Enviar prueba" en Klip -> DevPeer muestra `*** Prueba recibida… ***`.
   * Desactiva la sincronización en el menú de la bandeja -> el icono pasa a gris y DevPeer recibe "Error de conexión".

Para probar entre dos equipos, ejecuta DevPeer en el otro PC con el puerto por defecto (`8765`) y usa la IP de la LAN.
