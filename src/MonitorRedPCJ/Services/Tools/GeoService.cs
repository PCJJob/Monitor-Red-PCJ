using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MonitorRedPCJ.Models;

namespace MonitorRedPCJ.Services.Tools
{
    public class PaisGrupo
    {
        public string Codigo { get; set; } = "";
        public string Nombre { get; set; } = "";
        public int Destinos { get; set; }
        public int Conexiones { get; set; }
        public List<string> Apps { get; set; } = new();
        public List<string> Ips { get; set; } = new();
        public bool EsLocal { get; set; }
    }

    /// Tráfico por país: asocia cada IP destino a un país y agrupa.
    ///
    /// Dos formas de saber el país, y la Privada va primero:
    ///   · geoipv4.csv en la carpeta de datos (IP inicial, IP final, código) => cero red extra.
    ///   · si no existe y el usuario deja activada la consulta en línea, se pregunta una vez por
    ///     IP y se guarda en geo-cache.json. La consulta sale sin cifrar a ip-api.com: por eso el
    ///     interruptor está a la vista y no escondido.
    public class GeoService
    {
        public static GeoService? Instancia { get; private set; }

        public event Action? Actualizado;

        private readonly object _lock = new object();
        private readonly Dictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _pendientes = new(StringComparer.OrdinalIgnoreCase);
        // Cuántas veces se preguntó cada IP sin que el servidor contestara. No se guarda en
        // disco: es para no insistir dentro de una misma sesión.
        private readonly Dictionary<string, int> _intentos = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _paisesVistos = new(StringComparer.OrdinalIgnoreCase);
        private readonly string _ficheroCache;
        private readonly string _ficheroVistos;
        private Timer? _timer;
        private int _consultando;
        private volatile bool _avisarNuevos;

        // Base local: rangos ordenados por IP inicial.
        private Rango[] _rangos = Array.Empty<Rango>();
        private bool _baseLocal;

        private struct Rango { public long Ini; public long Fin; public string Codigo; }

        public GeoService()
        {
            _ficheroCache = Path.Combine(Paths.Root, "geo-cache.json");
            _ficheroVistos = Path.Combine(Paths.Root, "geo-vistos.json");
            Instancia = this;
            CargarCache();
            CargarVistos();
            CargarBaseLocal();
        }

        public void Start()
        {
            if (_timer != null) return;
            _timer = new Timer(_ => Vuelta(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(15));
        }

        public void Stop()
        {
            var t = Interlocked.Exchange(ref _timer, null);
            try { t?.Dispose(); } catch { }
            GuardarCache();
            GuardarVistos();
        }

        public bool BaseLocalCargada => _baseLocal;
        public int RangosEnBase => _rangos.Length;

        public bool AvisarNuevos
        {
            get => _avisarNuevos;
            set { _avisarNuevos = value; }
        }

        private void Vuelta()
        {
            if (App.IsShuttingDown) return;
            try
            {
                EncolarConocidas();
                if (SePuedePreguntarFuera) LoteEnLinea();
            }
            catch { }
        }

        /// Salir del equipo hay que querer: la consulta manda la IP destino a un tercero sin
        /// cifrar, así que solo se hace con el ajuste encendido y fuera del modo incógnito.
        public bool SePuedePreguntarFuera =>
            App.Settings.Current.PaisesConsultarOnline && !App.Settings.Current.IncognitoMode;

        private void EncolarConocidas()
        {
            var radar = RadarService.Instancia;
            if (radar == null) return;
            foreach (var d in radar.DestinosActuales())
                if (!string.IsNullOrEmpty(d.Ip)) Encolar(d.Ip);
        }

        /// Fuerza la resolución de todos los destinos que faltan (el botón «Resolver ahora» de
        /// la ventana). Devuelve false si no se puede salir a preguntar: el botón lo avisa en
        /// vez de quedarse callado, que es lo que parecía estar pasando.
        public bool ResolverAhora()
        {
            if (!SePuedePreguntarFuera)
            {
                UltimoError = App.Settings.Current.IncognitoMode
                    ? "modo incógnito puesto: PCJ no sale a preguntar nada"
                    : "está apagado «Consultar ip-api.com»";
                EncolarConocidas();
                Actualizado?.Invoke();
                return false;
            }
            EncolarConocidas();
            var radar = RadarService.Instancia;
            if (radar != null)
                foreach (var d in radar.DestinosHistoricos())
                    if (!string.IsNullOrEmpty(d.Ip)) Encolar(d.Ip);
            // Fuera del hilo de la interfaz: cien direcciones en una tanda tardan lo suyo, y
            // la ventana no puede quedarse esperando a un servidor.
            Task.Run(() => LoteEnLinea(500));
            Actualizado?.Invoke();
            return true;
        }

        private void Encolar(string ip)
        {
            if (TrafficService.IsLocalOrPrivate(ip)) return;
            lock (_lock)
            {
                if (_cache.ContainsKey(ip)) return;
                if (BuscarEnBase(ip, out _)) { _cache[ip] = BuscarEnBaseObligatoria(ip); return; }
                // Sin permiso para salir, la cola no crece: se llena al encender el ajuste.
                if (!SePuedePreguntarFuera) return;
                _pendientes.Add(ip);
            }
        }

        public string CodigoDe(string ip)
        {
            if (string.IsNullOrEmpty(ip)) return "";
            if (TrafficService.IsLocalOrPrivate(ip)) return "LAN";
            lock (_lock)
            {
                if (_cache.TryGetValue(ip, out var c)) return c;
            }
            if (BuscarEnBase(ip, out var codigo))
            {
                lock (_lock) _cache[ip] = codigo;
                return codigo;
            }
            lock (_lock) { if (SePuedePreguntarFuera) _pendientes.Add(ip); }
            return "";
        }

        // ---------- Agregación para la ventana ----------

        public List<PaisGrupo> Paises()
        {
            var grupos = new Dictionary<string, PaisGrupo>(StringComparer.OrdinalIgnoreCase);
            var radar = RadarService.Instancia;
            if (radar == null) return new List<PaisGrupo>();

            foreach (var d in radar.DestinosActuales().Concat(radar.DestinosHistoricos()))
            {
                if (string.IsNullOrEmpty(d.Ip)) continue;
                string code = CodigoDe(d.Ip);
                if (string.IsNullOrEmpty(code)) code = "??";
                if (!grupos.TryGetValue(code, out var g))
                {
                    g = new PaisGrupo
                    {
                        Codigo = code,
                        Nombre = NombreDe(code),
                        EsLocal = code == "LAN",
                    };
                    grupos[code] = g;
                }
                g.Conexiones++;
                if (!g.Ips.Contains(d.Ip)) { g.Ips.Add(d.Ip); g.Destinos++; }
                if (!string.IsNullOrEmpty(d.App) && !g.Apps.Contains(d.App, StringComparer.OrdinalIgnoreCase))
                    g.Apps.Add(d.App);
            }

            // «Sin resolver» al final aunque sea el montón más grande: si no, la primera fila
            // de la herramienta era un aviso de lo que falta en vez de lo que hay.
            var lista = grupos.Values
                .OrderBy(x => x.Codigo == "??")
                .ThenByDescending(x => x.Destinos)
                .ThenBy(x => x.Codigo).ToList();
            AnotarEstrenos(lista);
            return lista;
        }

        private void AnotarEstrenos(List<PaisGrupo> lista)
        {
            foreach (var g in lista)
            {
                if (g.Codigo == "??") continue;
                bool nuevo;
                lock (_lock) nuevo = _paisesVistos.Add(g.Codigo);
                if (!nuevo || g.Codigo == "LAN") continue;
                GuardarVistos();
                if (!_avisarNuevos) continue;
                App.Events.Add(EventKind.Info,
                    "Tráfico por país: primer contacto con " + g.Nombre,
                    g.Destinos + " destinos" + (g.Apps.Count > 0 ? " · " + string.Join(", ", g.Apps.Take(5)) : ""),
                    important: true);
                try { App.Tray?.Toast("País nuevo", "PCJ vio tráfico con " + g.Nombre + " por primera vez."); }
                catch { }
            }
        }

        // ---------- Base local ----------

        private void CargarBaseLocal()
        {
            try
            {
                string ruta = Path.Combine(Paths.Root, "geoipv4.csv");
                if (!File.Exists(ruta)) { _baseLocal = false; return; }
                var lista = new List<Rango>();
                foreach (var linea in File.ReadLines(ruta))
                {
                    var partes = linea.Split(new[] { ',', ';', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (partes.Length < 3) continue;
                    if (!TryParseIp(partes[0], out long ini) || !TryParseIp(partes[1], out long fin)) continue;
                    if (fin < ini) continue;
                    lista.Add(new Rango { Ini = ini, Fin = fin, Codigo = partes[2].Trim().ToUpperInvariant() });
                }
                _rangos = lista.OrderBy(r => r.Ini).ToArray();
                _baseLocal = _rangos.Length > 0;
            }
            catch { _baseLocal = false; }
        }

        private bool BuscarEnBase(string ip, out string codigo)
        {
            codigo = "";
            if (!_baseLocal || _rangos.Length == 0) return false;
            if (!TryParseIp(ip, out long v)) return false;
            int lo = 0, hi = _rangos.Length - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                var r = _rangos[mid];
                if (v < r.Ini) hi = mid - 1;
                else if (v > r.Fin) lo = mid + 1;
                else { codigo = r.Codigo; return true; }
            }
            return false;
        }

        private string BuscarEnBaseObligatoria(string ip)
        {
            return BuscarEnBase(ip, out var c) ? c : "";
        }

        public static bool TryParseIp(string texto, out long valor)
        {
            valor = 0;
            if (System.Net.IPAddress.TryParse(texto.Trim(), out var ip))
            {
                var b = ip.GetAddressBytes();
                if (b.Length != 4) return false;
                valor = ((long)b[0] << 24) | ((long)b[1] << 16) | ((long)b[2] << 8) | b[3];
                return true;
            }
            // Algunas bases traen la IP como número entero.
            return long.TryParse(texto.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out valor);
        }

        // ---------- Consulta en línea ----------

        private static readonly HttpClient Http = Crear();

        private static HttpClient Crear()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("MonitorRedPCJ/1.4");
            return c;
        }

        private void LoteEnLinea(int max = 100)
        {
            if (!SePuedePreguntarFuera) return;
            if (Interlocked.CompareExchange(ref _consultando, 1, 0) != 0) return;
            try
            {
                List<string> cola;
                lock (_lock)
                {
                    cola = _pendientes.Take(max).ToList();
                    foreach (var ip in cola) _pendientes.Remove(ip);
                }
                if (cola.Count == 0) { UltimoError = "la cola estaba vacía, no había nada que preguntar"; return; }

                // ip-api deja pedir cien direcciones de golpe en una sola llamada, y eso cuenta
                // como quince consultas de su límite gratis. Preguntando de una en una se
                // reventaba el límite y contestaba «denied» a todo: por eso la caché se quedaba
                // a cero. Se va de cien en cien, con su respiro entre tandas.
                int procesasHasta = 0;
                for (int desde = 0; desde < cola.Count; desde += 100)
                {
                    if (App.IsShuttingDown) break;
                    var tanda = cola.Skip(desde).Take(100).ToList();
                    var (codigos, contesto) = ConsultarTanda(tanda);
                    procesasHasta = desde + tanda.Count;
                    for (int i = 0; i < tanda.Count; i++)
                    {
                        string? c = i < codigos.Count ? codigos[i] : null;
                        if (!string.IsNullOrEmpty(c)) { lock (_lock) _cache[tanda[i]] = c; continue; }
                        // Aquí está la diferencia que la versión anterior no hacía: si el servidor
                        // no contestó —el firewall cortando la salida, el wifi caído, un 503— la
                        // dirección vuelve a la cola sin gastar intento. Lo de "sin país" solo se
                        // da por dicho cuando el servidor sí contestó y no traía país. Si no, una
                        // tarde con el bloqueador puesto bastaba para dejar las 300 direcciones
                        // sentenciadas a "Sin resolver" para siempre.
                        lock (_lock)
                        {
                            if (!contesto) { _pendientes.Add(tanda[i]); continue; }
                            _intentos.TryGetValue(tanda[i], out int n);
                            if (n + 1 >= 3) { _intentos.Remove(tanda[i]); _cache[tanda[i]] = ""; }
                            else { _intentos[tanda[i]] = n + 1; _pendientes.Add(tanda[i]); }
                        }
                    }
                    if (!contesto) break;   // sin respuesta, insistir tanda a tanda no sirve
                    if (desde + 100 < cola.Count) Thread.Sleep(2500);
                }
                // Lo que se sacó de la cola y no llegó a preguntarse (porque se paró el programa
                // o porque la primera tanda ya falló) no se puede quedar en el aire: de vuelta.
                if (procesasHasta < cola.Count)
                    lock (_lock)
                        foreach (var ip in cola.Skip(procesasHasta)) _pendientes.Add(ip);
                GuardarCache();
                Actualizado?.Invoke();
            }
            catch (Exception ex) { UltimoError = "lote: " + ex.GetType().Name + ": " + ex.Message; }
            finally { Interlocked.Exchange(ref _consultando, 0); }
        }

        /// Una sola petición POST con las IPs dentro. Devuelve un código por cada una, en el
        /// mismo orden (null donde no lo hubo) y si el servidor contestó de verdad.
        private static (List<string?> codigos, bool contesto) ConsultarTanda(List<string> ips)
        {
            var salida = new List<string?>(ips.Count);
            for (int i = 0; i < ips.Count; i++) salida.Add(null);
            try
            {
                var peticion = new HttpRequestMessage(HttpMethod.Post, "http://ip-api.com/batch?fields=status,countryCode")
                {
                    Content = new StringContent(JsonSerializer.Serialize(ips),
                                                 Encoding.UTF8, "application/json"),
                };
                var tarea = Http.SendAsync(peticion);
                if (!tarea.Wait(TimeSpan.FromSeconds(14)))
                {
                    UltimoError = "el servidor tardó más de 14 segundos";
                    return (salida, false);
                }
                using var respuesta = tarea.Result;
                if (!respuesta.IsSuccessStatusCode)
                {
                    UltimoError = "el servidor contestó " + (int)respuesta.StatusCode + " " +
                                  respuesta.StatusCode;
                    return (salida, false);
                }
                string texto = respuesta.Content.ReadAsStringAsync().Result;
                using var doc = JsonDocument.Parse(texto);
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                {
                    UltimoError = "respuesta rara: " + (texto.Length > 90 ? texto.Substring(0, 90) : texto);
                    return (salida, false);
                }
                int n = 0;
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    if (n >= ips.Count) break;
                    // Solo vale la respuesta buena. Un «fail» —el típico «denied» por ritmo o
                    // una IP reservada que el servicio no conoce— deja el hueco en null y la
                    // dirección vuelve a la cola, en vez de quedarse sin país para siempre.
                    if (el.TryGetProperty("status", out var s) && s.GetString() == "success" &&
                        el.TryGetProperty("countryCode", out var cc))
                        salida[n] = (cc.GetString() ?? "").ToUpperInvariant();
                    n++;
                }
                // Si la respuesta venía corta, lo que falta no es "sin país": es que no se pidió
                // bien. Se trata como fallo de transporte y se reintenta entero.
                return (salida, n >= ips.Count);
            }
            catch (Exception ex)
            {
                UltimoError = ex.GetType().Name + ": " + ex.Message;
            }
            return (salida, false);
        }

        /// Qué fue lo último que impidió saber un país, para poder decirlo en pantalla y en la
        /// comprobación automática en vez de dejar la lista muda.
        public static string UltimoError = "";

        // ---------- Nombres ----------

        private static readonly Dictionary<string, string> Nombres = new(StringComparer.OrdinalIgnoreCase)
        {
            { "LAN", "Tu red local" },
            { "??", "Sin resolver" },
            { "US", "Estados Unidos" }, { "CA", "Canadá" }, { "MX", "México" },
            { "GB", "Reino Unido" }, { "IE", "Irlanda" }, { "DE", "Alemania" }, { "FR", "Francia" },
            { "ES", "España" }, { "IT", "Italia" }, { "PT", "Portugal" }, { "NL", "Países Bajos" },
            { "BE", "Bélgica" }, { "LU", "Luxemburgo" }, { "AT", "Austria" }, { "CH", "Suiza" },
            { "SE", "Suecia" }, { "NO", "Noruega" }, { "DK", "Dinamarca" }, { "FI", "Finlandia" },
            { "IS", "Islandia" }, { "PL", "Polonia" }, { "CZ", "Chequia" }, { "SK", "Eslovaquia" },
            { "HU", "Hungría" }, { "RO", "Rumanía" }, { "BG", "Bulgaria" }, { "GR", "Grecia" },
            { "HR", "Croacia" }, { "SI", "Eslovenia" }, { "RS", "Serbia" }, { "BA", "Bosnia" },
            { "MK", "Macedonia del Norte" }, { "AL", "Albania" }, { "ME", "Montenegro" },
            { "MD", "Moldavia" }, { "UA", "Ucrania" }, { "BY", "Bielorrusia" }, { "RU", "Rusia" },
            { "EE", "Estonia" }, { "LV", "Letonia" }, { "LT", "Lituania" }, { "CY", "Chipre" },
            { "MT", "Malta" }, { "IL", "Israel" }, { "TR", "Turquía" }, { "AE", "Emiratos Árabes" },
            { "SA", "Arabia Saudí" }, { "QA", "Catar" }, { "KW", "Kuwait" }, { "EG", "Egipto" },
            { "MA", "Marruecos" }, { "DZ", "Argelia" }, { "TN", "Túnez" }, { "NG", "Nigeria" },
            { "KE", "Kenia" }, { "ZA", "Sudáfrica" }, { "ET", "Etiopía" }, { "GH", "Ghana" },
            { "CO", "Colombia" }, { "AR", "Argentina" }, { "CL", "Chile" }, { "BR", "Brasil" },
            { "PE", "Perú" }, { "VE", "Venezuela" }, { "EC", "Ecuador" }, { "UY", "Uruguay" },
            { "PY", "Paraguay" }, { "BO", "Bolivia" }, { "CR", "Costa Rica" }, { "PA", "Panamá" },
            { "DO", "República Dominicana" }, { "GT", "Guatemala" }, { "CU", "Cuba" },
            { "CN", "China" }, { "HK", "Hong Kong" }, { "TW", "Taiwán" }, { "JP", "Japón" },
            { "KR", "Corea del Sur" }, { "KP", "Corea del Norte" }, { "SG", "Singapur" },
            { "MY", "Malasia" }, { "TH", "Tailandia" }, { "VN", "Vietnam" }, { "ID", "Indonesia" },
            { "PH", "Filipinas" }, { "IN", "India" }, { "PK", "Pakistán" }, { "BD", "Bangladés" },
            { "LK", "Sri Lanka" }, { "NP", "Nepal" }, { "KZ", "Kazajistán" }, { "UZ", "Uzbekistán" },
            { "AU", "Australia" }, { "NZ", "Nueva Zelanda" }, { "FJ", "Fiyi" },
            { "SV", "El Salvador" }, { "HN", "Honduras" }, { "NI", "Nicaragua" }, { "JM", "Jamaica" },
            { "TT", "Trinidad y Tobago" }, { "AO", "Angola" },
            { "MZ", "Mozambique" }, { "TZ", "Tanzania" }, { "UG", "Uganda" }, { "SN", "Senegal" },
            { "CM", "Camerún" }, { "CI", "Costa de Marfil" }, { "JO", "Jordania" }, { "LB", "Líbano" },
            { "IR", "Irán" }, { "IQ", "Iraq" }, { "OM", "Omán" }, { "GE", "Georgia" },
            { "AM", "Armenia" }, { "AZ", "Azerbaiyán" },
        };

        public static string NombreDe(string codigo)
        {
            if (string.IsNullOrEmpty(codigo)) return "Sin resolver";
            if (Nombres.TryGetValue(codigo, out var n)) return n;
            try
            {
                if (codigo.Length == 2)
                {
                    var r = new RegionInfo(codigo);
                    return r.EnglishName;
                }
            }
            catch { }
            return codigo;
        }

        // ---------- Persistencia ----------

        private void CargarCache()
        {
            try
            {
                if (!File.Exists(_ficheroCache)) return;
                var leido = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_ficheroCache));
                if (leido == null) return;
                int envenenadas = 0;
                lock (_lock)
                {
                    _cache.Clear();
                    foreach (var kv in leido)
                    {
                        // Las cadenas vacías guardadas por la versión anterior eran negativas
                        // falsas: se preguntaba IP por IP, ip-api cortaba por exceso de ritmo y
                        // PCJ lo guardaba como si la dirección no tuviera país. Se descartan para
                        // que se vuelvan a preguntar, ahora de cien en cien.
                        if (string.IsNullOrEmpty(kv.Value)) { envenenadas++; continue; }
                        _cache[kv.Key] = kv.Value;
                    }
                }
                if (envenenadas > 0) GuardarCache();
            }
            catch { }
        }

        private void GuardarCache()
        {
            try
            {
                Dictionary<string, string> copia;
                lock (_lock) copia = new Dictionary<string, string>(_cache);
                Paths.Ensure();
                File.WriteAllText(_ficheroCache, JsonSerializer.Serialize(copia));
            }
            catch { }
        }

        private void CargarVistos()
        {
            try
            {
                if (!File.Exists(_ficheroVistos)) return;
                var leido = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_ficheroVistos));
                if (leido == null) return;
                lock (_lock) foreach (var c in leido) _paisesVistos.Add(c);
            }
            catch { }
        }

        private void GuardarVistos()
        {
            try
            {
                List<string> copia;
                lock (_lock) copia = _paisesVistos.ToList();
                Paths.Ensure();
                File.WriteAllText(_ficheroVistos, JsonSerializer.Serialize(copia));
            }
            catch { }
        }

        /// Borra lo consultado (el botón «Quitar la caché»).
        public void Limpiar()
        {
            lock (_lock) { _cache.Clear(); _pendientes.Clear(); }
            try { if (File.Exists(_ficheroCache)) File.Delete(_ficheroCache); } catch { }
            Actualizado?.Invoke();
        }

        public void OlvidarVistos()
        {
            lock (_lock) _paisesVistos.Clear();
            GuardarVistos();
        }

        /// Vacía la cola sin tocar la caché: es lo que se hace al apagar el permiso de salir,
        /// para que no queden direcciones esperando a preguntarse a escondidas.
        public void OlvidarCola()
        {
            lock (_lock) _pendientes.Clear();
        }

        public int EnCache
        {
            get { lock (_lock) return _cache.Count(kv => !string.IsNullOrEmpty(kv.Value)); }
        }

        public int EnCola
        {
            get { lock (_lock) return _pendientes.Count; }
        }
    }
}
