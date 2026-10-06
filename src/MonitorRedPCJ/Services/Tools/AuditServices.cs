using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using MonitorRedPCJ.Models;
using MonitorRedPCJ.Native;

namespace MonitorRedPCJ.Services.Tools
{
    // ============================================================================
    //  Auditor de fugas
    // ============================================================================

    public class Hallazgo
    {
        public string Nombre { get; set; } = "";
        // 0 = correcto · 1 = revisa · 2 = fuga clara
        public int Nivel { get; set; }
        public string Detalle { get; set; } = "";
        public string Accion { get; set; } = "";     // texto del botón de la derecha, vacío = ninguno
        public string Clave { get; set; } = "";      // para no repetir el aviso cada hora
    }

    /// Revisión de seguridad del equipo: QUIC, DNS sobre HTTPS, resolvers, reglas sucias y
    /// permisos que nadie usa. Todo lo que lee sale de las tablas que PCJ ya consulta.
    public class LeakAuditService
    {
        public static LeakAuditService? Instancia { get; private set; }

        public event Action? Actualizado;

        private readonly object _lock = new object();
        private List<Hallazgo> _resultados = new();
        private Timer? _timer;
        private DateTime _ultimo = DateTime.MinValue;
        private readonly HashSet<string> _avisados = new(StringComparer.OrdinalIgnoreCase);
        private volatile bool _cadaHora;

        public LeakAuditService() { Instancia = this; }

        public void Start(bool cadaHora)
        {
            _cadaHora = cadaHora;
            if (_timer == null) ComprobarAhora();
            RenovarTimer();
        }

        public void Stop()
        {
            var t = Interlocked.Exchange(ref _timer, null);
            try { t?.Dispose(); } catch { }
        }

        public bool RepasoHorario
        {
            get => _cadaHora;
            set { _cadaHora = value; RenovarTimer(); }
        }

        private void RenovarTimer()
        {
            if (!_cadaHora)
            {
                var viejo = Interlocked.Exchange(ref _timer, null);
                try { viejo?.Dispose(); } catch { }
                return;
            }
            if (_timer != null) return;
            _timer = new Timer(_ =>
            {
                if (App.IsShuttingDown) return;
                var nuevos = ComprobarAhora();
                foreach (var h in nuevos.Where(h => h.Nivel > 0 && _avisados.Add(h.Clave)))
                    App.Events.Add(EventKind.Info, "Auditor de fugas: " + h.Nombre, h.Detalle, important: true);
            }, null, TimeSpan.FromMinutes(1), TimeSpan.FromHours(1));
        }

        public DateTime UltimaComprobacion => _ultimo;
        public List<Hallazgo> Resultados() { lock (_lock) return new List<Hallazgo>(_resultados); }

        public List<Hallazgo> ComprobarAhora()
        {
            var lista = new List<Hallazgo>();
            try
            {
                lista.Add(ComprobarQUIC());
                lista.Add(ComprobarDoH());
                lista.Add(ComprobarResolvers());
                lista.Add(ComprobarReglas());
                lista.Add(ComprobarPermisosSinUso());
                lista.Add(ComprobarPolitica());
                lista.Add(ComprobarFirewallActivos());
            }
            catch (Exception ex)
            {
                lista.Add(new Hallazgo { Nombre = "El auditor no pudo terminar", Nivel = 1, Detalle = ex.Message });
            }
            lock (_lock) _resultados = lista;
            _ultimo = DateTime.Now;
            Actualizado?.Invoke();
            return lista;
        }

        public string Resumen()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Auditoría de fugas — Monitor de Red PCJ — " +
                          _ultimo.ToString("dd/MM/yyyy HH:mm"));
            foreach (var h in Resultados())
                sb.AppendLine("[" + (h.Nivel == 0 ? "BIEN" : h.Nivel == 1 ? "REVISAR" : "FUGA") + "] " +
                              h.Nombre + " :: " + h.Detalle);
            return sb.ToString();
        }

        // ---------- Comprobaciones ----------

        private Hallazgo ComprobarQUIC()
        {
            var apps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int filas = 0;
            try
            {
                var mapa = App.Traffic.ExeMapaActual();
                foreach (var c in NativeNet.GetUdpConnections())
                {
                    if (c.RemotePort != 443) continue;
                    if (string.IsNullOrEmpty(c.RemoteAddress) || c.RemoteAddress == "0.0.0.0") continue;
                    filas++;
                    if (mapa.TryGetValue(c.Pid, out var exe) && !string.IsNullOrEmpty(exe))
                        apps.Add(Path.GetFileName(exe));
                }
            }
            catch { }

            if (filas == 0)
                return new Hallazgo { Nombre = "QUIC / HTTP3 (UDP 443)", Nivel = 0,
                    Detalle = "No hay tráfico saliendo por UDP 443 en este momento." };
            string quienes = apps.Count == 0 ? "varias conexiones UDP" : string.Join(", ", apps.Take(6));
            return new Hallazgo
            {
                Nombre = "QUIC / HTTP3 (UDP 443)",
                Nivel = 1,
                Detalle = filas + " sockets UDP hacia el puerto 443" +
                          (apps.Count > 0 ? ": " + quienes : "") +
                          ". Ese tráfico no pasa por el filtro de TCP y muchos bloqueos por " +
                          "aplicación no lo cortan: si una app tiene salida permitida, el navegador " +
                          "puede estar hablando por un canal aparte.",
                Accion = "Ver en Protección",
                Clave = "quic",
            };
        }

        // Nombres que delatan un resolver DNS sobre HTTPS conocido.
        private static readonly string[] HostsDoH =
        {
            "dns.google", "cloudflare-dns.com", "security.cloudflare-dns.com",
            "mozilla.cloudflare-dns.com", "dns.quad9.net", "doh.dnspod.net",
            "doh.opendns.com", "dns.alidns.com", "doh.sb",
        };

        private Hallazgo ComprobarDoH()
        {
            var vistos = new List<string>();
            try
            {
                var radar = RadarService.Instancia;
                if (radar != null)
                {
                    foreach (var d in radar.DestinosActuales().Concat(radar.DestinosHistoricos()))
                    {
                        if (string.IsNullOrEmpty(d.Host)) continue;
                        if (HostsDoH.Any(h => d.Host.EndsWith(h, StringComparison.OrdinalIgnoreCase)) &&
                            !vistos.Contains(d.Host, StringComparer.OrdinalIgnoreCase))
                            vistos.Add(d.Host);
                    }
                }
                if (vistos.Count == 0)
                {
                    foreach (var h in HostsDoH)
                        if (App.Traffic.GetApps().Any(a => App.Traffic.HostsOf(a)
                                .Any(ip => string.Equals(ip, h, StringComparison.OrdinalIgnoreCase))))
                            vistos.Add(h);
                }
            }
            catch { }

            if (vistos.Count == 0)
                return new Hallazgo { Nombre = "DNS sobre HTTPS", Nivel = 0,
                    Detalle = "No se ha visto tráfico hacia resolvers DoH conocidos. " +
                              "(Si el navegador tiene DNS seguro activado, las consultas salen cifradas " +
                              "y aquí pueden no aparecer: compruébalo en Ajustes del navegador.)" };
            return new Hallazgo
            {
                Nombre = "DNS sobre HTTPS",
                Nivel = 1,
                Detalle = "Tráfico hacia " + string.Join(", ", vistos.Take(5)) + ". Mientras usa DoH, " +
                          "las búsquedas de nombre del navegador no pasan por tu router ni por el " +
                          "registro de DNS local, así que un bloqueo por fichero hosts sí le afecta " +
                          "pero lo que tú ves en el monitor es menos.",
                Accion = "Ver destinos",
                Clave = "doh",
            };
        }

        private static readonly Dictionary<string, string> ResolversConocidos =
            new(StringComparer.OrdinalIgnoreCase)
            {
                { "8.8.8.8", "Google Public DNS" },
                { "8.8.4.4", "Google Public DNS (secundario)" },
                { "1.1.1.1", "Cloudflare" },
                { "1.0.0.1", "Cloudflare (secundario)" },
                { "9.9.9.9", "Quad9 (bloquea dominios maliciosos)" },
                { "149.112.121.10", "Quad9 (secundario)" },
                { "208.67.222.222", "OpenDNS" },
                { "208.67.220.220", "OpenDNS (secundario)" },
                { "64.6.64.6", "Verisign" },
                { "9.9.9.10", "Quad9 sin filtro de malware" },
            };

        private Hallazgo ComprobarResolvers()
        {
            var lineas = new List<string>();
            bool raro = false;
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    var ips = ni.GetIPProperties().DnsAddresses
                        .Where(s => s.AddressFamily == AddressFamily.InterNetwork)
                        .Select(s => s.Address.ToString())
                        .Distinct().ToList();
                    if (ips.Count == 0) continue;
                    foreach (var ip in ips)
                    {
                        bool local = TrafficService.IsLocalOrPrivate(ip);
                        string quien = ResolversConocidos.TryGetValue(ip, out var n) ? n : "otro";
                        lineas.Add(ni.Name + " → " + ip + (quien != "otro" ? " (" + quien + ")" : "") +
                                   (local ? " · dentro de tu red" : " · fuera de tu red"));
                        if (!local && quien == "otro") raro = true;
                    }
                }
            }
            catch { }

            if (lineas.Count == 0)
                return new Hallazgo { Nombre = "Servidores DNS del equipo", Nivel = 1,
                    Detalle = "PCJ no consiguió leer los servidores DNS configurados.", Clave = "dns0" };
            if (raro)
                return new Hallazgo
                {
                    Nombre = "Servidores DNS del equipo",
                    Nivel = 1,
                    Detalle = string.Join(" · ", lineas) +
                              ". Hay un resolver público que no es de los habituales: puede haberlo " +
                              "puesto un programa o una VPN sin que te enteraras.",
                    Accion = "Ver adapters",
                    Clave = "dns-raro",
                };
            bool todosRouter = lineas.All(l => l.Contains("dentro de tu red"));
            return new Hallazgo
            {
                Nombre = "Servidores DNS del equipo",
                Nivel = 0,
                Detalle = string.Join(" · ", lineas) + (todosRouter
                    ? ". Las consultas pasan por tu router, que es lo esperado."
                    : ". Usas un resolver público conocido: las consultas salen cifradas o no según el programa, " +
                      "pero el destino es de fiar."),
            };
        }

        private Hallazgo ComprobarReglas()
        {
            var muertas = new List<string>();
            var duplicadas = new List<string>();
            try
            {
                var t = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
                if (t == null)
                    return new Hallazgo { Nombre = "Reglas de Windows Firewall de PCJ", Nivel = 1,
                        Detalle = "No se pudo abrir el servicio de firewall para leer las reglas." };
                dynamic policy = Activator.CreateInstance(t)!;
                var vistas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (dynamic r in policy.Rules)
                {
                    string name = (string)r.Name;
                    if (!name.StartsWith(FirewallService.RulePrefix)) continue;
                    if (!vistas.Add(name)) duplicadas.Add(name);
                    string appPath = ((string)r.ApplicationName) ?? "";
                    if (appPath.Length > 0 && !File.Exists(appPath)) muertas.Add(name);
                }
            }
            catch (Exception ex)
            {
                return new Hallazgo { Nombre = "Reglas de Windows Firewall de PCJ", Nivel = 1,
                    Detalle = "No se pudieron leer: " + ex.Message };
            }

            if (muertas.Count > 0 || duplicadas.Count > 0)
                return new Hallazgo
                {
                    Nombre = "Reglas de Windows Firewall de PCJ",
                    Nivel = muertas.Count > 3 ? 2 : 1,
                    Detalle = muertas.Count + " reglas apuntan a un ejecutable que ya no existe" +
                              (muertas.Count > 0 ? " (" + string.Join(", ", muertas.Take(4)) + ")" : "") +
                              (duplicadas.Count > 0 ? " y " + duplicadas.Count + " están duplicadas" : "") +
                              ". No dejan de funcionar, pero ensucian la lista y hacen más lenta la " +
                              "comprobación de cada conexión.",
                    Accion = "Limpiar reglas obsoletas",
                    Clave = "reglas",
                };
            return new Hallazgo { Nombre = "Reglas de Windows Firewall de PCJ", Nivel = 0,
                Detalle = "Todas las reglas con prefijo MRPCJ apuntan a un fichero que existe y no hay duplicadas." };
        }

        private Hallazgo ComprobarPermisosSinUso()
        {
            var sinUso = new List<string>();
            try
            {
                var snapshot = App.Firewall.RuleSnapshot();
                foreach (var exe in SignatureService.AppsConocidas())
                {
                    if (!snapshot.Contains(FirewallService.AllowRuleName(exe, Direction.Out))) continue;
                    var app = App.Traffic.FindApp(exe);
                    if (app == null) continue;
                    if (app.Hosts.Count == 0 && app.TotalSent + app.TotalReceived == 0) sinUso.Add(Path.GetFileName(exe));
                }
            }
            catch { }

            if (sinUso.Count == 0)
                return new Hallazgo { Nombre = "Permisos de salida sin uso", Nivel = 0,
                    Detalle = "Cada aplicación con permiso de salida ha movido tráfico alguna vez desde que " +
                              "se instaló PCJ." };
            return new Hallazgo
            {
                Nombre = "Permisos de salida sin uso",
                Nivel = 1,
                Detalle = sinUso.Count + " aplicaciones tienen salida permitida y jamás conectaron: " +
                          string.Join(", ", sinUso.Take(8)) +
                          ". O se instalaron hace poco, o no necesitan internet: en el segundo caso, " +
                          "quitarles la salida es gratis.",
                Accion = "Ver en Protección",
                Clave = "sinuso",
            };
        }

        private Hallazgo ComprobarPolitica()
        {
            try
            {
                bool diceEstricto = App.Settings.Current.StrictMode;
                bool bloqueado = FirewallService.IsOutboundDefaultBlocked();
                if (diceEstricto && !bloqueado)
                    return new Hallazgo
                    {
                        Nombre = "Coherencia del bloqueo total",
                        Nivel = 2,
                        Detalle = "PCJ cree que el bloqueo total está activado, pero Windows tiene la salida " +
                                  "libre por defecto. Alguien cambió la política desde el panel de firewall, " +
                                  "o el último cambio no se aplicó del todo.",
                        Accion = "Volver a aplicar",
                        Clave = "politica",
                    };
                if (!diceEstricto && bloqueado)
                    return new Hallazgo
                    {
                        Nombre = "Coherencia del bloqueo total",
                        Nivel = 1,
                        Detalle = "La salida está cortada por defecto en Windows y PCJ no está en modo bloqueo " +
                                  "total. Puede ser el candado de «Perfiles y candado», o un cambio hecho a mano.",
                        Accion = "Revisar candado",
                        Clave = "politica2",
                    };
                return new Hallazgo { Nombre = "Coherencia del bloqueo total", Nivel = 0,
                    Detalle = "La política de salida de Windows coincide con lo que PCJ tiene anotado." };
            }
            catch (Exception ex)
            {
                return new Hallazgo { Nombre = "Coherencia del bloqueo total", Nivel = 1,
                    Detalle = "No se pudo leer la política: " + ex.Message };
            }
        }

        private Hallazgo ComprobarFirewallActivos()
        {
            try
            {
                var t = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
                if (t == null) return new Hallazgo { Nombre = "Firewall de Windows activado", Nivel = 1,
                    Detalle = "No se pudo abrir el servicio de firewall." };
                dynamic policy = Activator.CreateInstance(t)!;
                var apagados = new List<string>();
                // 1 = perfil de dominio · 0 = privado · 2 = público
                if (!(bool)policy.FirewallEnabled[1]) apagados.Add("dominio");
                if (!(bool)policy.FirewallEnabled[0]) apagados.Add("privado");
                if (!(bool)policy.FirewallEnabled[2]) apagados.Add("público");
                if (apagados.Count > 0)
                    return new Hallazgo
                    {
                        Nombre = "Firewall de Windows activado",
                        Nivel = 2,
                        Detalle = "El firewall está apagado en los perfiles: " + string.Join(", ", apagados) +
                                  ". Las reglas de PCJ no se evalúan si el firewall no está encendido.",
                        Clave = "fw-off",
                    };
                return new Hallazgo { Nombre = "Firewall de Windows activado", Nivel = 0,
                    Detalle = "Encendido en los tres perfiles: las reglas de PCJ se están evaluando." };
            }
            catch (Exception ex)
            {
                return new Hallazgo { Nombre = "Firewall de Windows activado", Nivel = 1,
                    Detalle = "No se pudo comprobar: " + ex.Message };
            }
        }

        /// Quita las MRPCJ cuyo ejecutable ya no existe. Lo único que el auditor escribe, y
        /// solo cuando el usuario pulsa el botón.
        public static int LimpiarObsoletas()
        {
            if (!FirewallService.IsAdmin) return -1;
            return App.Firewall.CleanupStaleRules();
        }
    }

    // ============================================================================
    //  Puertos expuestos
    // ============================================================================

    public class Escuchador
    {
        public string Exe { get; set; } = "";
        public string App { get; set; } = "";
        public string Direccion { get; set; } = "";
        public int Puerto { get; set; }
        public bool EsTcp { get; set; } = true;
        // 0 = solo tu PC · 1 = tu red · 2 = cualquier sitio
        public int Alcance { get; set; }
        public string Servicio { get; set; } = "";
        public bool Delicado { get; set; }
    }

    /// Lista lo que está en escucha y desde dónde se puede entrar, y avisa cuando aparece un
    /// escuchador que antes no estaba.
    public class PortAuditService
    {
        public static PortAuditService? Instancia { get; private set; }

        public event Action? Actualizado;

        private readonly object _lock = new object();
        private Timer? _timer;
        private readonly HashSet<string> _base = new(StringComparer.OrdinalIgnoreCase);
        private bool _ancla;
        private volatile bool _avisar;

        private static readonly Dictionary<int, (string nombre, bool delicado)> Servicios =
            new()
            {
                { 21, ("FTP", true) },
                { 22, ("SSH", true) },
                { 23, ("Telnet (texto en claro)", true) },
                { 69, ("TFTP", true) },
                { 110, ("POP3", true) },
                { 135, ("RPC de Windows", true) },
                { 137, ("NetBIOS nombre", true) },
                { 138, ("NetBIOS datagramas", true) },
                { 139, ("NetBIOS sesión (compartidos antiguos)", true) },
                { 143, ("IMAP", true) },
                { 445, ("SMB · compartidos de archivos", true) },
                { 554, ("RTSP", false) },
                { 1433, ("SQL Server", true) },
                { 1434, ("SQL Server navegador", true) },
                { 2049, ("NFS", true) },
                { 3306, ("MySQL/MariaDB", true) },
                { 3389, ("Escritorio remoto (RDP)", true) },
                { 5432, ("PostgreSQL", true) },
                { 5357, ("WSD · descubrimiento de dispositivos", false) },
                { 5985, ("PowerShell remoto (WinRM)", true) },
                { 5986, ("PowerShell remoto (WinRM sobre HTTPS)", true) },
                { 6379, ("Redis", true) },
                { 7680, ("Windows Media Player Sharing", false) },
                { 8080, ("HTTP alternativo", false) },
                { 8443, ("HTTPS alternativo", false) },
                { 9200, ("Elasticsearch", true) },
                { 11211, ("Memcached", true) },
                { 27017, ("MongoDB", true) },
                { 50070, ("Hadoop NameNode", true) },
            };

        public PortAuditService() { Instancia = this; }

        public void Start()
        {
            if (_timer != null) return;
            _ancla = false;
            _timer = new Timer(_ => Vuelta(), null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(20));
        }

        public void Stop()
        {
            var t = Interlocked.Exchange(ref _timer, null);
            try { t?.Dispose(); } catch { }
            lock (_lock) { _base.Clear(); _ancla = false; }
        }

        public bool AvisarNuevos
        {
            get => _avisar;
            set { _avisar = value; if (value) lock (_lock) { _base.Clear(); _ancla = false; } }
        }

        private void Vuelta()
        {
            if (App.IsShuttingDown) return;
            try
            {
                var actuales = Escuchadores();
                lock (_lock)
                {
                    if (_avisar && _ancla)
                    {
                        var nuevas = actuales
                            .Where(e => !_base.Contains(Clave(e)) && e.Alcance > 0)
                            .ToList();
                        foreach (var n in nuevas)
                        {
                            App.Events.Add(EventKind.Info,
                                "Puertos expuestos: " + n.App + " abrió el " +
                                (n.EsTcp ? "TCP" : "UDP") + " " + n.Puerto + " en " + n.Direccion,
                                n.Servicio.Length > 0 ? n.Servicio : "servicio sin nombre conocido",
                                important: true);
                            try
                            {
                                App.Tray?.Toast("Puerto nuevo en escucha",
                                    n.App + " escucha en " + (n.EsTcp ? "TCP" : "UDP") + " " + n.Puerto +
                                    " (" + AlcanceTexto(n.Alcance) + ").");
                            }
                            catch { }
                        }
                    }
                    _base.Clear();
                    foreach (var e in actuales) _base.Add(Clave(e));
                    _ancla = true;
                }
                Actualizado?.Invoke();
            }
            catch { }
        }

        private static string Clave(Escuchador e) =>
            (e.EsTcp ? "t" : "u") + "|" + e.Direccion + "|" + e.Puerto + "|" + e.Exe;

        public List<Escuchador> Escuchadores()
        {
            var mapa = App.Traffic.ExeMapaActual();
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var salida = new List<Escuchador>();

            try
            {
                foreach (var c in NativeNet.GetConnections())
                {
                    bool escuchaTcp = c.IsTcp && c.State == 2;
                    bool udp = !c.IsTcp;
                    if (!escuchaTcp && !udp) continue;
                    if (c.LocalPort == 0) continue;
                    string dir = string.IsNullOrEmpty(c.LocalAddress) ? "0.0.0.0" : c.LocalAddress;
                    string clave = ClaveDe(dir, c.LocalPort, c.IsTcp);
                    if (!set.Add(clave)) continue;

                    string exe = mapa.TryGetValue(c.Pid, out var e) ? e : "";
                    int alcance = AlcanceDe(dir);
                    var (nombre, delicado) = Servicios.TryGetValue(c.LocalPort, out var s) ? s : ("", false);
                    salida.Add(new Escuchador
                    {
                        Exe = exe,
                        App = string.IsNullOrEmpty(exe) ? "sistema (PID " + c.Pid + ")" : Path.GetFileName(exe),
                        Direccion = dir,
                        Puerto = c.LocalPort,
                        EsTcp = c.IsTcp,
                        Alcance = alcance,
                        Servicio = nombre,
                        Delicado = delicado && alcance > 0,
                    });
                }
            }
            catch { }

            return salida.OrderByDescending(x => x.Delicado)
                         .ThenByDescending(x => x.Alcance)
                         .ThenBy(x => x.Puerto)
                         .ToList();
        }

        private static string ClaveDe(string dir, int puerto, bool tcp) =>
            (tcp ? "t" : "u") + "|" + dir + "|" + puerto;

        /// 0 localhost · 1 una_ip_de_red · 2 todas las interfaces
        public static int AlcanceDe(string dir)
        {
            if (dir == "127.0.0.1" || dir.Equals("::1", StringComparison.OrdinalIgnoreCase)) return 0;
            if (dir == "0.0.0.0" || dir == "::") return 2;
            return 1;
        }

        public static string AlcanceTexto(int alcance) => alcance switch
        {
            0 => "solo tu PC",
            2 => "en cualquier sitio",
            _ => "en tu red",
        };

        public static string NombrePuerto(int puerto) =>
            Servicios.TryGetValue(puerto, out var s) ? s.nombre : "";
    }
}
