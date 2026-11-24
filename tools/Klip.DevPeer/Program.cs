using System.Text;
using Klip.Core;
using Klip.Node;

// KlipDevPeer: a fake Klip device for testing without the Android app.
// Stores its config (with the pair secret in plain text) in ./devpeer.json unless --config is given.

Console.OutputEncoding = Encoding.UTF8;
var argv = args.ToList();
var configPath = TakeOption(argv, "--config") ?? "devpeer.json";

if (argv.Count == 0 || argv[0] is "-h" or "--help" or "help")
{
    PrintHelp();
    return 0;
}

var command = argv[0];
argv.RemoveAt(0);
string? pendingSession = null;

try
{
    switch (command)
    {
        case "new-secret":
            Console.WriteLine(Convert.ToHexStringLower(PairKeys.NewRandomSecret()));
            return 0;

        case "mdns-test":
        {
            // Self-test of the native Windows DNS-SD integration: announce a random instance and resolve it back.
            if (!OperatingSystem.IsWindows())
            {
                Console.WriteLine("Solo disponible en Windows.");
                return 2;
            }
            using var mdns = new WindowsDnsSd(m => Console.WriteLine($"  [{m}]"), LanAddress.PreferredIPv4);
            Console.WriteLine("IPs de la LAN: " + string.Join(", ", LanAddress.LocalIPv4()));
            var id = "klip-selftest-" + Guid.NewGuid().ToString("N")[..8];
            Console.WriteLine($"Registrando {id}._klip._tcp.local en el puerto 8799…");
            mdns.Register(id, 8799);
            await Task.Delay(2000);
            using var cts = new CancellationTokenSource(5000);
            var ip = await mdns.ResolveAsync(id, cts.Token);
            Console.WriteLine(ip is null ? "Resolución: FALLÓ" : $"Resolución: OK -> {ip}");
            using var cts2 = new CancellationTokenSource(1500);
            var missing = await mdns.ResolveAsync("klip-no-existe-" + Guid.NewGuid().ToString("N")[..8], cts2.Token);
            Console.WriteLine(missing is null ? "Instancia inexistente: OK (no resuelve)" : $"Instancia inexistente: INESPERADO -> {missing}");
            mdns.Unregister();
            await Task.Delay(500);
            return ip is null ? 1 : 0;
        }

        case "init":
        {
            var store = new ConfigStore(configPath);
            var cfg = store.LoadOrCreate();
            cfg.DeviceName = TakeOption(argv, "--name") ?? cfg.DeviceName;
            cfg.HttpPort = int.Parse(TakeOption(argv, "--port") ?? cfg.HttpPort.ToString());
            cfg.Platform = "devpeer";
            store.Save(cfg);
            Console.WriteLine($"Config:    {Path.GetFullPath(configPath)}");
            Console.WriteLine($"Nombre:    {cfg.DeviceName}");
            Console.WriteLine($"Device ID: {cfg.DeviceId}");
            Console.WriteLine($"Puerto:    {cfg.HttpPort}");
            return 0;
        }

        case "add-peer":
        {
            await using var node = OpenNode();
            var id = Require(argv, "--id");
            var secret = Convert.FromHexString(Require(argv, "--secret"));
            node.AddOrUpdatePeer(id, TakeOption(argv, "--name") ?? id, Require(argv, "--host"),
                int.Parse(TakeOption(argv, "--port") ?? Protocol.HttpPort.ToString()), secret, "windows");
            Console.WriteLine("Dispositivo añadido.");
            return 0;
        }

        case "peers":
        {
            await using var node = OpenNode();
            foreach (var p in node.Peers) Console.WriteLine($"{p.Name,-20} {p.Host}:{p.Port,-6} {(p.Enabled ? "activo" : "inactivo"),-9} {p.DeviceId}");
            return 0;
        }

        case "send":
        {
            await using var node = OpenNode();
            var isTest = argv.Remove("--test");
            var text = argv.Count > 0 ? string.Join(' ', argv) : await Console.In.ReadToEndAsync();
            var outcome = isTest ? await node.SendTestAsync() : await node.SendClipboardAsync(text);
            Console.WriteLine($"Decisión: {outcome.Decision}");
            foreach (var r in outcome.Results) Console.WriteLine($"  {r.Name}: {(r.Ok ? $"OK ({r.HttpStatus})" : r.Error)}");
            return outcome.Decision == OutgoingDecision.Send && !outcome.AnyOk ? 1 : 0;
        }

        case "ping":
        {
            await using var node = OpenNode();
            var failed = false;
            foreach (var p in node.Peers)
            {
                var r = await node.PingAsync(p.DeviceId);
                failed |= !r.Ok;
                Console.WriteLine(r.Ok ? $"{p.Name}: OK {r.ElapsedMs} ms ({r.Info?.DeviceName}, {r.Info?.Platform})" : $"{p.Name}: {r.Error}");
            }
            return failed ? 1 : 0;
        }

        case "pair":
        {
            await using var node = OpenNode();
            node.LogMessage += m => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {m}");
            var host = Require(argv, "--host");
            var port = int.Parse(TakeOption(argv, "--port") ?? Protocol.HttpPort.ToString());
            await node.StartAsync(); // the other side may send group introductions right away
            var (pairing, error) = await node.StartPairingAsync(host, port);
            if (pairing is null)
            {
                Console.WriteLine($"Error: {error}");
                return 1;
            }
            Console.WriteLine($"PIN: {pairing.Pin} ({pairing.DeviceName}, {pairing.Platform})");
            if (!argv.Remove("--yes"))
            {
                Console.Write("¿Coincide el PIN en el otro dispositivo? [s/N] ");
                if (Console.ReadLine()?.Trim().ToLowerInvariant() is not ("s" or "si" or "sí" or "y" or "yes"))
                {
                    await node.CancelPairingAsync(pairing);
                    Console.WriteLine("Cancelado.");
                    return 1;
                }
            }
            Console.WriteLine("Esperando confirmación en el otro dispositivo…");
            var outcome = await node.ConfirmPairingAsync(pairing);
            Console.WriteLine($"Resultado: {outcome.Status}{(outcome.Message is null ? "" : $" ({outcome.Message})")}");
            await Task.Delay(1000); // let group introductions go out
            return outcome.Ok ? 0 : 1;
        }

        case "listen":
        {
            var clipboard = new ConsoleClipboard();
            await using var node = OpenNode(clipboard);
            var acceptPairing = argv.Remove("--accept-pairing");
            node.LogMessage += m => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {m}");
            node.TestReceived += from => Console.WriteLine($"*** Prueba recibida correctamente desde {from} ***");
            node.PairingOpen = true;
            node.PairingRequested += p =>
            {
                Console.WriteLine($"*** Solicitud de vinculación de {p.DeviceName} ({p.Platform}) — PIN {p.Pin} ***");
                if (acceptPairing) node.RespondToPairing(p.SessionId, true);
                else Console.WriteLine("    (escribe 'aceptar' o 'rechazar')");
                pendingSession = p.SessionId;
            };
            node.IncomingPairingFinished += (_, status, name) => Console.WriteLine($"*** Vinculación con {name}: {status} ***");
            await node.StartAsync();
            if (argv.Remove("--radar"))
            {
                node.StartDiscovery();
                node.DiscoveredChanged += () => Console.WriteLine("Radar: " + string.Join(", ", node.Discovered.Select(d => $"{d.DeviceName} {d.Host}:{d.HttpPort}")));
            }
            Console.WriteLine($"Escuchando en el puerto {node.HttpPort} como «{node.DeviceName}» ({node.DeviceId}).");
            Console.WriteLine("Escribe una línea y pulsa Enter para enviarla. Ctrl+C para salir.");
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
            // Also stop cleanly on SIGTERM (kill, test harnesses, service managers).
            using var sigterm = System.Runtime.InteropServices.PosixSignalRegistration.Create(
                System.Runtime.InteropServices.PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; cts.Cancel(); });
            var input = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested && await Console.In.ReadLineAsync() is { } line)
                {
                    if (line.Length == 0) continue;
                    if (pendingSession is { } sid && line.Trim() is "aceptar" or "rechazar")
                    {
                        node.RespondToPairing(sid, line.Trim() == "aceptar");
                        pendingSession = null;
                        continue;
                    }
                    var outcome = await node.SendClipboardAsync(line);
                    Console.WriteLine($"  -> {outcome.Decision}: " + string.Join(", ", outcome.Results.Select(r => $"{r.Name} {(r.Ok ? "OK" : r.Error)}")));
                }
            });
            try { await Task.Delay(Timeout.Infinite, cts.Token); } catch (OperationCanceledException) { }
            return 0;
        }

        default:
            Console.Error.WriteLine($"Comando desconocido: {command}");
            PrintHelp();
            return 2;
    }
}
catch (Exception e) when (e is ArgumentException or FormatException or IOException)
{
    Console.Error.WriteLine($"Error: {e.Message}");
    return 2;
}

SyncNode OpenNode(IClipboardWriter? clipboard = null)
{
    if (!File.Exists(configPath)) throw new IOException($"No existe {configPath}. Ejecuta primero: KlipDevPeer init");
    var node = new SyncNode(new ConfigStore(configPath), new PlainSecretProtector(), clipboard ?? new ConsoleClipboard());
    if (OperatingSystem.IsWindows()) node.Resolver = new WindowsDnsSd(m => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {m}"), LanAddress.PreferredIPv4);
    return node;
}

static string? TakeOption(List<string> list, string name)
{
    var i = list.IndexOf(name);
    if (i < 0 || i + 1 >= list.Count) return null;
    var value = list[i + 1];
    list.RemoveRange(i, 2);
    return value;
}

static string Require(List<string> list, string name) =>
    TakeOption(list, name) ?? throw new ArgumentException($"Falta {name}");

static void PrintHelp() => Console.WriteLine("""
    KlipDevPeer — dispositivo Klip falso para pruebas (Fase 2)

    Uso: KlipDevPeer [--config devpeer.json] <comando>

      init [--name N] [--port P]          Crea/actualiza la identidad (puerto por defecto 8765)
      new-secret                          Genera un secreto de pareja (64 hex)
      mdns-test                           Autotest de mDNS nativo (solo Windows)
      add-peer --id ID --host H [--port P] [--name N] --secret HEX
                                          Vincula manualmente con otro dispositivo
      peers                               Lista los dispositivos vinculados
      ping                                Comprueba la conexión con cada dispositivo
      send [--test] [texto]               Envía un texto (o stdin) / una prueba
      pair --host H [--port P] [--yes]    Se vincula por PIN con otro dispositivo (que debe tener Klip abierto)
      listen [--accept-pairing] [--radar] Recibe copias y solicitudes de vinculación; cada línea escrita se envía
                                          ('aceptar' / 'rechazar' responde a una solicitud pendiente)

    Ejemplo en el mismo PC que Klip.exe (con la ventana de configuración de Klip abierta):
      KlipDevPeer init --name DevPeer --port 8770
      KlipDevPeer pair --host 127.0.0.1 --port 8765
      KlipDevPeer listen
    """);

internal sealed class ConsoleClipboard : IClipboardWriter
{
    public void WriteText(string text)
    {
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Portapapeles <- ({text.Length} caracteres)");
        Console.WriteLine(text.Length > 2000 ? text[..2000] + "…" : text);
    }
}
