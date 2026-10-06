using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MonitorRedPCJ.Models;
using MonitorRedPCJ.Native;

namespace MonitorRedPCJ.Services.Tools
{
    /// Un destino visto: la IP con la que habla un programa, el nombre que resulta de
    /// resolverla en inverso y si está en la lista de rastreo.
    public class Destino
    {
        public string Exe { get; set; } = "";
        public string App { get; set; } = "";
        public string Ip { get; set; } = "";
        public int Puerto { get; set; }
        public string Host { get; set; } = "";
        public bool Rastreador { get; set; }
        public bool EsUdp { get; set; }
        public bool EnRedLocal { get; set; }
    }

    /// Radar de destinos: DNS inverso con caché sobre las conexiones que ya ve el monitor,
    /// comparación con la lista de rastreo y bloqueo por fichero hosts (reversible).
    public class RadarService
    {
        public static RadarService? Instancia { get; private set; }

        public event Action? Actualizado;

        private readonly object _lock = new object();
        private readonly Dictionary<string, string> _dnsCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _pendientes = new(StringComparer.OrdinalIgnoreCase);
        private readonly string _ficheroCaché;
        private Timer? _timer;
        private int _resolviendo;
        private volatile bool _trabajando;

        // La resolución inversa es lenta y a veces se queda colgada: se limita el número de
        // consultas simultáneas y cada una tiene su plazo.
        private const int MaxConsultasSimultaneas = 4;
        private const int MilisegundosPorConsulta = 2500;

        public RadarService()
        {
            _ficheroCaché = Path.Combine(Paths.Root, "dns-cache.json");
            Instancia = this;
        }

        public void Start()
        {
            CargarCaché();
            if (_timer != null) return;
            _trabajando = true;
            _timer = new Timer(_ => Vuelta(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5));
        }

        public void Stop()
        {
            _trabajando = false;
            var t = Interlocked.Exchange(ref _timer, null);
            try { t?.Dispose(); } catch { }
            GuardarCaché();
        }

        private void Vuelta()
        {
            if (!_trabajando || App.IsShuttingDown) return;
            try
            {
                foreach (var d in DestinosActuales())
                    if (!string.IsNullOrEmpty(d.Ip) && !_dnsCache.ContainsKey(d.Ip)) Encolar(d.Ip);
                ResolverLote();
            }
            catch { }
        }

        // ---------- Qué se está viendo ----------

        /// Conexiones abiertas en este momento, con el programa dueño de cada una.
        public List<Destino> DestinosActuales()
        {
            var lista = new List<Destino>();
            try
            {
                Dictionary<int, string> pidMap = App.Traffic.ExeMapaActual();
                foreach (var c in NativeNet.GetConnections())
                {
                    if (!c.IsTcp && c.RemoteAddress.Length == 0) { /* UDP no tiene destino */ }
                    if (string.IsNullOrEmpty(c.RemoteAddress) || c.RemoteAddress == "0.0.0.0") continue;
                    if (c.IsTcp && c.State != ConnectionInfo.EstabState && c.State != 3) continue;
                    string exe = ExeDePid(c.Pid, pidMap);
                    if (exe == null) continue;
                    lista.Add(Ensamblar(exe, c.RemoteAddress, c.RemotePort, c.IsTcp));
                }
            }
            catch { }
            return Distintos(lista);
        }

        /// Todos los destinos que PCJ guardó desde que se instaló (apps.json), no solo los de ahora.
        public List<Destino> DestinosHistoricos()
        {
            var lista = new List<Destino>();
            try
            {
                foreach (var a in App.Traffic.GetApps())
                {
                    foreach (var ip in App.Traffic.HostsOf(a))
                        if (!string.IsNullOrEmpty(ip) && ip != "0.0.0.0") lista.Add(Ensamblar(a.ExePath, ip, 0, true));
                }
            }
            catch { }
            return Distintos(lista);
        }

        /// Un programa con seis sockets al mismo servidor no son seis destinos: se agrupan por
        /// aplicación, IP, puerto y protocolo, y se ordena poniendo primero lo de rastreo.
        private static List<Destino> Distintos(List<Destino> lista)
        {
            var vistos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var salida = new List<Destino>();
            foreach (var d in lista)
                if (vistos.Add(d.Exe + "|" + d.Ip + "|" + d.Puerto + "|" + d.EsUdp)) salida.Add(d);
            return salida.OrderByDescending(d => d.Rastreador)
                        .ThenBy(d => d.App, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(d => d.Ip, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private Destino Ensamblar(string exe, string ip, int puerto, bool tcp)
        {
            string host;
            lock (_lock) _dnsCache.TryGetValue(ip, out host!);
            host ??= "";
            return new Destino
            {
                Exe = exe,
                App = Path.GetFileNameWithoutExtension(exe),
                Ip = ip,
                Puerto = puerto,
                EsUdp = !tcp,
                Host = host,
                Rastreador = ToolCatalog.EsRastreador(host),
                EnRedLocal = TrafficService.IsLocalOrPrivate(ip),
            };
        }

        private static string? ExeDePid(int pid, Dictionary<int, string> mapa)
        {
            if (pid <= 4) return null;
            if (mapa.TryGetValue(pid, out var e) && !string.IsNullOrEmpty(e)) return e;
            return null;
        }

        // ---------- Resolver nombres ----------

        private void Encolar(string ip)
        {
            lock (_lock)
            {
                if (_dnsCache.ContainsKey(ip) || !_pendientes.Add(ip)) return;
            }
        }

        /// Fuerza la resolución de lo que falte (el botón «Actualizar» la llama).
        public void ResolverAhora()
        {
            try
            {
                foreach (var d in DestinosActuales().Concat(DestinosHistoricos()))
                    if (!string.IsNullOrEmpty(d.Ip)) Encolar(d.Ip);
                ResolverLote(sinfín: true);
            }
            catch { }
        }

        private void ResolverLote(bool sinfín = false)
        {
            if (Interlocked.CompareExchange(ref _resolviendo, 1, 0) != 0) return;
            try
            {
                List<string> cola;
                lock (_lock)
                {
                    cola = _pendientes.Take(sinfín ? 400 : 24).ToList();
                    foreach (var ip in cola) _pendientes.Remove(ip);
                }
                if (cola.Count == 0) return;

                using var sem = new SemaphoreSlim(MaxConsultasSimultaneas);
                var tareas = cola.Select(async ip =>
                {
                    await sem.WaitAsync();
                    try
                    {
                        string nombre = await ResolverUna(ip);
                        lock (_lock)
                        {
                            // Se guarda también el negativo: una IP que no tiene nombre no se
                            // vuelve a preguntar en cada vuelta.
                            _dnsCache[ip] = nombre;
                        }
                    }
                    finally { sem.Release(); }
                });
                Task.WaitAll(tareas.ToArray(), TimeSpan.FromSeconds(30));
                GuardarCaché();
                Actualizado?.Invoke();
            }
            catch { }
            finally { Interlocked.Exchange(ref _resolviendo, 0); }
        }

        private static async Task<string> ResolverUna(string ip)
        {
            try
            {
                var tarea = Dns.GetHostEntryAsync(ip);
                if (await Task.WhenAny(tarea, Task.Delay(MilisegundosPorConsulta)) != tarea) return "";
                var entry = await tarea;
                return (entry.HostName ?? "").TrimEnd('.');
            }
            catch { return ""; }
        }

        private void CargarCaché()
        {
            try
            {
                if (!File.Exists(_ficheroCaché)) return;
                var leido = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_ficheroCaché));
                if (leido == null) return;
                lock (_lock)
                {
                    _dnsCache.Clear();
                    foreach (var kv in leido) _dnsCache[kv.Key] = kv.Value;
                }
            }
            catch { }
        }

        private void GuardarCaché()
        {
            try
            {
                Dictionary<string, string> copia;
                lock (_lock) copia = new Dictionary<string, string>(_dnsCache);
                Paths.Ensure();
                File.WriteAllText(_ficheroCaché, JsonSerializer.Serialize(copia));
            }
            catch { }
        }

        public int NombresResueltos
        {
            get { lock (_lock) return _dnsCache.Count(kv => !string.IsNullOrEmpty(kv.Value)); }
        }

        // ---------- Bloqueo por hosts ----------

        public const string MarcaInicio = "# ==== Monitor de Red PCJ: bloqueo de rastreo ====";
        public const string MarcaFin = "# ==== fin Monitor de Red PCJ ====";

        public static string RutaHosts =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                         "System32", "drivers", "etc", "hosts");

        /// Dominios que PCJ tiene bloqueados ahora mismo en el hosts.
        public static List<string> Bloqueados()
        {
            var salida = new List<string>();
            try
            {
                string ruta = RutaHosts;
                if (!File.Exists(ruta)) return salida;
                bool dentro = false;
                foreach (var linea in File.ReadAllLines(ruta))
                {
                    string l = linea.Trim();
                    if (l.StartsWith(MarcaInicio)) { dentro = true; continue; }
                    if (l.StartsWith(MarcaFin)) { dentro = false; continue; }
                    if (!dentro || l.Length == 0 || l.StartsWith("#")) continue;
                    var partes = l.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (partes.Length >= 2) salida.Add(partes[1]);
                }
            }
            catch { }
            return salida;
        }

        /// Escribe (o reemplaza) el bloque propio del hosts. Devuelve el motivo si no puede.
        public static bool AplicarBloqueos(IEnumerable<string> dominios, out string error)
        {
            error = "";
            try
            {
                if (!FirewallService.IsAdmin)
                {
                    error = "Hace falta permisos de administrador para escribir el fichero hosts.";
                    return false;
                }
                var nuevos = new List<string>(dominios
                    .Select(d => d.Trim().TrimStart('.').ToLowerInvariant())
                    .Where(d => d.Length > 3 && !d.Contains(' '))
                    .Distinct()
                    .OrderBy(d => d, StringComparer.Ordinal));

                string ruta = RutaHosts;
                string original = File.Exists(ruta) ? File.ReadAllText(ruta) : "";
                GuardarCopia(ruta, original);

                var sinBloque = QuitarBloque(original);
                var sb = new StringBuilder(sinBloque.TrimEnd('\r', '\n'));
                sb.Append("\r\n\r\n").Append(MarcaInicio).Append("\r\n");
                sb.Append("# Escrito por Monitor de Red PCJ. Cada linea apunta ese dominio a 0.0.0.0,\r\n");
                sb.Append("# o sea que el programa que intente hablar con él no obtiene respuesta.\r\n");
                sb.Append("# Para quitarlo todo: Mas herramientas > Radar de destinos > Quitar bloqueos.\r\n");
                foreach (var d in nuevos) sb.Append("0.0.0.0\t").Append(d).Append("\r\n");
                sb.Append(MarcaFin).Append("\r\n");

                File.WriteAllText(ruta, sb.ToString(), new UTF8Encoding(false));
                VaciarCacheSistema();
                App.Events.Add(EventKind.RuleChanged,
                    "Radar de destinos: " + nuevos.Count + " dominios de rastreo bloqueados en el hosts",
                    ruta, important: true);
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        public static bool QuitarBloqueos(out string error)
        {
            error = "";
            try
            {
                if (!FirewallService.IsAdmin)
                {
                    error = "Hace falta permisos de administrador para escribir el fichero hosts.";
                    return false;
                }
                string ruta = RutaHosts;
                if (!File.Exists(ruta)) return true;
                string original = File.ReadAllText(ruta);
                if (!original.Contains(MarcaInicio)) return true;
                GuardarCopia(ruta, original);
                File.WriteAllText(ruta, QuitarBloque(original), new UTF8Encoding(false));
                VaciarCacheSistema();
                App.Events.Add(EventKind.RuleChanged, "Radar de destinos: quitados los bloqueos del hosts",
                    ruta, important: true);
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        private static string QuitarBloque(string contenido)
        {
            var sb = new StringBuilder();
            bool dentro = false;
            foreach (var linea in contenido.Replace("\r\n", "\n").Split('\n'))
            {
                string l = linea.TrimStart();
                if (l.StartsWith(MarcaInicio)) { dentro = true; continue; }
                if (l.StartsWith(MarcaFin)) { dentro = false; continue; }
                if (dentro) continue;
                sb.Append(linea).Append('\n');
            }
            return sb.ToString();
        }

        private static void GuardarCopia(string ruta, string contenido)
        {
            try
            {
                string copia = ruta + ".pcj-copia";
                if (!File.Exists(copia)) File.WriteAllText(copia, contenido, new UTF8Encoding(false));
            }
            catch { }
        }

        // El resolver de Windows guarda las respuestas: sin esto el bloqueo tarda en notarse.
        private static void VaciarCacheSistema()
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("ipconfig.exe", "/flushdns")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = false,
                };
                using var p = System.Diagnostics.Process.Start(psi);
                p?.WaitForExit(4000);
            }
            catch { }
        }
    }
}
