using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using MonitorRedPCJ.Models;
using MonitorRedPCJ.Native;

namespace MonitorRedPCJ.Services.Tools
{
    /// Un equipo de la red tal y como lo recuerda el guardián. Público porque la ventana lo
    /// muestra en una lista con botones por fila.
    public class EquipoConocido
    {
        public string mac { get; set; } = "";
        public string ip { get; set; } = "";
        public string nombre { get; set; } = "";
        public string descripcion { get; set; } = "";
        public DateTime vistoUtc { get; set; }
        public bool fiable { get; set; }
    }

    internal class LanDatos
    {
        public Dictionary<string, EquipoConocido> equipos { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
        // ssid -> BSSID que PCJ ya vio para esa red
        public Dictionary<string, List<string>> wifi { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
        public bool avisarNuevos { get; set; } = true;
    }

    public class SospechaWifi
    {
        public string Ssid { get; set; } = "";
        public string BssidNueva { get; set; } = "";
        public string Senal { get; set; } = "";
    }

    /// Guardián de la LAN: avisa de equipos nuevos, de cambios de nombre en una MAC que ya
    /// conocía y de redes wifi con el nombre de la tuya pero distinta dirección de hardware
    /// (la trampa de la red gemela). Reutiliza el escáner que ya trae PCJ; no añade otro.
    public class LanGuardService
    {
        public static LanGuardService? Instancia { get; private set; }

        public event Action? Actualizado;

        private readonly object _lock = new object();
        private readonly string _fichero;
        private LanDatos _d = new();
        private bool _enganchado;
        private List<SospechaWifi> _sospechas = new();
        private DateTime _ultimaWifi = DateTime.MinValue;

        public LanGuardService()
        {
            _fichero = Path.Combine(Paths.Root, "lan.json");
            Instancia = this;
            Cargar();
        }

        public void Start()
        {
            if (_enganchado) return;
            App.Scanner.ScanCompleted += OnScan;
            _enganchado = true;
            // La primera vez que se enciende toma foto de lo que ya hay, para no convertir en
            // «dispositivo nuevo» todo lo que llevaba meses en la red.
            if (_d.equipos.Count == 0)
            {
                RegistrarSilencioso();
                return;
            }
            if (App.Scanner.Devices.Count == 0) App.Scanner.ScanAsync();
        }

        public void Stop()
        {
            if (!_enganchado) return;
            App.Scanner.ScanCompleted -= OnScan;
            _enganchado = false;
        }

        public bool AvisarNuevos
        {
            get { lock (_lock) return _d.avisarNuevos; }
            set { lock (_lock) _d.avisarNuevos = value; Guardar(); }
        }

        public List<SospechaWifi> Sospechas
        {
            get { lock (_lock) return new List<SospechaWifi>(_sospechas); }
        }

        public int Conocidos
        {
            get { lock (_lock) return _d.equipos.Count; }
        }

        public IEnumerable<EquipoConocido> Equipos()
        {
            lock (_lock) return _d.equipos.Values
                .OrderByDescending(e => e.vistoUtc)
                .Select(e => new EquipoConocido
                {
                    mac = e.mac, ip = e.ip, nombre = e.nombre,
                    descripcion = e.descripcion, vistoUtc = e.vistoUtc, fiable = e.fiable,
                })
                .ToList();
        }

        private void RegistrarSilencioso()
        {
            try
            {
                lock (_lock)
                {
                    foreach (var dev in App.Scanner.Devices)
                        _d.equipos[Clave(dev)] = Copia(dev, _d.equipos, confiar: true);
                }
                Guardar();
            }
            catch { }
        }

        private static string Clave(DeviceInfo d) =>
            string.IsNullOrEmpty(d.Mac) ? "ip:" + d.Ip : "mac:" + d.Mac;

        private static string ClaveDe(EquipoConocido e) =>
            string.IsNullOrEmpty(e.mac) ? "ip:" + e.ip : "mac:" + e.mac;

        private static EquipoConocido Copia(DeviceInfo d, Dictionary<string, EquipoConocido> mapa, bool confiar)
        {
            string k = Clave(d);
            mapa.TryGetValue(k, out var viejo);
            return new EquipoConocido
            {
                mac = d.Mac,
                ip = d.Ip,
                nombre = d.Name,
                descripcion = d.Description,
                vistoUtc = d.LastSeenUtc,
                fiable = confiar || (viejo != null && viejo.fiable),
            };
        }

        private void OnScan()
        {
            if (App.IsShuttingDown) return;
            try
            {
                var nuevos = new List<DeviceInfo>();
                var cambios = new List<string>();
                lock (_lock)
                {
                    foreach (var dev in App.Scanner.Devices)
                    {
                        string k = Clave(dev);
                        if (!_d.equipos.TryGetValue(k, out var conocido))
                        {
                            _d.equipos[k] = Copia(dev, _d.equipos, confiar: false);
                            nuevos.Add(dev);
                            continue;
                        }
                        conocido.vistoUtc = dev.LastSeenUtc;
                        if (!string.IsNullOrEmpty(dev.Ip)) conocido.ip = dev.Ip;
                        if ((conocido.nombre ?? "") != (dev.Name ?? ""))
                        {
                            cambios.Add(dev.Ip + ": pasó a llamarse «" + dev.Name + "» (antes " +
                                        (string.IsNullOrEmpty(conocido.nombre) ? "sin nombre" : conocido.nombre) + ")");
                            conocido.nombre = dev.Name;
                        }
                        if (!string.IsNullOrEmpty(dev.Mac) && !string.IsNullOrEmpty(conocido.mac) &&
                            !dev.Mac.Equals(conocido.mac, StringComparison.OrdinalIgnoreCase))
                            cambios.Add("La dirección MAC de " + dev.Ip + " cambió: ahora es " + dev.Mac);
                        conocido.mac = dev.Mac;
                    }
                }
                Guardar();

                bool avisar = AvisarNuevos;
                foreach (var d in nuevos)
                {
                    string quien = string.IsNullOrEmpty(d.Name) ? d.Ip : d.Name + " (" + d.Ip + ")";
                    App.Events.Add(EventKind.NewDevice, "Guardián de la LAN: equipo nuevo en la red",
                        quien + (string.IsNullOrEmpty(d.Mac) ? "" : " · MAC " + d.Mac) +
                        (string.IsNullOrEmpty(d.Description) ? "" : " · " + d.Description),
                        important: true);
                    if (avisar)
                        try { App.Tray?.Toast("Equipo nuevo en tu red", quien); } catch { }
                }
                foreach (var c in cambios)
                    App.Events.Add(EventKind.Info, "Guardián de la LAN: cambio en un equipo conocido", c,
                        important: true);

                Actualizado?.Invoke();
            }
            catch { }

            ComprobarWifi();
        }

        // ---------- Redes wifi gemelas ----------

        /// Lee las redes que ve el adaptador. Si una tiene el nombre de la tuya pero una BSSID
        /// que PCJ no había visto nunca, sale como sospechosa: es exactamente la trampa del
        /// punto de acceso falso.
        public void ComprobarWifi()
        {
            var sospechosas = new List<SospechaWifi>();
            try
            {
                string salida = Ejecutar("netsh.exe", "wlan show networks mode=bssid");
                if (string.IsNullOrEmpty(salida)) return;    // sin adaptador wifi o sin AutoConfig

                string ssidActual = LeerSsidConectado();
                string miSsid = !string.IsNullOrEmpty(ssidActual)
                    ? ssidActual
                    : App.Settings.Current.LastWifiSsid;
                if (!string.IsNullOrEmpty(ssidActual))
                {
                    App.Settings.Current.LastWifiSsid = ssidActual;
                    App.Settings.Save();
                }

                string? red = null;
                foreach (var lineaBruta in salida.Replace("\r", "").Split('\n'))
                {
                    string linea = lineaBruta.Trim();
                    if (linea.StartsWith("SSID", StringComparison.OrdinalIgnoreCase))
                    {
                        int ix = linea.IndexOf(':', StringComparison.Ordinal);
                        if (ix < 0) continue;
                        string resto = linea.Substring(ix + 1).Trim();
                        if (resto.All(c => !char.IsDigit(c)) && resto.Length > 0 &&
                            !resto.StartsWith("BSSID", StringComparison.OrdinalIgnoreCase))
                            red = resto;
                        else if (resto.Length > 0 && resto[0] >= '0' && resto[0] <= '9')
                        {
                            // "SSID 2 : Nombre" => el nombre va después del segundo dos puntos.
                            int ix2 = resto.IndexOf(':', StringComparison.Ordinal);
                            if (ix2 >= 0) red = resto.Substring(ix2 + 1).Trim();
                        }
                    }
                    if (red == null) continue;
                    if (!linea.StartsWith("BSSID", StringComparison.OrdinalIgnoreCase)) continue;
                    int p = linea.IndexOf(':', StringComparison.Ordinal);
                    if (p < 0) continue;
                    string bssid = linea.Substring(p + 1).Trim();
                    if (bssid.Length == 0) continue;

                    bool conocido;
                    lock (_lock)
                    {
                        if (!_d.wifi.TryGetValue(red, out var lista))
                        {
                            lista = new List<string>();
                            _d.wifi[red] = lista;
                        }
                        conocido = lista.Contains(bssid, StringComparer.OrdinalIgnoreCase);
                        if (!conocido && lista.Count < 24) lista.Add(bssid);
                    }
                    if (!conocido && !string.IsNullOrEmpty(miSsid) &&
                        red.Equals(miSsid, StringComparison.OrdinalIgnoreCase))
                        sospechosas.Add(new SospechaWifi { Ssid = red, BssidNueva = bssid });
                }
                Guardar();
            }
            catch { }

            lock (_lock)
            {
                _sospechas = sospechosas;
                _ultimaWifi = DateTime.Now;
            }
            foreach (var s in sospechosas)
                App.Events.Add(EventKind.NewDevice,
                    "Guardián de la LAN: «" + s.Ssid + "» apareció con otra dirección de acceso",
                    "BSSID nueva " + s.BssidNueva + ". Si no has añadido ningún punto de acceso, " +
                    "puede ser una red gemela: no introduzcas la contraseña en ella.",
                    important: true);
            if (sospechosas.Count > 0) Actualizado?.Invoke();
        }

        private static string LeerSsidConectado()
        {
            string salida = Ejecutar("netsh.exe", "wlan show interfaces");
            if (string.IsNullOrEmpty(salida)) return "";
            foreach (var lineaBruta in salida.Replace("\r", "").Split('\n'))
            {
                string l = lineaBruta.Trim();
                if (!l.StartsWith("SSID", StringComparison.OrdinalIgnoreCase)) continue;
                int ix = l.IndexOf(':', StringComparison.Ordinal);
                if (ix < 0 || ix + 1 >= l.Length) continue;
                string valor = l.Substring(ix + 1).Trim();
                if (valor.Length > 0 && valor[0] >= '0' && valor[0] <= '9') continue;  // "SSID 1 :"
                return valor;
            }
            return "";
        }

        private static string Ejecutar(string archivo, string argumentos)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = archivo,
                    Arguments = argumentos,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    StandardOutputEncoding = Encoding.UTF8,
                };
                using var p = Process.Start(psi);
                if (p == null) return "";
                string texto = p.StandardOutput.ReadToEnd();
                if (!p.WaitForExit(5000)) { try { p.Kill(); } catch { } }
                return texto ?? "";
            }
            catch { return ""; }
        }

        public DateTime UltimaLecturaWifi => _ultimaWifi;

        /// Marca un equipo como conocido para que no vuelva a saltar el aviso.
        public void Confiar(string mac, string ip)
        {
            lock (_lock)
            {
                string k = string.IsNullOrEmpty(mac) ? "ip:" + ip : "mac:" + mac;
                if (_d.equipos.TryGetValue(k, out var e)) { e.fiable = true; Guardar(); return; }
                _d.equipos[k] = new EquipoConocido { mac = mac ?? "", ip = ip ?? "", fiable = true, vistoUtc = DateTime.UtcNow };
            }
            Guardar();
            Actualizado?.Invoke();
        }

        /// En el panel la lista va por IP: se confía por la clave que esté guardada.
        public void ConfiarDispositivo(DeviceInfo d)
        {
            lock (_lock)
            {
                string k = Clave(d);
                if (_d.equipos.TryGetValue(k, out var e)) e.fiable = true;
                else _d.equipos[k] = new EquipoConocido
                {
                    mac = d.Mac, ip = d.Ip, nombre = d.Name, descripcion = d.Description,
                    vistoUtc = DateTime.UtcNow, fiable = true,
                };
            }
            Guardar();
            Actualizado?.Invoke();
        }

        public void OlvidarTodo()
        {
            lock (_lock) _d = new LanDatos { wifi = _d.wifi, avisarNuevos = _d.avisarNuevos };
            Guardar();
            App.Scanner.ClearDevices();
            Actualizado?.Invoke();
            App.Events.Add(EventKind.Info, "Guardián de la LAN: lista de equipos olvidada", "", important: false);
        }

        // ---------- Persistencia ----------

        private void Cargar()
        {
            try
            {
                if (!File.Exists(_fichero)) return;
                var leido = JsonSerializer.Deserialize<LanDatos>(File.ReadAllText(_fichero));
                if (leido == null) return;
                if (leido.equipos == null) leido.equipos = new(StringComparer.OrdinalIgnoreCase);
                if (leido.wifi == null) leido.wifi = new(StringComparer.OrdinalIgnoreCase);
                lock (_lock) _d = leido;
            }
            catch { }
        }

        private void Guardar()
        {
            try
            {
                Paths.Ensure();
                string json;
                lock (_lock) json = JsonSerializer.Serialize(_d);
                File.WriteAllText(_fichero, json);
            }
            catch { }
        }
    }
}
