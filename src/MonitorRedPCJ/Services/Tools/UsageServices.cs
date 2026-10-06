using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using MonitorRedPCJ.Models;
using System.Threading;

namespace MonitorRedPCJ.Services.Tools
{
    // ============================================================================
    //  Cuota de datos
    // ============================================================================

    /// Lo que se guarda en cuota.json. Todo keyed por día: la semana y el mes se suman a
    /// partir de aquí, así no hay que arreglar contadores cuando cambia el día de facturación.
    internal class CuotaDatos
    {
        public int diaReinicio { get; set; } = 1;
        public string avisadoEn { get; set; } = "";
        public Dictionary<string, long> dias { get; set; } = new();
        public Dictionary<string, Dictionary<string, long>> porApp { get; set; } = new();
    }

    /// Cuenta lo que entra y sale por día, por semana y por mes, partido por aplicación, y
    /// avisa al acercarse a la cuota que uno ponga.
    public class QuotaService
    {
        public static QuotaService? Instancia { get; private set; }

        public event Action? Actualizado;

        private readonly object _lock = new object();
        private readonly string _fichero;
        private CuotaDatos _d = new();
        private Timer? _timer;
        private long _baseRx, _baseTx;
        private readonly Dictionary<string, long> _baseApp = new(StringComparer.OrdinalIgnoreCase);
        private bool _basePuesta;

        public QuotaService()
        {
            _fichero = Path.Combine(Paths.Root, "cuota.json");
            Instancia = this;
            Cargar();
        }

        public void Start()
        {
            if (_timer != null) return;
            FijarBase();
            _timer = new Timer(_ => Vuelta(), null, TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(30));
        }

        public void Stop()
        {
            var t = Interlocked.Exchange(ref _timer, null);
            try { t?.Dispose(); } catch { }
            Guardar();
        }

        /// Punto de partida: los contadores de las interfaces siguen sumando aunque PCJ esté
        /// cerrado, así que se ancla lo que había al arrancar y solo se cuentan los deltas.
        /// Si el equipo se reinició, el contador vuelve de cero y el delta saldría negativo:
        /// en ese caso se vuelve a anclar sin sumar nada.
        private void FijarBase()
        {
            try
            {
                var (rx, tx) = Native.NativeNet.GetInterfaceOctets();
                _baseRx = rx; _baseTx = tx;
                lock (_baseApp)
                {
                    _baseApp.Clear();
                    foreach (var a in App.Traffic.GetApps())
                        _baseApp[a.ExePath] = a.TotalSent + a.TotalReceived;
                }
                _basePuesta = true;
            }
            catch { _basePuesta = false; }
        }

        private void Vuelta()
        {
            if (App.IsShuttingDown) return;
            try
            {
                if (!_basePuesta) { FijarBase(); return; }
                var (rx, tx) = Native.NativeNet.GetInterfaceOctets();
                long dRx = rx - _baseRx, dTx = tx - _baseTx;
                if (dRx < 0 || dTx < 0)          // el equipo se reinició: el contador volvió a cero
                {
                    FijarBase();
                    return;
                }
                _baseRx = rx; _baseTx = tx;
                long total = dRx + dTx;
                if (total <= 0) { ComprobarAviso(); return; }

                var porApp = DeltaPorApp();
                string hoy = DateTime.Today.ToString("yyyy-MM-dd");
                lock (_lock)
                {
                    _d.dias[hoy] = _d.dias.TryGetValue(hoy, out var v) ? v + total : total;
                    if (!_d.porApp.TryGetValue(hoy, out var mapa))
                    {
                        mapa = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                        _d.porApp[hoy] = mapa;
                    }
                    foreach (var kv in porApp)
                        mapa[kv.Key] = mapa.TryGetValue(kv.Key, out var b) ? b + kv.Value : kv.Value;
                    Poda();
                }
                Guardar();
                ComprobarAviso();
                Actualizado?.Invoke();
            }
            catch { }
        }

        /// Deltas del acumulado por aplicación desde la última vuelta.
        private Dictionary<string, long> DeltaPorApp()
        {
            var salida = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            try
            {
                lock (_baseApp)
                {
                    foreach (var a in App.Traffic.GetApps())
                    {
                        long ahora = a.TotalSent + a.TotalReceived;
                        long antes = _baseApp.TryGetValue(a.ExePath, out var b) ? b : 0;
                        long d = ahora - antes;
                        if (d <= 0) { _baseApp[a.ExePath] = ahora; continue; }
                        _baseApp[a.ExePath] = ahora;
                        salida[Path.GetFileName(a.ExePath)] = d;
                    }
                }
            }
            catch { }
            return salida;
        }

        private void Poda()
        {
            var limite = DateTime.Today.AddDays(-400);
            foreach (var k in _d.dias.Keys.Where(k =>
                     DateTime.TryParse(k, out var f) ? f < limite : false).ToList())
                _d.dias.Remove(k);
            foreach (var k in _d.porApp.Keys.Where(k =>
                     DateTime.TryParse(k, out var f) ? f < limite : false).ToList())
                _d.porApp.Remove(k);
        }

        // ---------- Lo que lee la interfaz ----------

        public long TotalDeHoy
        {
            get
            {
                lock (_lock)
                    return _d.dias.TryGetValue(DateTime.Today.ToString("yyyy-MM-dd"), out var v) ? v : 0;
            }
        }

        /// Últimos siete días, contando desde hoy hacia atrás.
        public long TotalDeUnaSemana
        {
            get
            {
                lock (_lock)
                {
                    long suma = 0;
                    for (int i = 0; i < 7; i++)
                    {
                        var k = DateTime.Today.AddDays(-i).ToString("yyyy-MM-dd");
                        if (_d.dias.TryGetValue(k, out var v)) suma += v;
                    }
                    return suma;
                }
            }
        }

        /// Mes de facturación: del día de reinicio del mes en curso hasta hoy.
        public (long bytes, string etiqueta) TotalDelMes
        {
            get
            {
                lock (_lock)
                {
                    var hoy = DateTime.Today;
                    int dia = Math.Min(Math.Max(1, _d.diaReinicio), DateTime.DaysInMonth(hoy.Year, hoy.Month));
                    var inicio = new DateTime(hoy.Year, hoy.Month, dia);
                    if (inicio > hoy) inicio = inicio.AddMonths(-1);
                    long suma = 0;
                    for (var d = inicio; d <= hoy; d = d.AddDays(1))
                        if (_d.dias.TryGetValue(d.ToString("yyyy-MM-dd"), out var v)) suma += v;
                    return (suma, inicio.ToString("dd MMM", CulturaEs()) + " – " +
                            hoy.ToString("dd MMM", CulturaEs()));
                }
            }
        }

        private static CultureInfo CulturaEs() => CultureInfo.GetCultureInfo("es");

        /// Las aplicaciones que más han gastado dentro del mes de facturación.
        public List<(string app, long bytes)> TopDelMes(int cuantas = 12)
        {
            var hoy = DateTime.Today;
            int dia;
            lock (_lock) dia = Math.Min(Math.Max(1, _d.diaReinicio), DateTime.DaysInMonth(hoy.Year, hoy.Month));
            var inicio = new DateTime(hoy.Year, hoy.Month, dia);
            if (inicio > hoy) inicio = inicio.AddMonths(-1);

            var total = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            lock (_lock)
            {
                for (var dd = inicio; dd <= hoy; dd = dd.AddDays(1))
                {
                    if (!_d.porApp.TryGetValue(dd.ToString("yyyy-MM-dd"), out var mapa)) continue;
                    foreach (var kv in mapa)
                        total[kv.Key] = total.TryGetValue(kv.Key, out var v) ? v + kv.Value : kv.Value;
                }
            }
            return total.OrderByDescending(kv => kv.Value).Take(cuantas)
                       .Select(kv => (kv.Key, kv.Value)).ToList();
        }

        public List<(string app, long bytes)> TopDeHoy(int cuantas = 12)
        {
            string k = DateTime.Today.ToString("yyyy-MM-dd");
            lock (_lock)
            {
                if (!_d.porApp.TryGetValue(k, out var mapa)) return new List<(string, long)>();
                return mapa.OrderByDescending(kv => kv.Value).Take(cuantas)
                           .Select(kv => (kv.Key, kv.Value)).ToList();
            }
        }

        public int DiaDeReinicio
        {
            get { lock (_lock) return _d.diaReinicio; }
            set { lock (_lock) _d.diaReinicio = Math.Min(28, Math.Max(1, value)); Guardar(); }
        }

        public double PorcentajeDeCuota
        {
            get
            {
                double gb = App.Settings.Current.CuotaMensualGB;
                if (gb <= 0) return 0;
                return Math.Min(100.0, TotalDelMes.bytes / (gb * 1024.0 * 1024.0 * 1024.0) * 100.0);
            }
        }

        private void ComprobarAviso()
        {
            double gb = App.Settings.Current.CuotaMensualGB;
            if (gb <= 0) return;
            int umbral = Math.Min(100, Math.Max(10, App.Settings.Current.AvisoCuotaPorCiento));
            string clave = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).ToString("yyyy-MM");
            lock (_lock) { if (_d.avisadoEn == clave) return; }
            if (PorcentajeDeCuota < umbral) return;
            lock (_lock) _d.avisadoEn = clave;
            Guardar();
            long total = TotalDelMes.bytes;
            App.Events.Add(EventKind.Info,
                "Cuota de datos: vas por el " + PorcentajeDeCuota.ToString("0") + " % de la cuota de " +
                gb.ToString("0.#") + " GB",
                "Llevas " + Format.Bytes(total) + " en el periodo " + TotalDelMes.etiqueta +
                ". Revisa «Cuota de datos» en Más herramientas.", important: true);
            try { App.Tray?.Toast("Cuota de datos", PorcentajeDeCuota.ToString("0") + " % consumido (" +
                  Format.Bytes(total) + " de " + gb.ToString("0.#") + " GB)."); } catch { }
        }

        /// Borra las cuentas guardadas (el botón «Empezar de cero»).
        public void EmpezarDeCero()
        {
            lock (_lock) { _d = new CuotaDatos { diaReinicio = _d.diaReinicio }; }
            Guardar();
            FijarBase();
            Actualizado?.Invoke();
            App.Events.Add(EventKind.Info, "Cuota de datos: cuentas empezadas de cero", "", important: false);
        }

        public IReadOnlyDictionary<string, long> Dias()
        {
            lock (_lock) return new Dictionary<string, long>(_d.dias);
        }

        // ---------- Persistencia ----------

        private void Cargar()
        {
            try
            {
                if (!File.Exists(_fichero)) return;
                var leido = JsonSerializer.Deserialize<CuotaDatos>(File.ReadAllText(_fichero));
                if (leido == null) return;
                lock (_lock) _d = leido;
                if (_d.dias == null) _d.dias = new();
                if (_d.porApp == null) _d.porApp = new();
            }
            catch { }
        }

        private void Guardar()
        {
            try
            {
                CuotaDatos copia;
                lock (_lock)
                {
                    copia = new CuotaDatos
                    {
                        diaReinicio = _d.diaReinicio,
                        avisadoEn = _d.avisadoEn,
                        dias = new Dictionary<string, long>(_d.dias),
                        porApp = _d.porApp.ToDictionary(kv => kv.Key,
                            kv => new Dictionary<string, long>(kv.Value, StringComparer.OrdinalIgnoreCase)),
                    };
                }
                Paths.Ensure();
                File.WriteAllText(_fichero, JsonSerializer.Serialize(copia));
            }
            catch { }
        }
    }

    // ============================================================================
    //  Máquina del tiempo
    // ============================================================================

    /// Una foto de 30 segundos: lo que se movió en total y por aplicación.
    public class Muestra
    {
        public DateTime HoraUtc { get; set; }
        public long Rx, Tx;
        public Dictionary<string, long[]> Apps { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// Guarda muestras del tráfico en histórico.jsonl y deja desplazarse por ellas.
    /// Solo escribe deltas: si una app no se movió en ese tramo, no aparece.
    public class TimeMachineService
    {
        public static TimeMachineService? Instancia { get; private set; }

        public event Action? Actualizado;

        private readonly object _lock = new object();
        private readonly string _fichero;
        private Timer? _timer;
        private readonly Dictionary<string, long[]> _anterior = new(StringComparer.OrdinalIgnoreCase);
        private bool _anclaPuesta;
        private DateTime _ultimaLimpieza = DateTime.MinValue;

        public TimeMachineService()
        {
            _fichero = Path.Combine(Paths.Root, "historico-muestras.jsonl");
            Instancia = this;
        }

        public void Start()
        {
            if (_timer != null) return;
            _anclaPuesta = false;
            _timer = new Timer(_ => Vuelta(), null, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(30));
        }

        public void Stop()
        {
            var t = Interlocked.Exchange(ref _timer, null);
            try { t?.Dispose(); } catch { }
            _anclaPuesta = false;
        }

        public bool Trabajando => _timer != null;

        private void Vuelta()
        {
            if (App.IsShuttingDown) return;
            // Con el incógnito puesto no se escribe ninguna muestra, igual que con el resto
            // del histórico.
            if (Recording.Incognito) return;
            try
            {
                var apps = App.Traffic.GetApps();
                var actual = new Dictionary<string, long[]>(StringComparer.OrdinalIgnoreCase);
                foreach (var a in apps)
                    actual[Path.GetFileName(a.ExePath)] = new[] { a.TotalReceived, a.TotalSent };

                var m = new Muestra { HoraUtc = DateTime.UtcNow };

                // Totales del tramo: delta de los contadores de las interfaces.
                var (rx, tx) = Instantaneos();
                m.Rx = rx; m.Tx = tx;

                lock (_lock)
                {
                    if (_anclaPuesta)
                    {
                        foreach (var kv in actual)
                        {
                            if (!_anterior.TryGetValue(kv.Key, out var antes)) continue;
                            long dr = kv.Value[0] - antes[0], dt = kv.Value[1] - antes[1];
                            if (dr > 0 || dt > 0) m.Apps[kv.Key] = new[] { Math.Max(0, dr), Math.Max(0, dt) };
                        }
                    }
                    _anterior.Clear();
                    foreach (var kv in actual) _anterior[kv.Key] = kv.Value;
                    _anclaPuesta = true;
                }

                Escribir(m);
                Actualizado?.Invoke();
                LimpiezaSiProcede();
            }
            catch { }
        }

        private long _ultimoRx, _ultimoTx;

        private (long rx, long tx) Instantaneos()
        {
            try
            {
                var (rx, tx) = Native.NativeNet.GetInterfaceOctets();
                long drx = rx - _ultimoRx, dtx = tx - _ultimoTx;
                _ultimoRx = rx; _ultimoTx = tx;
                if (drx < 0 || dtx < 0) return (0, 0);   // reinicio del contador
                return (drx, dtx);
            }
            catch { return (0, 0); }
        }

        private void Escribir(Muestra m)
        {
            try
            {
                Paths.Ensure();
                var linea = new Dictionary<string, object?>
                {
                    ["t"] = m.HoraUtc.ToString("o"),
                    ["rx"] = m.Rx,
                    ["tx"] = m.Tx,
                    ["a"] = m.Apps.ToDictionary(kv => kv.Key, kv => new[] { kv.Value[0], kv.Value[1] }),
                };
                File.AppendAllText(_fichero, JsonSerializer.Serialize(linea) + Environment.NewLine);
            }
            catch { }
        }

        private void LimpiezaSiProcede()
        {
            if ((DateTime.Now - _ultimaLimpieza).TotalHours < 6) return;
            _ultimaLimpieza = DateTime.Now;
            try
            {
                int dias = Math.Max(1, App.Settings.Current.HistoryRetentionDays);
                var corte = DateTime.UtcNow.AddDays(-dias);
                var lineas = File.ReadLines(_fichero).Where(l => l.Trim().Length > 0).ToList();
                var keeps = lineas.Where(l => { try { return Fecha(l) >= corte; } catch { return false; } }).ToList();
                if (keeps.Count == lineas.Count) return;
                File.WriteAllText(_fichero, string.Join(Environment.NewLine, keeps) +
                                           (keeps.Count > 0 ? Environment.NewLine : ""));
            }
            catch { }
        }

        private static DateTime Fecha(string linea)
        {
            using var doc = JsonDocument.Parse(linea);
            return doc.RootElement.GetProperty("t").GetDateTime();
        }

        /// Muestras de las últimas `horas`, más recientes primero al revés (orden cronológico).
        public List<Muestra> Muestras(double horas = 8)
        {
            var salida = new List<Muestra>();
            try
            {
                if (!File.Exists(_fichero)) return salida;
                var desde = DateTime.UtcNow.AddHours(-horas);
                foreach (var linea in File.ReadLines(_fichero))
                {
                    if (linea.Trim().Length == 0) continue;
                    try
                    {
                        using var doc = JsonDocument.Parse(linea);
                        var root = doc.RootElement;
                        var t = root.GetProperty("t").GetDateTime();
                        if (t < desde) continue;
                        var m = new Muestra
                        {
                            HoraUtc = t,
                            Rx = root.GetProperty("rx").GetInt64(),
                            Tx = root.GetProperty("tx").GetInt64(),
                        };
                        if (root.TryGetProperty("a", out var apps))
                            foreach (var kv in apps.EnumerateObject())
                            {
                                var v = kv.Value;
                                m.Apps[kv.Name] = new[] { v[0].GetInt64(), v[1].GetInt64() };
                            }
                        salida.Add(m);
                    }
                    catch { }
                }
            }
            catch { }
            return salida;
        }

        /// Última muestra guardada, para saber si la herramienta lleva tiempo parada.
        public DateTime? UltimaHora
        {
            get
            {
                try
                {
                    if (!File.Exists(_fichero)) return null;
                    DateTime? ultimo = null;
                    foreach (var linea in File.ReadLines(_fichero))
                    {
                        if (linea.Trim().Length == 0) continue;
                        try { var f = Fecha(linea); if (ultimo == null || f > ultimo) ultimo = f; }
                        catch { }
                    }
                    return ultimo?.ToLocalTime();
                }
                catch { return null; }
            }
        }

        public void Borrar()
        {
            try { if (File.Exists(_fichero)) File.Delete(_fichero); } catch { }
            _anclaPuesta = false;
            Actualizado?.Invoke();
        }
    }
}
