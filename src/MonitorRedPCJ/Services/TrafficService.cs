using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MonitorRedPCJ.Models;
using MonitorRedPCJ.Native;

namespace MonitorRedPCJ.Services
{
    // Núcleo del monitor: sondea conexiones por proceso cada segundo, acumula bytes por app,
    // detecta primeras conexiones y dispara alertas.
    public class TrafficService
    {
        private readonly SettingsService _settings;
        private readonly EventStore _events;
        private readonly FirewallService _firewall;
        private readonly JsonStore<Dictionary<string, TrackedApp>> _appsStore =
            new JsonStore<Dictionary<string, TrackedApp>>(Paths.AppsFile);

        private readonly object _lock = new object();
        private Dictionary<string, TrackedApp> _apps;
        private readonly HashSet<string> _seenPairs = new HashSet<string>(); // exe|remoteIp
        private readonly Dictionary<string, HashSet<string>> _appHosts = new();

        private long _lastRx, _lastTx;
        private DateTime _lastPollUtc = DateTime.UtcNow;
        private bool _firstSample = true;
        private long _sessionRx, _sessionTx;

        private Timer? _timer;
        private int _polling;
        private int _stopping;

        public event Action? DataUpdated;
        public event Action<TrackedApp, string>? FirstConnectionDetected;

        // Con el bloqueo total activo un intento de salida cortado NO deja rastro en la tabla
        // TCP (lo comprobamos: la app bloqueada no aparece nunca), así que la única señal de
        // que un programa quiere internet es que el usuario lo acaba de abrir.
        public event Action<TrackedApp>? BlockedLaunchDetected;

        private readonly HashSet<string> _runningExes = new(StringComparer.OrdinalIgnoreCase);

        // Nombres de todos los procesos del último repaso, por si la ruta de alguno no se
        // pudo leer. Se guarda junto con _runningExes para poder contestar a «¿está en
        // marcha?» sin volver a enumerar los procesos.
        private HashSet<string> _runningNames = new(StringComparer.OrdinalIgnoreCase);
        // Snapshot del repaso anterior: la señal de "se acaba de abrir" es aparecer en este y
        // no estar en aquel. Con el viejo "_launchSeen" (que nunca se olvidaba) cerrar y volver
        // a abrir un programa no volvía a preguntar jamás.
        private readonly HashSet<string> _launchPrev = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _launchPending = new(StringComparer.OrdinalIgnoreCase);
        // Enfriamiento por exe: un programa que se reinicia solo en bucle no debe llenar la
        // pantalla de avisos.
        private readonly Dictionary<string, DateTime> _launchAlertedUtc = new(StringComparer.OrdinalIgnoreCase);
        private bool _launchSeeded;
        private bool _launchStrictWas;

        // Cola de avisos. Los programas sin decisión esperan aquí y se preguntan de uno en uno:
        // al encender el corte hay decenas a la vez, y sin cola saldrían veinte ventanas montadas
        // en la misma esquina de la pantalla, todas encima de la anterior.
        private readonly List<(string exe, bool programaNuevo)> _colaAviso = new();
        private readonly HashSet<string> _colaPuesta = new(StringComparer.OrdinalIgnoreCase);
        // Los que ya tienen una ventana abierta esperando respuesta. Mientras alguno esté ahí,
        // la cola no suelta nada; se quita cuando el aviso se cierra (AvisoCerrado).
        private readonly HashSet<string> _avisosViendo = new(StringComparer.OrdinalIgnoreCase);
        private const int ColaTope = 300;
        // Rutas de prueba que mete --cola-check para poder comprobar el orden de salida en un PC
        // que ya está todo respondido. No existen en el equipo, así que el sondeo de procesos no
        // las va a volver a poner nunca en la lista de abiertas: se recuerdan aquí para que el
        // aviso salga de verdad y se pueda medir si sale de uno en uno. Lo normal (un PC de
        // verdad) no llega jamás con la lista vacía de ficticios.
        private readonly HashSet<string> _ficticios = new(StringComparer.OrdinalIgnoreCase);
        private int _scanTicks = 3;   // a 3: el primer sondeo ya repasa los procesos, no espera tres vueltas
        private volatile bool _repasoPediente;

        // Pide que el próximo sondeo (dentro de un segundo) repase los procesos sin esperar a los
        // tres habituales. Es lo que mueve el botón «Refrescar» de Protección.
        public void PedirRepaso() => _repasoPediente = true;

        // Modo «en vivo» de la pestaña de Protección: repasa los procesos en cada sondeo en vez
        // de uno de cada tres. Lo enciende y apaga la propia vista (solo mientras está a la
        // vista), y es lo único que hace gastar más: leer la lista de procesos cuesta ~10 ms.
        public volatile bool RepasarCadaVuelta;
        private int _hiddenProcs;
        private List<string> _hiddenNames = new();

        // Procesos que el monitor no alcanza a leer (servicios y aplicaciones elevadas): no
        // aparece su ruta, así que ni se listan ni se pueden preguntar.
        public int HiddenProcessCount { get { lock (_lock) return _hiddenProcs; } }

        // Nombres de algunos de esos procesos invisibles, para poder decírselo al usuario.
        public List<string> HiddenProcessNames()
        {
            lock (_lock) return new List<string>(_hiddenNames);
        }

        public DateTime SessionStartUtc { get; } = DateTime.UtcNow;

        public TrafficService(SettingsService settings, EventStore events, FirewallService firewall)
        {
            _settings = settings;
            _events = events;
            _firewall = firewall;

            // Las rutas de Windows varían en mayúsculas entre llamadas (svchost vs SVCHOST);
            // sin esto la misma app aparece duplicada en el histórico.
            var stored = _appsStore.Load(() => new Dictionary<string, TrackedApp>());
            _apps = new Dictionary<string, TrackedApp>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in stored)
            {
                var app = kv.Value;
                if (string.IsNullOrEmpty(app.ExePath)) app.ExePath = kv.Key;
                _apps[app.ExePath] = app;
            }

            // Si la sesión anterior terminó en incógnito, se guarda tal cual está el histórico
            // ahora mismo: es el que habrá que recuperar cuando se desactive.
            if (_settings.Current.IncognitoMode) TakeIncognitoSnapshot();
        }

        // ---------- Modo incógnito ----------

        // El histórico de apps (bytes acumulados, hosts a los que salió, primera vez que se
        // vio cada programa) es lo que delata el uso del PC. En incógnito no se escribe, y al
        // desactivarlo se recupera el que había antes de activarlo: ese tramo no deja rastro.
        // Lo que NO se toca son las reglas del firewall ni los avisos: cortar o dejar salir a
        // una app es una decisión, no histórico.
        private string? _beforeIncognito;

        private void TakeIncognitoSnapshot()
        {
            lock (_lock)
            {
                try { _beforeIncognito = JsonSerializer.Serialize(_apps); }
                catch { _beforeIncognito = null; }
            }
        }

        /// on = incógnito activado (deja de grabarse); on = false (vuelve a grabar).
        public void ApplyIncognito(bool on)
        {
            if (on)
            {
                TakeIncognitoSnapshot();
                Recording.Set(false);
                return;
            }

            var backup = _beforeIncognito;
            _beforeIncognito = null;
            Recording.Set(true);
            if (string.IsNullOrEmpty(backup)) return;

            try
            {
                var foto = JsonSerializer.Deserialize<Dictionary<string, TrackedApp>>(backup);
                if (foto == null) return;

                lock (_lock)
                {
                    // La foto vuelve, pero por debajo de las decisiones tomadas en el tramo:
                    // ese detalle delicado vive en IncognitoHistory, que está probado a parte.
                    var dict = IncognitoHistory.Merge(foto, _apps);
                    _apps = dict;
                    _appsStore.Save(dict);   // sin pasar por el corte del incógnito
                }
            }
            catch { }
        }

        // Vaciar el histórico a propósito (botón de Configuración).
        public void ClearHistory()
        {
            lock (_lock)
            {
                _apps.Clear();
                _seenPairs.Clear();
                _beforeIncognito = null;
                _appsStore.Save(_apps);
            }
        }

        // Punto único por donde sale el histórico a disco.
        // (El llamante ya sostiene el lock cuando lo invoca desde Poll.)
        private void PersistApps()
        {
            if (Recording.Incognito) return;
            _appsStore.Save(_apps);
        }

        public void Start()
        {
            Interlocked.Exchange(ref _stopping, 0);
            _timer = new Timer(_ => Poll(), null, TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(1));
        }

        public void Stop()
        {
            Interlocked.Exchange(ref _stopping, 1);
            var timer = Interlocked.Exchange(ref _timer, null);
            if (timer == null) return;

            using var stopped = new ManualResetEvent(false);
            timer.Dispose(stopped);
            stopped.WaitOne(TimeSpan.FromSeconds(5));
        }

        public IReadOnlyList<TrackedApp> GetApps()
        {
            lock (_lock)
            {
                foreach (var a in _apps.Values)
                    a.IsRunning = RunningPidsFor(a.ExePath);
                return _apps.Values.OrderByDescending(a => a.TotalSent + a.TotalReceived).ToList();
            }
        }

        // Copia de los hosts: el conjunto vivo crece desde el hilo de sondeo.
        public List<string> HostsOf(TrackedApp app)
        {
            lock (_lock) return new List<string>(app.Hosts);
        }

        // Instantanea pid -> exe para las herramientas que relacionan una conexión viva con su
        // programa (el radar de destinos). Se copia porque el repaso de procesos la sustituye
        // entera cada pocos segundos desde otro hilo.
        public Dictionary<int, string> ExeMapaActual()
        {
            lock (_pidLock) return new Dictionary<int, string>(_pidExe);
        }

        public (long rx, long tx, double rxBps, double txBps) GetSessionTotals()
        {
            lock (_lock)
            {
                var secs = Math.Max(1, (DateTime.UtcNow - _lastPollUtc).TotalSeconds);
                return (_sessionRx, _sessionTx, _instantRx, _instantTx);
            }
        }

        private double _instantRx, _instantTx;

        // Instantanea pid -> executable que renueva el repaso de procesos (cada ~3 s).
        // Antes existia un cache permanente indexado por numero de proceso. Windows reutiliza
        // los PIDs, asi que tras varias horas de marcha el trafico acababa atribuido al programa
        // muerto que tuvo aquel numero antes: las apps en marcha marcaban 0 B/s y el consumo se
        // iba a filas que ya no existen. La instantanea se descarta entera en cada repaso, de
        // modo que un PID viejo no puede sobrevivir.
        private Dictionary<int, string> _pidExe = new();
        private readonly object _pidLock = new();

        // pid -> pid del padre, de la misma pasada del repaso de procesos. Solo se usa para la
        // herencia (1.6.0): un programa que dijo «aplicar lo mismo a mis procesos hijo» tiene que
        // poder decir cuáles son, y esa ascendencia no está en Process.GetProcesses().
        private Dictionary<int, int> _padreDe = new();

        // Hijos a los que ya les escribimos reglas la vuelta anterior. Se compara contra lo que
        // hay ahora: mientras el conjunto no cambie no se toca el firewall, así que un navegador
        // con veinte pestañas abiertas no genera veinte llamadas elevadas por minuto.
        private readonly HashSet<string> _hijosReglados = new(StringComparer.OrdinalIgnoreCase);
        private int _heredando;
        private int _heredadesSucias;

        private const int HijosPorPasada = 40;

        private void Poll()
        {
            if (Volatile.Read(ref _stopping) != 0) return;
            if (Interlocked.CompareExchange(ref _polling, 1, 0) != 0) return;
            try
            {
                if (Volatile.Read(ref _stopping) != 0) return;
                var conns = NativeNet.GetConnections();
                var (rx, tx) = NativeNet.GetInterfaceOctets();
                var now = DateTime.UtcNow;
                double dt = Math.Max(0.2, (now - _lastPollUtc).TotalSeconds);

                long dRx = 0, dTx = 0;
                if (!_firstSample)
                {
                    // Contadores de 64 bits: solo hay que proteger el reinicio por cambio de interfaz.
                    dRx = rx >= _lastRx ? rx - _lastRx : 0;
                    dTx = tx >= _lastTx ? tx - _lastTx : 0;
                }
                _lastRx = rx; _lastTx = tx; _lastPollUtc = now; _firstSample = false;

                // Agrupa conexiones por exe. Para REPARTIR el tráfico medido solo cuentan las
                // TCP establecidas (las que de verdad transportan datos); para detectar hosts y
                // primeras conexiones sirven todas. Así una app bloqueada o cerrada, que sigue
                // dejando filas residuales (TIME_WAIT, CLOSE_WAIT, LISTEN, sockets UDP abiertos),
                // ya no recibe una tajada del tráfico de los demás: su peso pasa a ser 0.
                var byExe = new Dictionary<string, List<ConnectionInfo>>();
                var estByExe = new Dictionary<string, int>();
                var remoteByExe = new Dictionary<string, HashSet<string>>();
                Dictionary<int, string> pidMap;
                lock (_pidLock) pidMap = _pidExe;
                foreach (var c in conns)
                {
                    string exe = ExePathForPid(c.Pid, pidMap);
                    if (exe == null) continue;
                    if (!byExe.TryGetValue(exe, out var l)) byExe[exe] = l = new List<ConnectionInfo>();
                    l.Add(c);
                    if (c.Established) estByExe[exe] = estByExe.TryGetValue(exe, out var n) ? n + 1 : 1;
                    if (!string.IsNullOrEmpty(c.RemoteAddress))
                    {
                        if (!remoteByExe.TryGetValue(exe, out var h)) remoteByExe[exe] = h = new HashSet<string>();
                        h.Add(c.RemoteAddress);
                    }
                }

                lock (_lock)
                {
                    _instantRx = dRx / dt;
                    _instantTx = dTx / dt;
                    _sessionRx += dRx;
                    _sessionTx += dTx;

                    // Peso = nº de conexiones establecidas de la app entre el total de establecidas.
                    int totalEst = 0;
                    foreach (var kv in estByExe) totalEst += kv.Value;
                    if (totalEst == 0) totalEst = 1;

                    foreach (var kv in byExe)
                    {
                        var app = GetOrCreateApp(kv.Key, now);
                        // Cualquier fila en la tabla de conexiones cuenta como «pidiendo salida»
                        // esta vuelta, aunque esté en TIME_WAIT: es la marca que lee la ventana de
                        // minutos de la lista de Protección.
                        app.LastEgressUtc = now;
                        app.LastAliveUtc = now;
                        int est = estByExe.TryGetValue(kv.Key, out var n) ? n : 0;
                        double weight = est / (double)totalEst;
                        app.TotalReceived += (long)(dRx * weight);
                        app.TotalSent += (long)(dTx * weight);
                        app.ReceivedBps = _instantRx * weight;
                        app.SentBps = _instantTx * weight;

                        foreach (var host in remoteByExe.TryGetValue(kv.Key, out var hs) ? hs : Enumerable.Empty<string>())
                        {
                            app.Hosts.Add(host);
                            string pair = kv.Key + "|" + host;
                            if (_seenPairs.Add(pair) && Volatile.Read(ref _stopping) == 0)
                                OnFirstConnection(app, host, now);
                        }
                    }

                    // Apps sin ninguna conexión esta vuelta: decaimiento de tasas.
                    foreach (var a in _apps.Values)
                    {
                        if (!byExe.ContainsKey(a.ExePath))
                        {
                            a.SentBps *= 0.7;
                            a.ReceivedBps *= 0.7;
                        }
                    }
                }

                // Cada ~3 s se repasa la lista de procesos: es lo que permite ver apps nuevas
                // cuando el bloqueo total no deja ni una entrada en la tabla TCP. El botón
                // «Refrescar» de Protección no espera a ese repaso: pide uno para ahora.
                // Con la barra de Protección en «en vivo» el repaso va en cada sondeo, y solo
                // mientras esa pestaña está a la vista (ella misma enciende y apaga la bandera).
                if (_repasoPediente || RepasarCadaVuelta || ++_scanTicks >= 3)
                {
                    _repasoPediente = false;
                    _scanTicks = 0;
                    ScanProcesses();
                }

                if (Volatile.Read(ref _stopping) == 0)
                {
                    SaveAppsThrottled();
                    DataUpdated?.Invoke();
                }
            }
            catch { }
            finally { Interlocked.Exchange(ref _polling, 0); }
        }

        private void OnFirstConnection(TrackedApp app, string remoteIp, DateTime now)
        {
            if (Volatile.Read(ref _stopping) != 0) return;
            bool local = IsLocalOrPrivate(remoteIp);
            _events.Add(EventKind.FirstConnection,
                $"{app.Name} inició su primera conexión de red",
                remoteIp + (local ? " (red local)" : ""),
                important: !local);

            if (Volatile.Read(ref _stopping) == 0 &&
                _settings.Current.AlertsEnabled && !local && !app.IsKnown)
                FirstConnectionDetected?.Invoke(app, remoteIp);
        }

        // Copia de los ejecutables en marcha la última vez que se repasó (cada ~3 s).
        public List<string> GetRunningExes()
        {
            lock (_lock) return _runningExes.ToList();
        }

        // Repasa los procesos en marcha: actualiza el inventario y, con el bloqueo total
        // activo, avisa de los programas que se acaban de abrir y que no tienen permiso.
        private void ScanProcesses()
        {
            var exes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pidMap = new Dictionary<int, string>();
            int hidden = 0;
            var hiddenNames = new List<string>();
            int mySession = SafeSessionId();
            try
            {
                foreach (var p in Process.GetProcesses())
                {
                    try
                    {
                        string? path = null;
                        try { path = p.MainModule?.FileName; } catch { }
                        pidMap[p.Id] = path ?? "";
                        // El nombre se apunta siempre, también de los que sí dieron su ruta:
                        // así «¿está en marcha?» se contesta con un solo vistazo a la tabla.
                        try { names.Add(p.ProcessName); } catch { }
                        if (!string.IsNullOrEmpty(path) && File.Exists(path)) exes.Add(path);
                        else if (p.Id > 4 && SameSession(p, mySession))
                        {
                            // Solo se cuentan los que están en el escritorio del usuario: los de
                            // la sesión 0 son servicios de Windows (csrss, wininit, Registry...) y
                            // nunca van a ser un programa que él haya abierto.
                            hidden++;
                            string n = p.ProcessName;
                            if (n.Length > 0 && !hiddenNames.Contains(n) && hiddenNames.Count < 12)
                                hiddenNames.Add(n);
                        }
                    }
                    catch { }
                    finally { try { p.Dispose(); } catch { } }
                }
            }
            catch { }

            // La ascendencia sale de una sola llamada barata (una instantánea de Toolhelp):
            // mezclarla con el bucle de arriba obligaría a abrir cada proceso dos veces, y esa
            // lectura sí puede fallar con los procesos protegidos.
            var padres = Native.NativeProcs.ParentMap();

            // app + "es la primera vez que este PC ve ese programa". Aquí solo acaban las que se
            // registran sin ventana: el modo «Bloquear a todos» no pregunta, pero sí deja rastro.
            var nuevos = new List<(TrackedApp app, bool nuevoPrograma)>();
            bool strict = _settings.Current.StrictMode;
            var now = DateTime.UtcNow;

            lock (_lock)
            {
                _hiddenProcs = hidden;
                _hiddenNames = hiddenNames;
                _runningExes.Clear();
                foreach (var e in exes) _runningExes.Add(e);
                _runningNames = names;

                // Marca de «estaba abierto en este repaso». Se apunta solo en las fichas que ya
                // existen: crear una ficha por cada proceso del sistema inflaría apps.json con
                // cien entradas que el usuario nunca va a mirar.
                foreach (var e in exes)
                    if (_apps.TryGetValue(e, out var viva)) viva.LastAliveUtc = now;

                // La instantanea de PIDs se cambia de golpe: el sondeo de conexiones la lee tal
                // cual era en este repaso, sin ver medias actualizaciones.
                lock (_pidLock) { _pidExe = pidMap; _padreDe = padres; }

                if (!_launchSeeded || strict != _launchStrictWas)
                {
                    Copy(exes, _launchPrev);
                    _launchPending.Clear();
                    _launchAlertedUtc.Clear();
                    _launchSeeded = true;
                    _launchStrictWas = strict;

                    // Con el corte puesto, lo que ya estaba abierto TAMBIÉN se pregunta. Antes se
                    // eximía a todo lo abierto ("lo tenía en marcha antes de decidir nada"), y con
                    // la salida cortada eso significaba bloquearlo en silencio y sin derecho a
                    // respuesta: justo la queja de «pone Preguntar y no pregunta, corta directo».
                    // Sin corte no se barre: nada se corta, y preguntar veinte cosas al abrir el
                    // programa sería ruido por algo que sigue funcionando igual.
                    if (strict) BarridaDeAbiertos(exes);
                }
                else
                {
                    // 1) Los candidatos del repaso anterior que SIGUEN vivos se confirman: así
                    //    una utilidad relámpago que ya cerró no corta el trabajo con un aviso.
                    var confirmados = new List<string>();
                    foreach (var e in _launchPending)
                        if (exes.Contains(e)) confirmados.Add(e);
                    // Ya se avisó de ellos; lo que cerró antes del segundo repaso se descarta.
                    foreach (var e in confirmados) _launchPending.Remove(e);
                    _launchPending.RemoveWhere(e => !exes.Contains(e));

                    // 2) Los que acaban de aparecer entran en lista de espera para el siguiente
                    //    repaso (~3 s después).
                    foreach (var e in exes)
                        if (!_launchPrev.Contains(e)) _launchPending.Add(e);

                    foreach (var exe in confirmados)
                    {
                        if (!IsLaunchAlertCandidate(exe)) continue;
                        if (_firewall.HasUserDecision(exe, Direction.Out)) continue;

                        // En el modo normal solo se pregunta por los programas que el monitor no
                        // había visto nunca (y a los que no se les respondió ya) o por los que el
                        // usuario rearma con "Que vuelva a preguntar"; sin bloqueo total la app
                        // sale igual, así que insistir en cada arranque sería ruido.
                        bool nuncaVisto = !_apps.ContainsKey(exe);
                        var app = GetOrCreateApp(exe, now);
                        if (app.IsKnown) continue;
                        bool rearmando = app.AskOnNextLaunch;
                        if (!strict && (!nuncaVisto || app.AskedWithoutDeciding) && !rearmando) continue;
                        if (rearmando) app.AskOnNextLaunch = false;   // el rearme dura un aviso

                        if (_launchAlertedUtc.TryGetValue(exe, out var last) &&
                            (now - last).TotalSeconds < RelaunchAlertSeconds) continue;

                        _launchAlertedUtc[exe] = now;
                        // Pedir salida cuenta aunque no llegue ninguna conexión a la tabla: con el
                        // corte activo el SYN se tira fuera y el sondeo de conexiones nunca lo ve.
                        // Sin esta marca, la ventana de minutos de Protección dejaría fuera a la
                        // app que justo está esperando una respuesta.
                        app.LastEgressUtc = now;
                        // Con avisos puestos se encola (se pregunta de uno en uno). En «Bloquear a
                        // todos» solo se registra el evento, que es lo que ese modo promete:
                        // cortar sin interrumpir.
                        if (_settings.Current.AlertsEnabled) EncolarAviso(exe, !strict);
                        else nuevos.Add((app, !strict));
                    }
                    if (_launchAlertedUtc.Count > 600) _launchAlertedUtc.Clear();
                }

                Copy(exes, _launchPrev);
            }

            foreach (var (app, nuevoPrograma) in nuevos)
            {
                if (Volatile.Read(ref _stopping) != 0) break;
                RegistrarAviso(app, nuevoPrograma);
            }

            // El aviso que toque, si no hay ninguno sin responder delante.
            SoltarAviso();

            if (nuevos.Count > 0) SaveAppsThrottled();

            // La herencia va al final del repaso: necesita la tabla pid→exe ya publicada, y es lo
            // único de aquí que puede tardar (habla con el firewall), así que no debe retrasar los
            // avisos de arranque.
            HeredarAProcesosHijo();
        }

        // ---------- La cola de avisos (1.6.7) ----------

        // El texto del aviso cambia según por qué se pregunta: con el corte activo la app no tiene
        // salida; sin él es simplemente un programa nuevo que decide si puede salir.
        private static string TextoAviso(TrackedApp app, bool programaNuevo)
            => programaNuevo
                ? $"{app.Name} se abrió por primera vez: todavía no tiene decisión de salida"
                : $"Monitor de Red PCJ bloqueó la salida de {app.Name} al abrirla sin permiso";

        private void RegistrarAviso(TrackedApp app, bool programaNuevo)
            => _events.Add(EventKind.FirstConnection, TextoAviso(app, programaNuevo),
                           app.ExePath, important: true);

        // Meten una ruta en la cola si no estaba ya. Se llama con el candón cogido.
        private void EncolarAviso(string exe, bool programaNuevo)
        {
            if (string.IsNullOrEmpty(exe) || _colaPuesta.Contains(exe) ||
                _colaAviso.Count >= ColaTope) return;
            _colaPuesta.Add(exe);
            _colaAviso.Add((exe, programaNuevo));
        }

        // Juzga si un programa abierto merece aviso: no puede ser componente de Windows ni el
        // propio monitor, no puede tener ya una decisión puesta, y no puede estar esperando su
        // turno o con la ventana abierta. `crearFicha` es la diferencia entre encolar de verdad
        // (hace falta la ficha para poder montar el aviso) y comprobar a seco.
        private bool EsPreguntable(string exe, bool respetarDescartadas, bool crearFicha,
                                   out TrackedApp? app)
        {
            app = null;
            if (!IsLaunchAlertCandidate(exe)) return false;
            if (_firewall.HasUserDecision(exe, Direction.Out)) return false;
            if (_colaPuesta.Contains(exe) || _avisosViendo.Contains(exe)) return false;

            if (crearFicha) app = GetOrCreateApp(exe, DateTime.UtcNow);
            else _apps.TryGetValue(exe, out app);

            if (app != null)
            {
                if (app.IsKnown) return false;                       // ya respondió alguna vez
                if (respetarDescartadas && app.AskedWithoutDeciding) return false;
            }
            return true;
        }

        // Barrida de lo que ya estaba abierto al poner el corte: todo programa que no sea de
        // Windows, no sea el propio monitor y no tenga decisión, entra en la cola. Va en orden
        // alfabético de ruta para que el orden de las preguntas sea siempre el mismo.
        // Devuelve cuántas entradas nuevas se encolaron (el botón de «volver a preguntar» lo dice).
        private int EncolarAbiertos(HashSet<string> exes, bool respetarDescartadas)
        {
            int n = 0;
            foreach (string exe in exes.OrderBy(e => e, StringComparer.OrdinalIgnoreCase))
            {
                if (!EsPreguntable(exe, respetarDescartadas, crearFicha: true, out var app)) continue;
                if (app != null) app.AskedWithoutDeciding = false;   // el rearme manual borra la marca
                EncolarAviso(exe, !_settings.Current.StrictMode);
                n++;
            }
            if (n > 0) PersistApps();
            return n;
        }

        // Versión de la barrida que usa el repaso de procesos, dentro de su propio candón.
        private void BarridaDeAbiertos(HashSet<string> exes) => EncolarAbiertos(exes, true);

        // Lo que preguntaría ahora mismo la barrida, sin encolar nada ni crear fichas. Es lo que
        // lee --cola-check para poder revisar el filtro sobre los procesos reales del PC.
        public List<string> CandidatosDeBarrida()
        {
            var salida = new List<string>();
            lock (_lock)
            {
                foreach (string exe in _runningExes.OrderBy(e => e, StringComparer.OrdinalIgnoreCase))
                    if (EsPreguntable(exe, respetarDescartadas: true, crearFicha: false, out _))
                        salida.Add(exe);
            }
            return salida;
        }

        // Lo que queda por preguntar, para poder decirlo con palabras en la ventana de aviso y en
        // la cabecera de Protección.
        public int AvisosEnCola
        {
            get { lock (_lock) return _colaAviso.Count; }
        }

        // El aviso se cerró (respondido o descartado): se libera la cola para el siguiente, que
        // saldrá en el próximo repaso. Se quita también si la ventana no llegó a abrirse porque
        // ya había una igual, para que la cola no se quede bloqueada esperando un cierre imposible.
        public void AvisoCerrado(string exePath)
        {
            if (string.IsNullOrEmpty(exePath)) return;
            lock (_lock) _avisosViendo.Remove(exePath);
        }

        // Saca un aviso de la cola y lo pone en pantalla. Solo uno cada vez: si el usuario aún no
        // ha respondido el anterior, los demás esperan, en vez de taparle la pantalla entera.
        private void SoltarAviso()
        {
            if (Volatile.Read(ref _stopping) != 0) return;
            if (!_settings.Current.AlertsEnabled) return;

            TrackedApp? app = null;
            bool programaNuevo = false;
            lock (_lock)
            {
                if (_avisosViendo.Count > 0 || _colaAviso.Count == 0) return;
                while (_colaAviso.Count > 0)
                {
                    var (exe, nuevo) = _colaAviso[0];
                    _colaAviso.RemoveAt(0);
                    _colaPuesta.Remove(exe);

                    // Ya no vale la pena preguntarle: cerró (se le pregunta cuando vuelva a
                    // abrirse) o alguien le dio una decisión mientras esperaba en la cola.
                    if (!_runningExes.Contains(exe) && !_ficticios.Contains(exe)) continue;
                    if (_firewall.HasUserDecision(exe, Direction.Out)) continue;
                    if (!_apps.TryGetValue(exe, out var ficha) || ficha.IsKnown) continue;

                    _avisosViendo.Add(exe);
                    _launchAlertedUtc[exe] = DateTime.UtcNow;
                    app = ficha;
                    programaNuevo = nuevo;
                    break;
                }
            }
            if (app == null) return;

            RegistrarAviso(app, programaNuevo);
            BlockedLaunchDetected?.Invoke(app);
        }

        // «Que vuelva a preguntar por los abiertos»: rearme manual de la barrida, para cuando se
        // fueron cerrando los avisos sin responder. Devuelve cuántos volvieron a la cola.
        public int VolverAPreguntarAbiertos()
        {
            lock (_lock) return EncolarAbiertos(_runningExes, false);
        }

        // El programa que tiene ahora mismo un aviso abierto esperando respuesta.
        public List<string> AvisosEnPantalla()
        {
            lock (_lock) return _avisosViendo.ToList();
        }

        // ---------- Parte de comprobación (--cola-check) ----------

        /// Enseña a quién preguntaría la barrida y comprueba que los avisos salen de uno en uno.
        /// Se llama desde --cola-check, que no crea la ventana principal: el evento no tiene nadie
        /// apuntado, así que no aparece ningún aviso en pantalla, y el modo comprobación trae
        /// EscrituraProhibida (ni una regla) e incógnito (ni una línea del histórico).
        public string ReporteDeCola()
        {
            var sb = new System.Text.StringBuilder();
            int fallos = 0;
            void Chequeo(string etiqueta, bool ok)
            {
                sb.AppendLine((ok ? "  ok    " : "  FALLO ") + etiqueta);
                if (!ok) fallos++;
            }

            // Primero hay que dejar que el sondeo repase los procesos: al arrancar el servicio la
            // tabla de abiertos todavía está vacía y todo saldría a cero.
            PedirRepaso();
            Espera(3000);

            bool strict = _settings.Current.StrictMode;
            bool avisos = _settings.Current.AlertsEnabled;
            sb.AppendLine("== El modo pedido ==");
            sb.AppendLine("  " + (!strict ? "0 · Avisar sin cortar"
                                : avisos ? "1 · Preguntar para conectar"
                                         : "2 · Bloquear a todos"));
            sb.AppendLine("  programas abiertos que este monitor ve: " + _runningExes.Count);

            // Cuánto se llevaría puesto «Borrar todas las decisiones». Aquí solo se cuenta: el
            // modo comprobación trae la escritura prohibida y el borrado ni se llama. Se lee dos
            // veces lo mismo para comprobar que contar no cambia nada.
            int marcas1 = _firewall.DecisionCount();
            int apps1 = OlvidarMarcasDeApps(soloContar: true);
            sb.AppendLine();
            sb.AppendLine("== Decisiones guardadas (solo lectura) ==");
            sb.AppendLine("  marcas de decisión en el disco: " + marcas1);
            sb.AppendLine("  reglas de app que se borrarían:  " + _firewall.DecisionRulesCount());
            sb.AppendLine("  aplicaciones ya contestadas:     " + apps1);
            sb.AppendLine("  se conservan: la salida del propio monitor, las excepciones del " +
                          "sistema, los puertos y la configuración adicional");
            Chequeo("contar decisiones no cambia nada",
                marcas1 == _firewall.DecisionCount() && apps1 == OlvidarMarcasDeApps(soloContar: true));

            var candidatos = CandidatosDeBarrida();
            int yaEnCola = AvisosEnCola;
            sb.AppendLine();
            sb.AppendLine("== A quién preguntaría la barrida (" + candidatos.Count + ") ==");
            // La barrida real ya actuó en el primer repaso de este proceso: lo que entonces entró
            // en la cola no vuelve a salir en la lista de arriba, porque ya está preguntado.
            sb.AppendLine("  (y hay " + yaEnCola + " ya en la cola desde el primer repaso)");
            foreach (string e in candidatos) sb.AppendLine("  " + e);

            string windir = (Environment.GetFolderPath(Environment.SpecialFolder.Windows) ?? "")
                .TrimEnd('\\');
            string self = Environment.ProcessPath ?? "";
            sb.AppendLine();
            sb.AppendLine("== Comprobaciones del filtro ==");
            Chequeo("ninguna candidata vive dentro de Windows",
                !candidatos.Any(e => e.StartsWith(windir + "\\", StringComparison.OrdinalIgnoreCase)));
            Chequeo("ninguna candidata es el propio monitor",
                !candidatos.Any(e => string.Equals(e, self, StringComparison.OrdinalIgnoreCase)));
            Chequeo("ninguna candidata tiene la decisión ya puesta",
                candidatos.All(e => !_firewall.HasUserDecision(e, Direction.Out)));
            Chequeo("sin rutas repetidas",
                candidatos.Count == candidatos.Distinct(StringComparer.OrdinalIgnoreCase).Count());

            sb.AppendLine();
            sb.AppendLine("== Los avisos, de uno en uno ==");
            if (!strict || !avisos)
            {
                sb.AppendLine("  en este modo no se pregunta a nadie: nada que probar");
            }
            else
            {
                int rearmados = VolverAPreguntarAbiertos();
                int porPreguntar = AvisosEnCola + AvisosEnPantalla().Count;
                sb.AppendLine("  en la cola de avisos: " + porPreguntar +
                              " (los de la barrida del primer repaso y " + rearmados + " rearmados)");
                if (porPreguntar == 0)
                {
                    // En un PC ya repasado no queda nadie sin decidir, así que no habría nada que
                    // comprobar. Se meten tres rutas falsas en la cola —solo en la memoria de este
                    // proceso, sin tocar el firewall ni el histórico— para poder verificar el
                    // comportamiento que de verdad importa: que un aviso no pise al siguiente.
                    sb.AppendLine("  no hay programas abiertos sin decidir en este PC: se prueba con tres falsos");
                    lock (_lock)
                    {
                        foreach (string e in new[] { @"C:\pcj-prueba\uno.exe", @"C:\pcj-prueba\dos.exe",
                                                    @"C:\pcj-prueba\tres.exe" })
                        {
                            _apps[e] = new TrackedApp { ExePath = e, Name = Path.GetFileNameWithoutExtension(e) };
                            _runningExes.Add(e);
                            // Además se recuerdan como ficticias: el repaso de procesos siguiente
                            // reconstruye la lista de abiertos desde lo que hay en el equipo, y
                            // estas tres rutas no existen, así que sin este aviso se perderían de
                            // la cola sin llegar a salir nunca.
                            _ficticios.Add(e);
                            EncolarAviso(e, programaNuevo: false);
                        }
                    }
                    porPreguntar = AvisosEnCola + AvisosEnPantalla().Count;
                }

                PedirRepaso();
                Espera(2500);
                var viendo = AvisosEnPantalla();
                sb.AppendLine("  tras un repaso: en pantalla " + viendo.Count + ", en cola " + AvisosEnCola);
                Chequeo("en pantalla no hay más de un aviso", viendo.Count <= 1);
                Chequeo("el resto espera su turno en la cola",
                        viendo.Count + AvisosEnCola == porPreguntar);

                PedirRepaso();
                Espera(2500);
                Chequeo("mientras el primero no se cierre no sale el siguiente",
                        AvisosEnPantalla().Count <= 1 &&
                        AvisosEnPantalla().Count + AvisosEnCola == porPreguntar);

                if (viendo.Count == 1)
                {
                    AvisoCerrado(viendo[0]);
                    PedirRepaso();
                    Espera(2500);
                    int despues = AvisosEnPantalla().Count + AvisosEnCola;
                    sb.AppendLine("  tras cerrar el primero: en pantalla " + AvisosEnPantalla().Count +
                                  ", en cola " + AvisosEnCola);
                    Chequeo("cerrar uno deja uno menos por preguntar", despues == porPreguntar - 1);
                    Chequeo("y el hueco lo ocupa el siguiente de la cola",
                        porPreguntar == 1 ? AvisosEnPantalla().Count == 0
                                          : AvisosEnPantalla().Count == 1);
                }
            }

            sb.AppendLine();
            sb.AppendLine(fallos == 0 ? "TODO CORRECTO" : "HAY " + fallos + " FALLOS");
            return sb.ToString();
        }

        // El repaso va en su hilo; aquí se espera a que termine la vuelta pedida.
        private static void Espera(int ms) => Task.Delay(ms).Wait();

        // ---------- Herencia a los procesos hijo (1.6.0) ----------

        // El usuario puede pedir, en la configuración adicional de una aplicación, que lo que se le
        // permitió o se le cortó se aplique también a los procesos que ella abre. Sin esto, un
        // instalador cortado saca tráfico por su descargador, que nunca llegó a la lista.
        //
        // Se hace dentro del repaso que ya existía, en vez de un hilo nuevo, porque ese repaso es
        // el único sitio que sabe qué procesos están vivos. Y se compara el conjunto de hijos con
        // el de la vuelta anterior: mientras no cambie no se escribe nada, que es lo que evita que
        // un navegador con veinte pestañas mantenga al firewall ocupado cada tres segundos.
        private void HeredarAProcesosHijo()
        {
            if (Volatile.Read(ref _stopping) != 0) return;
            if (Interlocked.CompareExchange(ref _heredando, 1, 0) != 0) return;
            try
            {
                Dictionary<int, string> pidExe;
                Dictionary<int, int> padreDe;
                lock (_pidLock) { pidExe = _pidExe; padreDe = _padreDe; }
                if (pidExe.Count == 0 || padreDe.Count == 0) return;

                // Si el usuario cambió una configuración, lo ya heredado puede estar viejo: se
                // olvida el conjunto y esta vuelta se reescribe entero. La bandera la pone la
                // interfaz, pero se consume aquí, que es el único hilo que toca _hijosReglados.
                if (Interlocked.Exchange(ref _heredadesSucias, 0) != 0) _hijosReglados.Clear();

                // Qué procesos deberían llevar reglas heredadas ahora, y de quién. El emparejamiento
                // vive en AppAjustes para poder comprobarse sobre el papel (--ajuste-check).
                var deseados = AppAjustes.HijosHeredables(pidExe, padreDe, FindApp,
                    Environment.ProcessPath);

                var nuevos = new List<string>();
                foreach (var kv in deseados)
                    if (!_hijosReglados.Contains(kv.Key)) nuevos.Add(kv.Key);
                var idos = new List<string>();
                foreach (string e in _hijosReglados)
                    if (!deseados.ContainsKey(e)) idos.Add(e);
                if (nuevos.Count == 0 && idos.Count == 0) return;

                // Techo por pasada: si un programa suelta cien hijos a la vez, la primera tanda ya
                // está cortada antes de que terminen de nacer; el resto entra en pasadas siguientes.
                if (nuevos.Count > HijosPorPasada)
                    nuevos.RemoveRange(HijosPorPasada, nuevos.Count - HijosPorPasada);

                var borrar = new List<string>();
                var crear = new List<FirewallService.ReglaSpec>();
                foreach (string exe in idos) borrar.AddRange(FirewallService.AjusteRuleNames(exe));

                var ahora = DateTime.UtcNow;
                foreach (string exe in nuevos)
                {
                    var cfg = FindApp(deseados[exe]);
                    if (cfg == null) continue;
                    AppAjustes.ConstruirHijo(exe, cfg, borrar, crear);
                    HeredarFicha(exe, cfg, ahora);
                }
                if (crear.Count == 0 && borrar.Count == 0) return;

                // Si Windows no las acepta no se apunta nadie como reglado: la vuelta siguiente lo
                // intenta otra vez, en vez de quedarse con el hueco para siempre.
                if (!App.Firewall.ApplySpecs(borrar, crear)) return;

                foreach (string exe in nuevos) _hijosReglados.Add(exe);
                foreach (string exe in idos) _hijosReglados.Remove(exe);
                if (nuevos.Count > 0)
                {
                    SaveAppsThrottled();
                    foreach (string exe in nuevos)
                        _events.Add(EventKind.RuleChanged,
                            "Monitor de Red PCJ copió la configuración de " +
                            NombreCorto(deseados[exe]) + " a su proceso hijo " + NombreCorto(exe),
                            exe, important: true);
                }
            }
            catch { }
            finally { Interlocked.Exchange(ref _heredando, 0); }
        }

        // La ficha del hijo se queda con lo del padre, para que la lista lo diga con las mismas
        // palabras, pero sin cadena: HeredarAHijos no pasa, así que los nietos no heredan del abuelo.
        private void HeredarFicha(string exeHijo, TrackedApp padre, DateTime ahora)
        {
            lock (_lock)
            {
                var app = GetOrCreateApp(exeHijo, ahora);
                app.ModoAdicional = padre.ModoAdicional;
                app.PuertosAdicionales = padre.PuertosAdicionales;
                app.SoloRedLocal = padre.SoloRedLocal;
                app.HeredarAHijos = false;
                app.HeredadaDe = padre.ExePath;
            }
        }

        private static string NombreCorto(string exe)
        {
            try { return Path.GetFileNameWithoutExtension(exe); } catch { return exe; }
        }

        // El usuario cambió la configuración de una aplicación: lo que ya estaba heredado puede
        // haberse quedado viejo, así que se pide que el próximo repaso lo reescriba todo. Se deja
        // en una bandera y no se hace a mano desde aquí para que el único que toca _hijosReglados
        // siga siendo el hilo del sondeo.
        public void RevalidarHeredades() => Interlocked.Exchange(ref _heredadesSucias, 1);

        private const double RelaunchAlertSeconds = 90;

        private static int SafeSessionId()
        {
            try { return Process.GetCurrentProcess().SessionId; } catch { return -1; }
        }

        private static bool SameSession(Process p, int session)
        {
            if (session < 0) return true;
            try { return p.SessionId == session; } catch { return false; }
        }

        private static void Copy(HashSet<string> from, HashSet<string> to)
        {
            to.Clear();
            foreach (var e in from) to.Add(e);
        }

        // Candidatas a preguntar: cualquier programa que no sea de Windows ni el propio monitor.
        // Los componentes de Windows también se listan en la pestaña, pero bajo el grupo de
        // sistema y sin interrumpir con avisos.
        private static bool IsLaunchAlertCandidate(string exe)
        {
            string self = Environment.ProcessPath ?? "";
            if (string.Equals(exe, self, StringComparison.OrdinalIgnoreCase)) return false;

            string windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows) ?? "";
            if (windir.Length > 0 && exe.StartsWith(windir, StringComparison.OrdinalIgnoreCase)) return false;

            // Un instalador o un asistente que se abre y se cierra solo no es un programa al que
            // haya que preguntarle por internet; solo se pregunta por los que se quedan abiertos.
            try
            {
                string dir = Path.GetDirectoryName(exe) ?? "";
                string lower = dir.ToLowerInvariant();
                if (lower.Contains("\\updates\\") || lower.Contains("\\temp\\") ||
                    lower.Contains("\\~\\") || lower.Contains("\\installers\\")) return false;
            }
            catch { }

            return true;
        }

        private TrackedApp GetOrCreateApp(string exePath, DateTime now)
        {
            if (!_apps.TryGetValue(exePath, out var app))
            {
                string name = Path.GetFileNameWithoutExtension(exePath);
                app = new TrackedApp { ExePath = exePath, Name = name, FirstSeenUtc = now };
                _apps[exePath] = app;
                // Aplica reglas existentes guardadas
                app.OutAction = _firewall.GetSavedAction(exePath, Direction.Out);
                app.InAction = _firewall.GetSavedAction(exePath, Direction.In);
            }
            return app;
        }

        public static bool IsLocalOrPrivate(string ip)
        {
            if (!IPAddress.TryParse(ip, out var a)) return true;
            byte[] b = a.GetAddressBytes();
            return b[0] == 0 || b[0] == 127 || (b[0] == 10) || (b[0] == 192 && b[1] == 168) ||
                   (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || b[0] == 169 ||
                   ip == "224.0.0.251" || ip.StartsWith("224.") || ip.StartsWith("239.") ||
                   ip == "255.255.255.255";
        }

        private static string? ExePathForPid(int pid, Dictionary<int, string> snapshot)
        {
            if (pid <= 4) return null;   // System y descarte: nunca son una aplicacion
            if (snapshot.TryGetValue(pid, out var known))
                return string.IsNullOrEmpty(known) ? null : known;

            // Proceso recién nacido, aún fuera de la instantánea: se lee suelto una vez y no se
            // guarda, para que el siguiente repaso lo confirme con su dato limpio.
            string path = "";
            try
            {
                using var p = Process.GetProcessById(pid);
                path = p.MainModule?.FileName ?? "";
            }
            catch { path = ""; }
            return string.IsNullOrEmpty(path) ? null : path;
        }

        // Si el programa está en marcha ahora mismo. Se contesta con las tablas que ya deja
        // el repaso de procesos, sin volver a enumerar nada: preguntar una por una con
        // Process.GetProcessesByName (unas cien aplicaciones en la lista) costaba ~340 ms y
        // era lo que congelaba la pestaña al escribir en el buscador.
        private bool RunningPidsFor(string exePath)
        {
            // Se llama desde dentro de GetApps, que ya tiene el candón cogido; por eso el
            // acceso a las tablas va aparte, con su propio candón corto.
            string name;
            try { name = Path.GetFileNameWithoutExtension(exePath) ?? ""; } catch { return false; }
            lock (_lock)
                return _runningExes.Contains(exePath) ||
                       (name.Length > 0 && _runningNames.Contains(name));
        }

        private DateTime _lastSave = DateTime.MinValue;
        private void SaveAppsThrottled()
        {
            if ((DateTime.UtcNow - _lastSave).TotalSeconds < 10) return;
            _lastSave = DateTime.UtcNow;
            lock (_lock) PersistApps();
        }

        public void MarkKnown(TrackedApp app)
        {
            lock (_lock) { app.IsKnown = true; PersistApps(); }
        }

        // Apunta un programa a mano desde la pestaña Protección. Hace falta para los que el
        // monitor no puede ver por sí solo (los que corren elevados no dicen su ruta) y para
        // los que nunca llegaron a conectar porque el bloqueo total se lo cortó.
        public TrackedApp NoteManualApp(string exePath)
        {
            lock (_lock)
            {
                var app = GetOrCreateApp(exePath, DateTime.UtcNow);
                PersistApps();
                return app;
            }
        }

        // El aviso se cerró sin pulsar nada: se recuerda para no repetir la pregunta en el
        // modo normal (con el bloqueo total sí se repite, porque sin regla no sale).
        public void NoteDismissed(TrackedApp app)
        {
            lock (_lock)
            {
                app.AskedWithoutDeciding = true;
                PersistApps();
            }
        }

        // Vuelve a preguntar por un programa cuando el usuario quita su decisión, en vez de
        // dejarlo callado para siempre. El rearme se guarda en la app para que el aviso salga
        // también con el bloqueo total apagado, que por lo demás solo pregunta los nuevos.
        public void AskAgain(string exePath)
        {
            lock (_lock)
            {
                if (_apps.TryGetValue(exePath, out var app))
                {
                    app.IsKnown = false;
                    app.AskedWithoutDeciding = false;
                    app.AskOnNextLaunch = true;
                    PersistApps();
                }
                _launchAlertedUtc.Remove(exePath);
            }
        }

        // «Borrar todas las decisiones» (la cuarta opción del menú de modos): deja a todas las
        // aplicaciones como si PCJ nunca hubiera recibido respuesta. Solo toca las marcas de
        // aviso —si estaba decidida, si se descartó sin responder y el rearme—; los bytes y el
        // histórico de cada programa se quedan igual, porque son estadística, no una decisión.
        // Con soloContar pide cuántas había y no cambia nada: es lo que lee la ventana de
        // confirmación antes de preguntar.
        public int OlvidarMarcasDeApps(bool soloContar = false)
        {
            lock (_lock)
            {
                int contadas = 0;
                foreach (var app in _apps.Values)
                {
                    if (!app.IsKnown && !app.AskedWithoutDeciding && !app.AskOnNextLaunch) continue;
                    contadas++;
                    if (soloContar) continue;
                    app.IsKnown = false;
                    app.AskedWithoutDeciding = false;
                    app.AskOnNextLaunch = true;   // que el aviso salga también con el corte apagado
                }
                if (soloContar) return contadas;
                _launchAlertedUtc.Clear();        // sin el freno de reapertura, el aviso vuelve enseguida
                PersistApps();
                return contadas;
            }
        }

        public TrackedApp? FindApp(string exePath)
        {
            lock (_lock) return _apps.TryGetValue(exePath, out var a) ? a : null;
        }

        // Escribe ya en disco los campos de la configuración adicional, que se ponen desde la
        // ventana de Protección. Es el mismo PersistApps de siempre; sin esto el cambio se
        // quedaría en memoria y al reiniciar el programa la fila volvería vacía.
        public void NoteAppConfig()
        {
            lock (_lock) PersistApps();
        }
    }
}
