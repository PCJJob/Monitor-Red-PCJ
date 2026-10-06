using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text.Json;
using System.Threading.Tasks;
using MonitorRedPCJ.Models;

namespace MonitorRedPCJ.Services
{
    // Gestor de reglas de Windows Firewall vía COM (HNetCfg.FwPolicy2 / FwRule).
    // Las reglas se nombran con prefijo "MRPCJ:" para poder limpiarlas.
    // Si el proceso no es admin, la operación se reintenta lanzando una copia
    // elevada del propio exe con argumentos --fw-apply.
    public class FirewallService
    {
        public const string RulePrefix = "MRPCJ:";

        // Estado guardado por "exe|dir": marca propia para saber qué regla nos toca
        // conservar; el firewall se consulta de verdad cuando hace falta.
        private const int RuleAllowed = 0;
        private const int RuleInstalled = 1;   // regla de bloqueo confirmada en el firewall
        private const int RulePending = 2;     // bloqueo pedido pero UAC cancelado: no hay regla

        // Las clases del firewall se activan por ProgID. Los GUID que se usaban antes
        // no están registrados en esta máquina (REGDB_E_CLASSNOTREG), así que NINGÚN
        // cambio llegaba a aplicarse, ni siquiera en una copia elevada: de ahí que el
        // botón pidiera UAC una y otra vez sin hacer nada.
        private static dynamic NewPolicy()
        {
            var t = Type.GetTypeFromProgID("HNetCfg.FwPolicy2")
                    ?? Type.GetTypeFromCLSID(new Guid("E2B3C97F-6AE1-41AC-817A-F6F92166D7DD"));
            return Activator.CreateInstance(t)!;
        }

        private static dynamic NewRule()
        {
            var t = Type.GetTypeFromProgID("HNetCfg.FwRule")
                    ?? Type.GetTypeFromCLSID(new Guid("2C5BC43E-3369-4C33-AB0C-BE9469677AF4"));
            return Activator.CreateInstance(t)!;
        }

        // Nombres de nuestras reglas tal y como están en el firewall (lectura: no exige
        // permisos de administrador). Se cachea porque enumerar las ~550 reglas de Windows
        // por COM tarda cientos de milisegundos.
        //
        // Cuando la caché se vence, la interfaz NO espera a la lectura nueva: sigue pintando
        // con la lista anterior y la releemos en segundo plano. Eso es lo que quitaba el
        // microcorte cada cuarto de minuto al tener la pestaña de Protección abierta.
        private List<string> _realNames = new();
        // Nombre de nuestra regla -> programa al que apunta de verdad. El nombre se forma solo con
        // el fichero sin extensión («MonitorRedPCJ», «node»…), así que dos copias distintas del
        // mismo programa compiten por la misma regla: la que está en C:\Program Files y la de
        // pruebas de bin\Debug se llaman igual. Sin esta tabla, dar «Permitir» a una dejaba por
        // permitida a la otra, que es exactamente como se cortó la descarga de la lista de malware.
        private Dictionary<string, string> _realPaths = new(StringComparer.OrdinalIgnoreCase);
        private DateTime _realNamesAt = DateTime.MinValue;
        private bool _realNamesOk;
        private bool _namesReading;
        private readonly object _namesLock = new();

        private const double NamesCacheSeconds = 15;

        // Quita comillas y espacios y lo pasa a minúsculas: es la única comparación que vale
        // entre la ruta que escribió el usuario y la que devuelve el COM del firewall.
        public static string RutaReglaNormal(string ruta)
            => (ruta ?? "").Trim().Trim('"').ToLowerInvariant();

        private List<string> ReadNamesNow()
        {
            var found = new List<string>();
            var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            bool ok;
            try
            {
                dynamic policy = NewPolicy();
                foreach (dynamic rule in policy.Rules)
                {
                    string name = (string)rule.Name;
                    if (!name.StartsWith(RulePrefix)) continue;
                    found.Add(name);
                    try
                    {
                        string app = ((string)rule.ApplicationName) ?? "";
                        if (app.Length > 0) paths[name] = RutaReglaNormal(app);
                    }
                    catch { }   // una regla sin programa (por puertos) no aporta nada
                }
                ok = true;
            }
            catch { ok = false; }

            lock (_namesLock)
            {
                _realNames = found;
                _realPaths = paths;
                _realNamesOk = ok;
                _realNamesAt = DateTime.Now;
            }
            return found;
        }

        // A qué ejecutable apunta una regla nuestra que ya está leída. "" si no se sabe: puede
        // que la lectura de rutas fallara o que la regla sea de puertos, sin programa.
        public string RutaRegla(string ruleName)
        {
            lock (_namesLock)
                return _realPaths.TryGetValue(ruleName, out var p) ? p : "";
        }

        // La regla existe Y apunta a este ejecutable. Si no sabemos a qué apunta se acepta por
        // nombre, que era el comportamiento de antes: mejor enseñar de más que quedarse corto.
        public bool ReglaApuntaA(string ruleName, string exePath)
        {
            if (!HasRealRule(ruleName)) return false;
            string ruta = RutaRegla(ruleName);
            return ruta.Length == 0 || ruta == RutaReglaNormal(exePath);
        }

        private List<string> RealRuleNames(bool force = false)
        {
            bool fresh;
            lock (_namesLock)
                fresh = !force && (DateTime.Now - _realNamesAt).TotalSeconds < NamesCacheSeconds;

            if (!fresh)
            {
                bool starter = false;
                lock (_namesLock)
                {
                    if (!_namesReading) { _namesReading = true; starter = true; }
                }
                if (starter)
                {
                    // Con force (justo después de tocar una regla) se espera de verdad:
                    // quien llama necesita leer el estado nuevo.
                    if (force)
                    {
                        try { return ReadNamesNow(); }
                        finally { lock (_namesLock) _namesReading = false; }
                    }
                    _ = Task.Run(() =>
                    {
                        try { ReadNamesNow(); }
                        finally { lock (_namesLock) _namesReading = false; }
                    });
                }
                else if (force)
                {
                    return ReadNamesNow();
                }
            }

            lock (_namesLock) return _realNames;
        }

        // Copia para consultar de una sentada: la lista de Protección pregunta por cien filas
        // seguidas y hacerlo contra el diccionario evita volver a mirar la caché cada vez.
        public HashSet<string> RuleSnapshot()
            => new HashSet<string>(RealRuleNames(), StringComparer.OrdinalIgnoreCase);

        // Cuántas reglas nuestras hay de verdad; -1 si el firewall no las dejó leer.
        public int RealRuleCount()
        {
            var names = RealRuleNames();
            return _realNamesOk ? names.Count : -1;
        }

        public bool HasRealRule(string ruleName)
            => RealRuleNames().Any(n => string.Equals(n, ruleName, StringComparison.OrdinalIgnoreCase));

        // Caduca la caché sin esperar: la próxima lectura la trae en segundo plano. Lo usa el
        // botón «Refrescar» de Protección, que no puede quedarse parado enumerando las quinientas
        // y pico reglas de Windows por el hilo de la interfaz.
        public void InvalidarReglas()
        {
            lock (_namesLock) _realNamesAt = DateTime.MinValue;
        }

        private readonly JsonStore<Dictionary<string, int>> _saved =
            new JsonStore<Dictionary<string, int>>(Paths.AppsFile + ".rules");

        private Dictionary<string, int> _actions; // "exe|dir" -> estado de regla

        public FirewallService()
        {
            _actions = _saved.Load(() => new Dictionary<string, int>());
            HealMarkers();
        }

        // Los marcadores "pendiente" (bloqueo pedido y UAC cancelado) y "instalado"
        // pueden quedar mintiendo cuando el firewall ya no coincide con ellos: al abrir
        // la app se contrastan con las reglas reales y se corrigen, para no mostrar una
        // app como bloqueada cuando en realidad no lo está (ni al revés).
        private void HealMarkers()
        {
            bool hayDudosos = _actions.Any(kv => kv.Value != RuleAllowed);
            if (!hayDudosos) return;

            var names = new HashSet<string>(RealRuleNames(force: true), StringComparer.OrdinalIgnoreCase);
            if (!_realNamesOk) return;   // sin lectura no se puede juzgar nada

            bool changed = false;
            foreach (var key in _actions.Keys.ToList())
            {
                int v = _actions[key];
                if (v == RuleAllowed) continue;
                bool existe = names.Contains(RuleNameFromKey(key));
                int fixedValue = existe ? RuleInstalled : RuleAllowed;
                if (fixedValue != v) { _actions[key] = fixedValue; changed = true; }
            }
            if (changed) _saved.Save(_actions);
        }

        // "exe|dir" -> nombre de regla que le correspondería en el firewall
        private static string RuleNameFromKey(string key)
        {
            int i = key.LastIndexOf('|');
            if (i <= 0) return key;
            string exe = key.Substring(0, i);
            Direction dir = key.EndsWith("|1") ? Direction.Out : Direction.In;
            return RuleName(exe, dir);
        }

        private static string KeyOf(string exePath, Direction dir)
            => exePath.ToLowerInvariant() + "|" + (int)dir;

        public static bool IsAdmin
        {
            get
            {
                try
                {
                    using var id = WindowsIdentity.GetCurrent();
                    return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
                }
                catch { return false; }
            }
        }

        // Acción que ve la interfaz. En modo estricto la salida no depende de nuestros
        // marcadores sino de si existe la regla PERMISO de esa app: si no existe, no sale.
        public RuleAction GetSavedAction(string exePath, Direction dir)
        {
            if (IsStrictOutbound(dir))
                return IsAllowRule(exePath, Direction.Out) ? RuleAction.Allow : RuleAction.Block;

            return _actions.TryGetValue(KeyOf(exePath, dir), out var v) && v != RuleAllowed
                ? RuleAction.Block
                : RuleAction.Allow;
        }

        private static bool IsStrictOutbound(Direction dir)
            => dir == Direction.Out && App.Settings != null && App.Settings.Current.StrictMode;

        // ¿Fue decisión del usuario o simplemente no hay nada marcado?
        public bool HasUserDecision(string exePath, Direction dir)
            => _actions.ContainsKey(KeyOf(exePath, dir));

        // Contadores leídos del firewall, no de nuestras marcas: es lo que puede afirmar
        // la interfaz sin mentir al usuario.
        public int AllowRuleCount() => RealRuleNames().Count(n => n.EndsWith(AllowSuffixOut));
        public int InboundAllowCount() => RealRuleNames().Count(n => n.EndsWith(AllowSuffixIn));
        // Cuántas aplicaciones están cortadas de raíz. Las reglas FINAS (las de «Configuración
        // adicional», que llevan " AJUSTE " en el nombre) no cuentan: una app con el modo fino
        // puesto ya está contada por su regla de siempre, y si no, sería un permiso parcial
        // pintado como si fuera un corte completo.
        public int BlockRuleCount() => RealRuleNames().Count(n =>
            n.StartsWith(RulePrefix) && !n.EndsWith("PERMISO") && !n.Contains(" AJUSTE ") &&
            !EsExcepcion(n));

        // La interfaz se pinta cada segundo y lee reglas y política; esas dos lecturas son
        // lentas (COM y netsh), así que se recalientan fuera del hilo de interfaz.
        public void WarmCaches()
        {
            RealRuleNames(force: true);
            OutboundBlockedNow(force: true);
        }

        // La política de salida se lee con netsh (tarda ~200 ms), así que se cachea igual que
        // las reglas y con la misma regla: al vencer se relee detrás y mientras se usa el valor
        // anterior, para que ningún clic pague el netsh.
        private static bool _policyRead;
        private static bool _policyBlocked;
        private static DateTime _policyReadAt = DateTime.MinValue;
        private static bool _policyReading;
        private static readonly object _policyLock = new();

        public static bool OutboundBlockedNow(bool force = false)
        {
            bool fresh;
            lock (_policyLock)
                fresh = !force && (DateTime.Now - _policyReadAt).TotalSeconds < 15 && _policyRead;

            if (fresh) return _policyBlocked;

            bool starter = false;
            lock (_policyLock)
            {
                if (!_policyReading) { _policyReading = true; starter = true; }
            }

            if (starter)
            {
                if (force)
                {
                    try
                    {
                        bool v = IsOutboundDefaultBlocked();
                        lock (_policyLock) { _policyBlocked = v; _policyRead = true; _policyReadAt = DateTime.Now; }
                        return v;
                    }
                    finally { lock (_policyLock) _policyReading = false; }
                }
                _ = Task.Run(() =>
                {
                    try
                    {
                        bool v = IsOutboundDefaultBlocked();
                        lock (_policyLock) { _policyBlocked = v; _policyRead = true; _policyReadAt = DateTime.Now; }
                    }
                    finally { lock (_policyLock) _policyReading = false; }
                });
            }

            lock (_policyLock) return _policyBlocked;
        }

        // ¿Sabemos realmente leer la política? (si netsh falla, no conviene afirmar nada)
        public static bool PolicyReadable => _policyRead;

        // Mismo trato para la política de ENTRADA: netsh tarda ~200 ms y la tabla de puertos se
        // refresca sola cada 20 s, así que se cachea y, al vencer, se relee detrás sin bloquear.
        private static bool _inRead;
        private static bool _inBlocked;
        private static DateTime _inReadAt = DateTime.MinValue;
        private static bool _inReading;
        private static readonly object _inLock = new();

        public static bool InboundBlockedNow(bool force = false)
        {
            bool fresh;
            lock (_inLock)
                fresh = !force && (DateTime.Now - _inReadAt).TotalSeconds < 15 && _inRead;

            if (fresh) return _inBlocked;

            bool starter = false;
            lock (_inLock)
            {
                if (!_inReading) { _inReading = true; starter = true; }
            }

            if (starter)
            {
                if (force)
                {
                    try
                    {
                        bool v = IsInboundDefaultBlocked();
                        lock (_inLock) { _inBlocked = v; _inRead = true; _inReadAt = DateTime.Now; }
                        return v;
                    }
                    finally { lock (_inLock) _inReading = false; }
                }
                _ = Task.Run(() =>
                {
                    try
                    {
                        bool v = IsInboundDefaultBlocked();
                        lock (_inLock) { _inBlocked = v; _inRead = true; _inReadAt = DateTime.Now; }
                    }
                    finally { lock (_inLock) _inReading = false; }
                });
            }

            lock (_inLock) return _inBlocked;
        }

        // Tras crear o borrar una regla de entrada por puerto, la caché queda anticuada.
        public static void InvalidateInboundCache()
        {
            lock (_inLock) _inReadAt = DateTime.MinValue;
        }

        // Reglas nuestras realmente instaladas en el firewall.
        public int InstalledRuleCount
        {
            get { lock (_actions) return _actions.Count(kv => kv.Value == RuleInstalled); }
        }

        // Último motivo de fallo al elevar, para poder explicárselo al usuario.
        public static string LastError { get; private set; } = "";

        // Avisos de una operación que salió bien pero con excepciones (apps sin archivo, etc.).
        public static string LastWarning { get; private set; } = "";

        // NET_FW_ACTION solo admite 0 (bloqueo) y 1 (permiso). El COM de Windows valida el
        // valor al asignarlo: poner cualquier otro (p. ej. 2) lanza
        // "Value does not fall within the expected range" y la regla no se crea.
        private const int ActionBlock = 0;
        private const int ActionAllow = 1;

        // Añade una regla y comprueba de verdad que Windows la guardó.
        private static void AddRule(dynamic rules, dynamic rule, string name)
        {
            rules.Add(rule);
            try
            {
                object? back = rules.Item(name);
                if (back != null) return;
            }
            catch { }

            foreach (dynamic check in rules)
                if (string.Equals((string)check.Name, name, StringComparison.OrdinalIgnoreCase)) return;

            throw new InvalidOperationException("Windows Firewall no guardó la regla «" + name + "».");
        }

        // Guarda la decisión del usuario y solo pide elevación si hay que tocar el firewall.
        public bool SetRule(string exePath, Direction dir, RuleAction action, bool enabled)
        {
            string key = KeyOf(exePath, dir);
            string name = RuleName(exePath, dir);

            // "Instalada" si lo dice nuestra marca o si de verdad hay una regla con ese nombre:
            // así no se queda uno con la marca desincronizada pidiendo UAC de más.
            bool ruleInstalled = (_actions.TryGetValue(key, out var previous) && previous == RuleInstalled)
                                 || HasRealRule(name);

            _actions[key] = action == RuleAction.Block ? RulePending : RuleAllowed;
            _saved.Save(_actions);

            // Windows ya permite el tráfico saliente por defecto: "Permitir" sin regla
            // previa no requiere cambiar el firewall ni abrir UAC.
            if (action == RuleAction.Allow && !ruleInstalled) return true;

            bool ok = IsAdmin
                ? ApplyLocal(exePath, dir, action, enabled)
                : ApplyElevated(exePath, dir, action, enabled);

            if (ok)
            {
                _actions[key] = action == RuleAction.Block ? RuleInstalled : RuleAllowed;
                _saved.Save(_actions);
                RealRuleNames(force: true);   // el firewall acaba de cambiar
            }
            return ok;
        }

        // Reajusta un marcador propio a lo que hay de verdad en el firewall, sin hablar con
        // Windows. Lo usa la configuración adicional, que escribe y borra la regla de siempre
        // dentro de su propio lote elevado: sin esto el marcador seguiría diciendo que hay un
        // bloqueo puesto y el siguiente clic en la pastilla abriría una ventana de UAC vacía.
        public void RefreshMarker(string exePath, Direction dir)
        {
            string key = KeyOf(exePath, dir);
            if (!_actions.ContainsKey(key)) return;
            bool existe = HasRealRule(RuleName(exePath, dir));
            _actions[key] = existe ? RuleInstalled : RuleAllowed;
            _saved.Save(_actions);
        }

        public bool ApplyLocal(string exePath, Direction dir, RuleAction action, bool enabled)
        {
            try
            {
                dynamic policy = NewPolicy();

                string ruleName = RuleName(exePath, dir);
                dynamic rules = policy.Rules;

                // Elimina regla previa con el mismo nombre
                try { rules.Remove(ruleName); } catch { }

                if (action == RuleAction.Allow) return true; // sin regla = permitir (política por defecto)

                dynamic rule = NewRule();
                rule.Name = ruleName;
                rule.Description = "Bloqueo aplicado por Monitor de Red PCJ";
                rule.ApplicationName = exePath;
                rule.Direction = dir == Direction.In ? 1 : 2;   // NET_FW_RULE_DIR_IN/OUT
                rule.Action = ActionBlock;                      // NET_FW_ACTION_BLOCK
                // NO tocar rule.Protocol: asignarle 255 ("cualquiera") hace que Windows
                // guarde la regla con protocolo 255 literal y NUNCA coincida con el
                // trafico real (se ve en netsh como "Protocolo: 255" y no bloquea).
                // Sin asignar, la regla queda en "Protocolo: Cualquiera" y sí bloquea.
                rule.Enabled = enabled;
                // NET_FW_PROFILE2_ALL: así la regla aplica en los tres perfiles.
                rule.Profiles = 0x7FFFFFFF;
                // Sin RemoteAddresses ni LocalPorts: el firewall los deja en "cualquiera",
                // que es justo lo que queremos para bloquear una app completa.
                rules.Add(rule);

                // Comprobación real: si el firewall no la guardó, lo decimos, no fingimos éxito
                foreach (dynamic check in rules)
                {
                    if (string.Equals((string)check.Name, ruleName, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                LastError = "Windows Firewall aceptó la regla pero no aparece en la lista.";
                return false;
            }
            catch (Exception ex)
            {
                LastError = "Windows Firewall no aceptó la regla: " + ex.Message;
                return false;
            }
        }

        private bool ApplyElevated(string exePath, Direction dir, RuleAction action, bool enabled)
        {
            var payload = new { exe = exePath, dir = (int)dir, act = (int)action, en = enabled };
            string b64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)));
            return RunElevated($"--fw-apply {b64}");
        }

        // Lanza una copia elevada de sí mismo y espera el resultado real.
        // No se usa el código de salida del proceso: al venir de otro nivel de
        // integridad, a veces el padre no puede leerlo y parecía un fallo inexistente.
        private static bool RunElevated(string args)
        {
            LastError = "";
            LastWarning = "";
            string resultFile = Path.Combine(Path.GetTempPath(), "mrpcj-fw-" + Guid.NewGuid().ToString("N") + ".result");
            try
            {
                string self = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule.FileName;
                var psi = new ProcessStartInfo
                {
                    FileName = self,
                    Arguments = args + " \"" + resultFile + "\"",
                    UseShellExecute = true,
                    Verb = "runas", // fuerza UAC
                };
                using var p = Process.Start(psi);
                if (p == null)
                {
                    LastError = "Windows no permitió abrir la ventana de aprobación.";
                    return false;
                }
                // La copia elevada de --fw-strict o de --fw-forget puede tardar mucho: una regla
                // COM tarda ~0,5 s y el repaso completo son decenas de apps.
                int ms = (args.StartsWith("--fw-strict") || args.StartsWith("--fw-forget")) ? 240000 : 60000;
                if (!p.WaitForExit(ms))
                {
                    LastError = "La ventana de aprobación quedó sin responder y se canceló el cambio.";
                    return false;
                }

                string detail = "";
                try
                {
                    if (File.Exists(resultFile))
                    {
                        detail = File.ReadAllText(resultFile).Trim();
                        try { File.Delete(resultFile); } catch { }
                    }
                }
                catch { }

                if (detail == "ok" || detail.StartsWith("ok\t", StringComparison.Ordinal))
                {
                    int tab = detail.IndexOf('\t');
                    LastWarning = tab >= 0 ? detail.Substring(tab + 1) : "";
                    return true;
                }
                LastError = detail.Length > 0
                    ? detail
                    : "No llegó la confirmación de la copia elevada (¿se cerró o se canceló la ventana de UAC?).";
                return false;
            }
            catch (Exception ex)
            {
                LastError = ex is System.ComponentModel.Win32Exception
                    ? "Se canceló la ventana de aprobación de administrador."
                    : "No se pudo abrir la copia elevada: " + ex.Message;
                try { if (File.Exists(resultFile)) File.Delete(resultFile); } catch { }
                return false;
            }
        }

        private static void WriteResult(string resultFile, string text)
        {
            if (string.IsNullOrEmpty(resultFile)) return;
            try { File.WriteAllText(resultFile, text); } catch { }
        }

        // Modo auxiliar: se ejecuta desde el proceso elevado y aplica la regla localmente
        public static int RunApplyMode(string base64Payload, string resultFile)
        {
            try
            {
                Paths.Ensure();
                var doc = JsonDocument.Parse(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(base64Payload)));
                var r = doc.RootElement;
                var svc = new FirewallService();
                bool ok = svc.ApplyLocal(r.GetProperty("exe").GetString()!,
                    (Direction)r.GetProperty("dir").GetInt32(),
                    (RuleAction)r.GetProperty("act").GetInt32(),
                    r.GetProperty("en").GetBoolean());
                WriteResult(resultFile, ok ? "ok"
                    : (LastError.Length > 0 ? LastError : "Windows Firewall rechazó el cambio de regla."));
                return ok ? 0 : 1;
            }
            catch (Exception ex)
            {
                WriteResult(resultFile, "La copia elevada falló: " + ex.Message);
                return 2;
            }
        }

        public static string RuleName(string exePath, Direction dir)
            => $"{RulePrefix} {Path.GetFileNameWithoutExtension(exePath)} {(dir == Direction.In ? "ENTRADA" : "SALIDA")}";

        // Regla que da acceso a una app concreta. Con el bloqueo total hace falta para la
        // SALIDA; para la ENTRADA siempre hace falta, porque Windows ya corta lo entrante
        // por defecto: sin regla propia la app no escucha nada.
        public static string AllowRuleName(string exePath, Direction dir = Direction.Out)
            => $"{RulePrefix} {Path.GetFileNameWithoutExtension(exePath)} " +
               (dir == Direction.In ? "ENTRADA PERMISO" : "SALIDA PERMISO");

        public const string AllowSuffixOut = "SALIDA PERMISO";
        public const string AllowSuffixIn = "ENTRADA PERMISO";

        // ---------- Bloqueo de un puerto concreto en ENTRADA (herramienta Puertos expuestos) ----------

        // Nombre propio para no pisar las reglas por aplicación. Acaba en "(entrada)", que no
        // coincide ni con AllowSuffixIn ("ENTRADA PERMISO") ni con el sufijo de las reglas de
        // app, así que el asistente de salida y el recuento de permisos no las confunden.
        public static string PortRuleName(int port, bool tcp)
            => $"{RulePrefix} Puerto {port} {(tcp ? "TCP" : "UDP")} (entrada)";

        // ¿Ya existe una regla nuestra que corta este puerto en entrada?
        public bool PortBlocked(int port, bool tcp) => HasRealRule(PortRuleName(port, tcp));

        // Crea (o quita) la regla de bloqueo de entrada para un puerto, dentro de este proceso.
        // A diferencia de las reglas por app, aquí SÍ se toca Protocol y LocalPorts: es justo lo
        // que define el puerto. El aviso de "no tocar Protocol" de ApplyLocal es solo para el
        // valor 255 ("cualquiera"), que rompe la coincidencia; 6 (TCP) y 17 (UDP) son válidos.
        public bool BlockPortInLocal(int port, bool tcp, bool block)
        {
            try
            {
                dynamic policy = NewPolicy();
                dynamic rules = policy.Rules;
                string name = PortRuleName(port, tcp);
                try { rules.Remove(name); } catch { }

                if (!block) { InvalidateInboundCache(); return true; }

                dynamic rule = NewRule();
                rule.Name = name;
                rule.Description = "Entrada cortada por Monitor de Red PCJ (Puertos expuestos)";
                rule.Direction = 1;                 // NET_FW_RULE_DIR_IN
                rule.Action = ActionBlock;          // bloqueo
                rule.Protocol = tcp ? 6 : 17;       // 6=TCP, 17=UDP
                rule.LocalPorts = port.ToString();
                rule.Enabled = true;
                rule.Profiles = 0x7FFFFFFF;         // los tres perfiles
                rule.Grouping = "Monitor de Red PCJ";
                AddRule(rules, rule, name);
                InvalidateInboundCache();
                RealRuleNames(force: true);
                return true;
            }
            catch (Exception ex)
            {
                LastError = "Windows Firewall no aceptó la regla del puerto: " + ex.Message;
                return false;
            }
        }

        // Puerta de entrada desde la interfaz: si hay permisos, lo hace aquí; si no, lanza la
        // copia elevada y espera el resultado real, igual que las demás operaciones del firewall.
        public bool BlockPortIn(int port, bool tcp, bool block)
        {
            if (IsAdmin) return BlockPortInLocal(port, tcp, block);
            var payload = new { port, tcp, block };
            string b64 = Convert.ToBase64String(
                System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)));
            bool ok = RunElevated($"--fw-port {b64}");
            if (ok) { RealRuleNames(force: true); InvalidateInboundCache(); }
            return ok;
        }

        // Se ejecuta dentro de la copia elevada (UAC) lanzada por BlockPortIn.
        public static int RunPortMode(string base64Payload, string resultFile)
        {
            try
            {
                Paths.Ensure();
                var doc = JsonDocument.Parse(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(base64Payload)));
                var r = doc.RootElement;
                var svc = new FirewallService();
                bool ok = svc.BlockPortInLocal(r.GetProperty("port").GetInt32(),
                    r.GetProperty("tcp").GetBoolean(),
                    r.GetProperty("block").GetBoolean());
                WriteResult(resultFile, ok ? "ok"
                    : (LastError.Length > 0 ? LastError : "Windows Firewall rechazó la regla del puerto."));
                return ok ? 0 : 1;
            }
            catch (Exception ex)
            {
                WriteResult(resultFile, "La copia elevada falló: " + ex.Message);
                return 2;
            }
        }

        // ---------- Reglas sueltas: excepciones del sistema y configuración adicional ----------
        //
        // Todo lo que no sea "esta app sale o no sale" (los permisos de siempre) se construye
        // aquí, a partir de una descripción de regla. Se usan propiedades y no campos porque
        // System.Text.Json solo serializa propiedades públicas, y el payload viaja a la copia
        // elevada convertido a JSON.

        public class ReglaSpec
        {
            public string n { get; set; } = "";       // nombre de la regla
            public string d { get; set; } = "";       // descripción que ve Windows
            public int dir { get; set; } = 2;         // 1 = entrada, 2 = salida
            public int act { get; set; } = 1;         // 0 = bloqueo, 1 = permiso
            public string proto { get; set; } = "";   // "", "6" (TCP), "17" (UDP)
            public string lp { get; set; } = "";      // puertos locales
            public string rp { get; set; } = "";      // puertos remotos
            public string ra { get; set; } = "";      // direcciones remotas
            public string exe { get; set; } = "";     // programa
            public int orden { get; set; } = 0;       // orden de juego: gana el número menor
        }

        // Las dos cifras de orden que usa PCJ. Windows resuelve los empates dando prioridad al
        // bloqueo, así que un "permite solo estos puertos" perdería contra un "bloquea todo"
        // escrito igual. Bajar el número del permiso es lo que le deja ganar a horas ciertas.
        public const int OrdenPermiso = 10;
        public const int OrdenBloqueo = 20;

        // Las reglas de las excepciones del sistema (actualizaciones de Windows, DHCP, DNS,
        // escritorio remoto…) y la de «dejar hablar la red local». Todas caben bajo el mismo
        // token " EXC " a propósito, y no es un detalle de estética:
        //
        //   · BlockRuleCount() llama «bloqueada» a toda regla nuestra que no termine en PERMISO.
        //     Sin excluir estas, cada excepción activada saldría en la banda de Protección como
        //     si fuera una aplicación cortada.
        //   · ToggleLocal pausa lo nuestro al quitar la protección. Un permiso del sistema no es
        //     una regla de aplicación: pausarlo, y luego reactivarlo, encendería excepciones que
        //     el usuario tenía apagadas.
        //   · CleanupStaleRules borra la regla cuyo programa ya no está en disco. Estas no llevan
        //     programa (son por puerto, como las de TinyWall): sin excluirlas, el limpiado las
        //     borraría todas en cuanto se pulsara «Limpiar reglas».
        //
        // Tampoco terminan en "SALIDA PERMISO" ni en "ENTRADA PERMISO", que es como el asistente
        // de salida y los perfiles reconocen los permisos normales de una aplicación.
        public static string ExcRuleName(string id, int n) => $"{RulePrefix} EXC {id} #{n}";
        public const string ExcToken = " EXC ";
        public static bool EsExcepcion(string nombreRegla) => nombreRegla.Contains(ExcToken);

        // La salida libre hacia la red local es una excepción más del mismo tipo.
        public static string LocalTrafficRuleName() => ExcRuleName("redlocal", 0);

        // Nombres de las reglas FINAS de una aplicación. Llevan "AJUSTE" en medio y terminan en
        // "FINO"/"FINA", y eso no es un capricho: el asistente de salida, el recuento de
        // permitidas y los perfiles reconocen sus reglas por EndsWith("SALIDA PERMISO"). Si una
        // regla fina terminara igual, contaría como un permiso de salida normal y el bloqueo
        // total se la llevaría puesta.
        //
        // La lista es FIJA y se borra entera antes de crear nada: así una configuración que pasa
        // de "sólo puertos" a "sin restricciones" no deja reglas huérfanas cortando el tráfico.
        // Cada sufijo es una regla concreta que AppAjustes sabe escribir.
        public static readonly string[] AjusteSufijos =
        {
            "SALIDA BLOQUEO FINA",       // no sale de ninguna manera
            "SALIDA PERMISO FINO",       // sale por cualquier protocolo y cualquier puerto
            "SALIDA TCP PERMISO FINO",   // sale por TCP, solo por los puertos que diga la regla
            "SALIDA UDP PERMISO FINO",   // sale por UDP
            "SALIDA TCP BLOQUEO FINO",   // la parte de TCP que NO está en la lista permitida
            "SALIDA UDP BLOQUEO FINO",   // la parte de UDP que NO está en la lista permitida
            "SALIDA LOCAL PERMISO FINO", // salida hacia la red local
            "SALIDA LOCAL BLOQUEO FINO", // salida hacia internet, cortada
            "ENTRADA BLOQUEO FINA",      // nadie puede llamarle
        };

        public static string AjusteRuleName(string exePath, string sufijo)
            => $"{RulePrefix} {Path.GetFileNameWithoutExtension(exePath)} AJUSTE {sufijo}";

        public static List<string> AjusteRuleNames(string exePath)
            => AjusteSufijos.Select(s => AjusteRuleName(exePath, s)).ToList();

        private static void BuildRule(dynamic rules, ReglaSpec s)
        {
            dynamic rule = NewRule();
            rule.Name = s.n;
            rule.Description = s.d;
            rule.Direction = s.dir;
            rule.Action = s.act;
            rule.Enabled = true;
            rule.Profiles = 0x7FFFFFFF;
            rule.Grouping = "Monitor de Red PCJ";
            // RulesOrder es la interfaz INetFwRule3 (Windows 10 1709 en adelante). Si el equipo
            // no la tiene, asignarla lanzaría y la regla entera se perdería: se prueba aparte
            // para que lo único que se pierda sea el orden, que aquí es un detalle fino.
            if (s.orden != 0) { try { rule.RulesOrder = s.orden; } catch { } }
            // Protocolo NUNCA a 255: Windows guarda un filtro que no casa con tráfico real.
            // Solo se asigna cuando la regla dice uno concreto (6 TCP / 17 UDP).
            if (s.proto.Length > 0) rule.Protocol = int.Parse(s.proto);
            if (s.lp.Length > 0) rule.LocalPorts = s.lp;
            if (s.rp.Length > 0) rule.RemotePorts = s.rp;
            if (s.ra.Length > 0) rule.RemoteAddresses = s.ra;
            if (s.exe.Length > 0) rule.ApplicationName = s.exe;
            AddRule(rules, rule, s.n);
        }

        /// Quita las reglas nombradas y crea las nuevas, dentro de este proceso. Un fallo en una
        /// regla no tumba el resto: se anota en el aviso, igual que en el bloqueo total.
        public bool ApplySpecsLocal(IReadOnlyList<string> aBorrar, IReadOnlyList<ReglaSpec> aCrear,
                                    out string aviso)
        {
            aviso = "";
            try
            {
                dynamic policy = NewPolicy();
                dynamic rules = policy.Rules;

                foreach (string n in aBorrar)
                    try { rules.Remove(n); } catch { }

                var fallidas = new List<string>();
                foreach (var s in aCrear)
                {
                    try { BuildRule(rules, s); }
                    catch (Exception one)
                    {
                        fallidas.Add(s.n + " (" + one.Message + ")");
                        try { rules.Remove(s.n); } catch { }
                    }
                }
                RealRuleNames(force: true);
                InvalidateInboundCache();
                if (fallidas.Count > 0) aviso = string.Join("; ", fallidas);
                return fallidas.Count < aCrear.Count || aCrear.Count == 0;
            }
            catch (Exception ex)
            {
                LastError = "Windows Firewall no aceptó el cambio de reglas: " + ex.Message;
                aviso = "";
                return false;
            }
        }

        // Los modos de fotografía (--*-preview) montan los servicios de verdad, sondeo incluido,
        // para que la lista salga con los procesos de este PC. Con esta bandera puesta ninguna
        // escritura de reglas llega a Windows: una foto del diseño no tiene por qué tocar la
        // máquina, ni aunque en el apps.json de verdad hubiera aplicaciones heredando reglas.
        public static bool EscrituraProhibida;

        public bool ApplySpecs(IReadOnlyList<string> aBorrar, IReadOnlyList<ReglaSpec> aCrear)
        {
            if (EscrituraProhibida) return false;
            if (IsAdmin)
            {
                bool ok = ApplySpecsLocal(aBorrar, aCrear, out string aviso);
                if (ok && aviso.Length > 0) LastWarning = aviso;
                return ok;
            }
            var payload = new { borrar = aBorrar, crear = aCrear };
            string b64 = Convert.ToBase64String(
                System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)));
            bool r = RunElevated($"--fw-rules {b64}");
            if (r) { RealRuleNames(force: true); InvalidateInboundCache(); }
            return r;
        }

        // Se ejecuta dentro de la copia elevada (UAC) lanzada por ApplySpecs.
        public static int RunRulesMode(string base64Payload, string resultFile)
        {
            try
            {
                Paths.Ensure();
                var doc = JsonDocument.Parse(System.Text.Encoding.UTF8.GetString(
                    Convert.FromBase64String(base64Payload)));
                var r = doc.RootElement;
                var borrar = new List<string>();
                if (r.TryGetProperty("borrar", out var bd))
                    foreach (var x in bd.EnumerateArray()) borrar.Add(x.GetString() ?? "");
                var crear = new List<ReglaSpec>();
                if (r.TryGetProperty("crear", out var cd))
                    foreach (var x in cd.EnumerateArray())
                        crear.Add(x.Deserialize<ReglaSpec>() ?? new ReglaSpec());

                var svc = new FirewallService();
                bool ok = svc.ApplySpecsLocal(borrar, crear, out string aviso);
                string res = ok ? "ok"
                    : (LastError.Length > 0 ? LastError : "Windows Firewall rechazó el cambio de reglas.");
                if (ok && aviso.Length > 0) res = "ok\t" + aviso;
                WriteResult(resultFile, res);
                return ok ? 0 : 1;
            }
            catch (Exception ex)
            {
                WriteResult(resultFile, "La copia elevada falló: " + ex.Message);
                return 2;
            }
        }

        // ---------- Modo estricto: bloquear toda la salida y permitir app por app ----------
        private static (int code, string text) RunNetsh(string args)
        {
            try
            {
                var psi = new ProcessStartInfo("netsh.exe", args)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p == null) return (-1, "");
                string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit(15000);
                return (p.HasExited ? p.ExitCode : -1, o);
            }
            catch (Exception ex)
            {
                return (-1, ex.Message);
            }
        }

        // Estado real de la política de salida. Se lee con netsh porque la propiedad COM
        // DefaultOutboundAction devuelve valores que no cuadran con lo que aplica Windows.
        public static bool IsOutboundDefaultBlocked()
        {
            var (code, text) = RunNetsh("advfirewall show allprofiles firewallpolicy");
            if (code != 0 || string.IsNullOrEmpty(text)) return false;
            int bloqueados = System.Text.RegularExpressions.Regex.Matches(text, "BlockOutbound").Count;
            return bloqueados >= 3;   // dominio + privado + público
        }

        // Igual para la ENTRADA. Es lo que necesita Puertos expuestos para saber si un puerto
        // que escucha en 0.0.0.0 es de verdad alcanzable desde la red o si el firewall ya lo
        // está cortando. Windows trae BlockInbound de fábrica en los tres perfiles; si alguien
        // lo cambió, aquí se nota.
        public static bool IsInboundDefaultBlocked()
        {
            var (code, text) = RunNetsh("advfirewall show allprofiles firewallpolicy");
            if (code != 0 || string.IsNullOrEmpty(text)) return false;
            int bloqueados = System.Text.RegularExpressions.Regex.Matches(text, "BlockInbound").Count;
            return bloqueados >= 3;   // dominio + privado + público
        }

        // Los tres perfiles de Windows Firewall, en el nombre que entiende netsh.
        private static readonly string[] NetshProfiles = { "domainprofile", "privateprofile", "publicprofile" };

        // "BlockInbound,AllowOutbound" -> ("BlockInbound", "AllowOutbound").
        // netsh imprime la etiqueta traducida ("Directiva de firewall"), pero los valores
        // siempre salen en inglés, así que se busca la pareja y no el nombre del campo.
        private static (string inbound, string outbound) ReadPolicyLocal(string profile)
        {
            var (code, text) = RunNetsh("advfirewall show " + profile + " firewallpolicy");
            var m = System.Text.RegularExpressions.Regex.Match(text ?? "",
                @"([A-Za-z]*Inbound(?:Always)?)\s*,\s*([A-Za-z]*Outbound)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!m.Success) return ("", "");
            return (m.Groups[1].Value, m.Groups[2].Value);
        }

        // Escribe la política de un perfil probando las dos formas de invocar netsh.
        // La ayuda de "set <perfil>" documenta "firewallpolicy blockinbound,allowoutbound"
        // (separado por espacios); se añade la variante con "=" por si algún build de
        // Windows solo la acepta a ella. Si la primera funciona no se ejecuta la segunda.
        private static (int code, string text) SetPolicyLocal(string profile, string pair)
        {
            var first = RunNetsh("advfirewall set " + profile + " firewallpolicy " + pair);
            if (first.code == 0) return first;
            var second = RunNetsh("advfirewall set " + profile + " firewallpolicy=" + pair);
            return second.code == 0 ? second : first;
        }

        // Aplicado dentro del proceso elevado: cambia SOLO la acción de salida de cada perfil,
        // conservando la de entrada que tuviera.
        // Sintaxis real de netsh (comprobada con "set domainprofile" a secas, que imprime el
        // uso): los parámetros de "set <perfil>" van SEPARADOS POR ESPACIOS, no con "=", y el
        // parámetro es firewallpolicy con la pareja "entrada,salida". Con "=" o con el
        // inexistente "firewallruleoutbound=" netsh responde "Un valor especificado no es válido".
        public static bool ApplyOutboundDefaultLocal(bool block)
        {
            _policyReadAt = DateTime.MinValue;   // la caché queda anticuada
            string wanted = block ? "blockoutbound" : "allowoutbound";

            foreach (var profile in NetshProfiles)
            {
                var (inb, outb) = ReadPolicyLocal(profile);
                if (string.IsNullOrEmpty(outb))
                {
                    LastError = "No se pudo leer la política de salida del perfil " + profile +
                                " de Windows Firewall.";
                    return false;
                }
                if (string.IsNullOrEmpty(inb)) inb = "blockinbound";
                if (string.Equals(outb, wanted, StringComparison.OrdinalIgnoreCase)) continue;

                var (code, text) = SetPolicyLocal(profile, inb + "," + wanted);
                if (code != 0)
                {
                    LastError = "netsh rechazó el cambio de política (" + profile + "): " +
                                (text ?? "").Split('\n')
                                    .FirstOrDefault(l => l.Trim().Length > 0)?.Trim();
                    return false;
                }
            }

            _policyReadAt = DateTime.MinValue;   // la caché queda anticuada
            // Comprobación real: leemos otra vez en vez de fiarnos del código de salida.
            if (IsOutboundDefaultBlocked() != block)
            {
                LastError = "Windows aceptó el cambio pero la política de salida sigue igual.";
                return false;
            }
            return true;
        }

        // Activa o desactiva el bloqueo total. Con allow se crean reglas PERMISO para las
        // apps que el usuario aprobó en el repaso previo.
        public bool SetStrictMode(bool on, IEnumerable<string>? allow = null)
        {
            var allowed = (allow ?? Enumerable.Empty<string>()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var payload = new { on, allow = allowed };
            string b64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)));

            bool ok = IsAdmin ? StrictLocal(on, allowed) : RunElevated($"--fw-strict {b64}");
            if (ok)
            {
                App.Settings.Current.StrictMode = on;
                App.Settings.Current.ProtectionEnabled = on || App.Settings.Current.ProtectionEnabled;
                App.Settings.Save();
                // La copia elevada tocó los marcadores en su propio proceso: los leemos otra vez.
                ReloadMarkers();
                RealRuleNames(force: true);
                _policyReadAt = DateTime.MinValue;
            }
            return ok;
        }

        // Recarga los marcadores desde disco (los escribe la copia elevada del exe).
        // Se mantiene la misma instancia de diccionario: es el objeto de bloqueo de la clase.
        private void ReloadMarkers()
        {
            lock (_actions)
            {
                try
                {
                    var fresh = _saved.Load(() => new Dictionary<string, int>());
                    _actions.Clear();
                    foreach (var kv in fresh) _actions[kv.Key] = kv.Value;
                }
                catch { }
            }
        }

        // Ejecutado con permisos: primero las reglas de permiso y solo al final la política
        // de salida. Así, si algo falla, el equipo no se queda cortado sin ninguna app permitida.
        public bool StrictLocal(bool on, List<string> allow)
        {
            var created = new List<string>();
            try
            {
                dynamic policy = NewPolicy();
                dynamic rules = policy.Rules;

                if (on)
                {
                    var wanted = new HashSet<string>(allow, StringComparer.OrdinalIgnoreCase);
                    LastWarning = "";
                    var avisos = new List<string>();

                    foreach (var exe in allow)
                    {
                        string name = AllowRuleName(exe);

                        // Una ruta que ya no existe no puede tener permiso: se avisa y se
                        // sigue con las demás en vez de abortar todo el repaso.
                        if (!File.Exists(exe))
                        {
                            avisos.Add(Path.GetFileName(exe) + " (el archivo ya no existe en esa ruta)");
                            continue;
                        }

                        try
                        {
                            try { rules.Remove(name); } catch { }
                            try { rules.Remove(RuleName(exe, Direction.Out)); } catch { }

                            dynamic rule = NewRule();
                            rule.Name = name;
                            rule.Description = "Permitido por el usuario en Monitor de Red PCJ";
                            rule.ApplicationName = exe;
                            rule.Direction = 2;   // SALIDA
                            rule.Action = ActionAllow;
                            rule.Enabled = true;
                            rule.Profiles = 0x7FFFFFFF;
                            rule.Grouping = "Monitor de Red PCJ";
                            AddRule(rules, rule, name);
                            created.Add(name);

                            _actions[KeyOf(exe, Direction.Out)] = RuleInstalled;
                        }
                        catch (Exception exRule)
                        {
                            avisos.Add(Path.GetFileName(exe) + " (" + exRule.Message + ")");
                        }
                    }

                    // Permisos que ya no están en la lista: quien los quite aquí se queda
                    // sin salida, así que la regla sobrante se borra.
                    var sobrantes = new List<string>();
                    foreach (dynamic r in rules)
                    {
                        string n = (string)r.Name;
                        if (!n.StartsWith(RulePrefix) || !n.EndsWith(AllowSuffixOut)) continue;
                        string appPath = ((string)r.ApplicationName) ?? "";
                        if (!wanted.Contains(appPath)) sobrantes.Add(n);
                    }
                    foreach (var n in sobrantes) { try { rules.Remove(n); created.Remove(n); } catch { } }

                    _saved.Save(_actions);
                    if (avisos.Count > 0) LastWarning = string.Join(" · ", avisos);

                    if (created.Count == 0)
                    {
                        // Sin ni un solo permiso no tiene sentido cortar la salida: dejaría
                        // el equipo desconectado del todo, incluida la ventana de aviso.
                        LastError = "No se pudo crear ningún permiso de salida"
                            + (avisos.Count > 0 ? ": " + LastWarning : ".");
                        return false;
                    }

                    if (!ApplyOutboundDefaultLocal(true))
                    {
                        // No se pudo cortar por defecto: deshacemos los permisos para no dejar
                        // reglas sueltas que parezcan importantes.
                        foreach (var n in created) { try { rules.Remove(n); } catch { } }
                        foreach (var exe in allow) _actions[KeyOf(exe, Direction.Out)] = RuleAllowed;
                        _saved.Save(_actions);
                        RealRuleNames(force: true);
                        return false;
                    }
                }
                else
                {
                    // Primero devolvemos la salida libre; después, sin prisa, quitamos nuestros permisos.
                    if (!ApplyOutboundDefaultLocal(false)) return false;

                    var permisos = new List<string>();
                    foreach (dynamic r in rules)
                    {
                        string n = (string)r.Name;
                        if (n.StartsWith(RulePrefix) && n.EndsWith(AllowSuffixOut)) permisos.Add(n);
                    }
                    foreach (var n in permisos) { try { rules.Remove(n); } catch { } }

                    // Sin bloqueo total, un marcador "instalado" que ya no tenga su regla
                    // de bloqueo deja de significar bloqueado: se corrige contra el firewall.
                    RealRuleNames(force: true);
                    var vivas = new HashSet<string>(_realNames, StringComparer.OrdinalIgnoreCase);
                    foreach (var key in _actions.Keys.ToList())
                    {
                        if (!key.EndsWith("|" + (int)Direction.Out)) continue;
                        if (_actions[key] == RuleInstalled && !vivas.Contains(RuleNameFromKey(key)))
                            _actions[key] = RuleAllowed;
                    }
                    _saved.Save(_actions);
                }

                RealRuleNames(force: true);
                return true;
            }
            catch (Exception ex)
            {
                // Se guarda el tipo de excepción porque el mensaje COM en inglés no dice
                // en qué paso se rompió el cambio de modo.
                LastError = "Windows Firewall no aceptó el cambio de modo: " + ex.Message
                            + " [" + ex.GetType().Name + "]";
                return false;
            }
        }

        public static int RunStrictMode(string base64Payload, string resultFile)
        {
            try
            {
                Paths.Ensure();
                var doc = JsonDocument.Parse(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(base64Payload)));
                var r = doc.RootElement;
                bool on = r.GetProperty("on").GetBoolean();
                var allow = new List<string>();
                if (r.TryGetProperty("allow", out var arr))
                    foreach (var item in arr.EnumerateArray())
                    {
                        var s = item.GetString();
                        if (!string.IsNullOrEmpty(s)) allow.Add(s);
                    }

                var svc = new FirewallService();
                bool ok = svc.StrictLocal(on, allow);
                WriteResult(resultFile, ok
                    ? (string.IsNullOrEmpty(LastWarning) ? "ok" : "ok\t" + LastWarning)
                    : (LastError.Length > 0 ? LastError : "Windows Firewall rechazó el cambio de modo."));
                return ok ? 0 : 1;
            }
            catch (Exception ex)
            {
                WriteResult(resultFile, "La copia elevada falló: " + ex.Message);
                return 2;
            }
        }

        // ---------- Borrar todas las decisiones ----------

        // La regla que este borrado deja en su sitio: el propio monitor necesita salida para
        // bajar la lista de malware y para comprobar sus reglas, y eso no es una decisión del
        // usuario sino la herramienta misma. Tampoco se tocan las excepciones del sistema
        // (llevan " EXC "), los bloqueos de puerto a mano ni la «Configuración adicional»
        // (" AJUSTE "), que el usuario configura apartado por apartado.
        private static string ReglaPropia()
        {
            string yo = Environment.ProcessPath ?? "";
            return yo.Length > 0 ? AllowRuleName(yo, Direction.Out) : "";
        }

        private static bool EsDecision(string nombre, string mia)
            => nombre.StartsWith(RulePrefix) && !EsExcepcion(nombre) &&
               !nombre.Contains(" AJUSTE ") && !nombre.Contains(" Puerto ") &&
               !string.Equals(nombre, mia, StringComparison.OrdinalIgnoreCase);

        // Cuántas marcas de decisión hay guardadas y cuántas reglas se llevaría el borrado.
        // Los dos solo leen: los pide la ventana de confirmación para poder decir cifras
        // exactas antes de tocar nada.
        public int DecisionCount()
        {
            lock (_actions) return _actions.Count;
        }

        public int DecisionRulesCount()
        {
            string mia = ReglaPropia();
            return RealRuleNames().Count(n => EsDecision(n, mia));
        }

        // Puerta de entrada desde la interfaz: borra aquí si hay permisos y si no lanza la
        // copia elevada. Después vuelve a leer marcadores y reglas, porque la copia elevada los
        // tocó en su propio proceso (lo mismo que hace SetStrictMode).
        public bool ForgetDecisions()
        {
            LastWarning = "";
            bool ok = IsAdmin ? ForgetLocal() : RunElevated("--fw-forget 1");
            if (ok)
            {
                ReloadMarkers();
                RealRuleNames(force: true);
                _policyReadAt = DateTime.MinValue;
            }
            return ok;
        }

        // Ejecutado con permisos. Primero se quitan las reglas y al final se borran las marcas:
        // si algo se corta por el camino, los marcadores siguen contando la verdad de lo que
        // hay en el firewall en vez de decir que ya no hay decisiones.
        public bool ForgetLocal()
        {
            if (EscrituraProhibida)
            {
                LastError = "Este proceso es de comprobación y no puede tocar el firewall.";
                return false;
            }
            try
            {
                dynamic policy = NewPolicy();
                dynamic rules = policy.Rules;
                string mia = ReglaPropia();

                var borrar = new List<string>();
                foreach (dynamic r in rules)
                {
                    string n = (string)r.Name;
                    if (!EsDecision(n, mia)) continue;
                    // Las reglas se nombran por fichero sin ruta, así que el permiso que PCJ se
                    // dio a sí mismo puede estar apuntando a otra copia suya (la de pruebas o la
                    // instaladora anterior). Ninguna se borra: todas son el monitor pidiendo
                    // salida, no una decisión sobre una aplicación.
                    string app = ((string)r.ApplicationName) ?? "";
                    if (app.EndsWith("MonitorRedPCJ.exe", StringComparison.OrdinalIgnoreCase)) continue;
                    borrar.Add(n);
                }

                foreach (var n in borrar) { try { rules.Remove(n); } catch { } }

                lock (_actions)
                {
                    _actions.Clear();
                    _saved.Save(_actions);
                }

                RealRuleNames(force: true);
                int quedan = DecisionRulesCount();
                if (quedan > 0)
                    LastWarning = "Windows dejó " + quedan +
                        (quedan == 1 ? " regla por borrar." : " reglas por borrar.");
                return true;
            }
            catch (Exception ex)
            {
                LastError = "Windows Firewall no aceptó borrar las decisiones: " + ex.Message
                            + " [" + ex.GetType().Name + "]";
                return false;
            }
        }

        public static int RunForgetMode(string arg, string resultFile)
        {
            try
            {
                Paths.Ensure();
                var svc = new FirewallService();
                bool ok = svc.ForgetLocal();
                WriteResult(resultFile, ok
                    ? (string.IsNullOrEmpty(LastWarning) ? "ok" : "ok\t" + LastWarning)
                    : (LastError.Length > 0 ? LastError : "Windows Firewall rechazó el borrado."));
                return ok ? 0 : 1;
            }
            catch (Exception ex)
            {
                WriteResult(resultFile, "La copia elevada falló: " + ex.Message);
                return 2;
            }
        }

        // Decisión del usuario sobre el acceso de una app, según el modo activo.
        // En estricto manda el firewall de verdad: si no hay regla PERMISO, esa app no sale.
        // En el modo normal, "sin marca" ya no se disfraza de permitido: es Decision.Pendiente.
        // La ENTRADA es distinta en los dos modos: Windows la corta por defecto, así que solo
        // está abierta si existe nuestra regla de permiso.
        public Decision GetDecision(string exePath, Direction dir)
            => GetDecision(exePath, dir, null);

        // Misma respuesta, pero con las reglas ya copiadas en un conjunto: la lista de
        // Protección pregunta por las cien y pico filas a la vez y así no se vuelve a
        // recorrer la lista en cada fila.
        public Decision GetDecision(string exePath, Direction dir, HashSet<string>? snapshot)
        {
            // Vale por nombre cuando el snapshot viene dado (es lo que se miraba antes) y por
            // ruta cuando hay que juzgar la regla a fondo: sin la ruta, un «Permitir» dado a otra
            // copia del mismo programa se enseñaba aquí como si fuera de este.
            bool Has(string name) => snapshot != null
                ? snapshot.Contains(name)
                : HasRealRule(name);
            bool Vale(string name) => Has(name) && ReglaApuntaA(name, exePath);

            if (dir == Direction.In)
                return Vale(AllowRuleName(exePath, Direction.In))
                    ? Decision.Permitido : Decision.Bloqueado;

            if (IsStrictOutbound(dir))
                return Vale(AllowRuleName(exePath, Direction.Out)) ? Decision.Permitido : Decision.Bloqueado;

            if (!_actions.TryGetValue(KeyOf(exePath, dir), out var v)) return Decision.Pendiente;
            return v switch
            {
                RuleInstalled => Decision.Bloqueado,
                RulePending => Decision.Pendiente,
                _ => Decision.Permitido
            };
        }

        private bool IsAllowRule(string exePath, Direction dir)
            => ReglaApuntaA(AllowRuleName(exePath, dir), exePath);

        // Da o quita el acceso a una app en la dirección pedida, respetando el modo activo.
        public bool SetDecision(string exePath, Direction dir, Decision decision)
        {
            if (dir == Direction.In)
                return EnsureAllowRule(exePath, dir, decision == Decision.Permitido);

            if (IsStrictOutbound(dir))
                return EnsureAllowRule(exePath, dir, decision == Decision.Permitido);

            var action = decision == Decision.Bloqueado ? RuleAction.Block : RuleAction.Allow;
            return SetRule(exePath, dir, action, enabled: true);
        }

        public bool EnsureAllowRule(string exePath, Direction dir, bool allow)
        {
            string name = AllowRuleName(exePath, dir);
            // Consultamos el firewall sin caché: si la regla ya está, no hace falta volver a pedir
            // permisos. «Está» quiere decir que además apunta a este ejecutable: si el nombre está
            // cogido por la regla de otra copia del programa, hay que reescribirla con esta ruta,
            // que es la que el usuario tiene delante.
            bool installed = RealRuleNames(force: true)
                .Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase))
                && ReglaApuntaA(name, exePath);
            string key = KeyOf(exePath, dir);

            if (allow == installed)
            {
                _actions[key] = allow ? RuleInstalled : RuleAllowed;
                _saved.Save(_actions);
                return true;
            }

            bool ok = IsAdmin
                ? AllowRuleLocal(exePath, dir, allow)
                : RunElevated($"--fw-allow {Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
                    JsonSerializer.Serialize(new { exe = exePath, dir = (int)dir, on = allow })))}");

            if (ok)
            {
                _actions[key] = allow ? RuleInstalled : RuleAllowed;
                _saved.Save(_actions);
                RealRuleNames(force: true);
            }
            return ok;
        }

        public bool AllowRuleLocal(string exePath, Direction dir, bool allow)
        {
            try
            {
                dynamic policy = NewPolicy();
                dynamic rules = policy.Rules;
                string name = AllowRuleName(exePath, dir);
                try { rules.Remove(name); } catch { }

                if (allow)
                {
                    dynamic rule = NewRule();
                    rule.Name = name;
                    rule.Description = "Permitido por el usuario en Monitor de Red PCJ";
                    rule.ApplicationName = exePath;
                    rule.Direction = dir == Direction.In ? 1 : 2;
                    rule.Action = ActionAllow;
                    rule.Enabled = true;
                    rule.Profiles = 0x7FFFFFFF;
                    rule.Grouping = "Monitor de Red PCJ";
                    AddRule(rules, rule, name);
                    return true;
                }
                return true;
            }
            catch (Exception ex)
            {
                LastError = "Windows Firewall no aceptó el permiso: " + ex.Message;
                return false;
            }
        }

        // El permiso de salida que el usuario dio a «MonitorRedPCJ» puede haber quedado apuntando
        // a otra copia del programa (la de pruebas de bin\Debug, la instaladora anterior…). Como
        // las reglas se llaman por nombre de fichero, la copia en marcha se queda sin permiso y
        // Windows Firewall le responde WSAEACCES — así se cortaba la descarga de la lista de
        // malware. Con el bloqueo total puesto, PCJ no puede ni actualizarse a sí mismo, así que
        // al arrancar se reescribe esa regla con la ruta de la copia que está corriendo. Devuelve
        // el camino viejo cuando hubo que corregirlo, y "" si no hacía falta o no se pudo tocar.
        public string ReparaReglaPropia()
        {
            string yo = Environment.ProcessPath ?? "";
            if (yo.Length == 0 || !IsAdmin || EscrituraProhibida) return "";

            // Una copia de pruebas (las que salen de bin\Debug y bin\Release) no reescribe reglas
            // de nadie: si no, cada compilación elevada se estaría robando el permiso a la copia
            // instalada, que es justo el problema que esta función viene a arreglar.
            string r = RutaReglaNormal(yo);
            if (r.Contains("\\bin\\debug\\") || r.Contains("\\bin\\release\\")) return "";

            RealRuleNames(force: true);
            string name = AllowRuleName(yo, Direction.Out);
            if (!HasRealRule(name)) return "";                 // no lo permitió nunca: no es nuestro
            string vieja = RutaRegla(name);
            if (vieja.Length == 0 || vieja == RutaReglaNormal(yo)) return "";
            if (!AllowRuleLocal(yo, Direction.Out, true)) return "";
            RealRuleNames(force: true);
            return vieja;
        }

        public static int RunAllowMode(string base64Payload, string resultFile)
        {
            try
            {
                Paths.Ensure();
                var doc = JsonDocument.Parse(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(base64Payload)));
                var svc = new FirewallService();
                int dirValue = doc.RootElement.TryGetProperty("dir", out var d) ? d.GetInt32() : (int)Direction.Out;
                bool ok = svc.AllowRuleLocal(doc.RootElement.GetProperty("exe").GetString()!,
                                             (Direction)dirValue,
                                             doc.RootElement.GetProperty("on").GetBoolean());
                WriteResult(resultFile, ok ? "ok"
                    : (LastError.Length > 0 ? LastError : "Windows Firewall rechazó el permiso."));
                return ok ? 0 : 1;
            }
            catch (Exception ex)
            {
                WriteResult(resultFile, "La copia elevada falló: " + ex.Message);
                return 2;
            }
        }

        // Limpia reglas de apps que ya no existen en disco
        public int CleanupStaleRules(int olderThanDays = 30)
        {
            if (!IsAdmin) return -1;
            int removed = 0;
            try
            {
                dynamic policy = NewPolicy();
                var toRemove = new List<string>();
                foreach (dynamic rule in policy.Rules)
                {
                    string name = (string)rule.Name;
                    if (!name.StartsWith(RulePrefix)) continue;
                    // Las excepciones del sistema se escriben por puerto y sin programa: no es
                    // que su programa se haya desinstalado, es que nunca tuvieron uno. Borrarlas
                    // aquí sería cargarse las actualizaciones de Windows en un limpiado.
                    if (EsExcepcion(name)) continue;
                    string app = (string)rule.ApplicationName;
                    if (string.IsNullOrEmpty(app) || !File.Exists(app))
                        toRemove.Add(name);
                }
                foreach (var n in toRemove) { try { policy.Rules.Remove(n); removed++; } catch { } }
                if (removed > 0) RealRuleNames(force: true);
            }
            catch (Exception ex)
            {
                LastError = "No se pudieron revisar las reglas: " + ex.Message;
            }
            return removed;
        }

        // Encendido/apagado general: activa o desactiva todas nuestras reglas
        public bool SetAllRulesEnabled(bool enabled)
        {
            // Si en el firewall no hay ninguna regla nuestra, el interruptor es solo una
            // marca local: no hay nada que cambiar y no se debe abrir ninguna ventana de UAC.
            int real = RealRuleCount();
            if (real == 0) return true;
            if (real < 0 && InstalledRuleCount == 0) return true; // sin reglas y sin lectura: nada que tocar
            return IsAdmin ? ToggleLocal(enabled) : ToggleElevated(enabled);
        }

        public bool ToggleLocal(bool enabled)
        {
            try
            {
                dynamic policy = NewPolicy();
                foreach (dynamic rule in policy.Rules)
                {
                    string name = (string)rule.Name;
                    // Las reglas PERMISO nunca se pausan: desactivarlas con el bloqueo total
                    // puesto dejaría al equipo sin red en lugar de "protección en pausa".
                    // Las EXC tampoco: son permisos del sistema que el usuario enciende y apaga
                    // desde Ajustes, y reactivar la protección los pondría todos encendidos.
                    if (EsExcepcion(name)) continue;
                    if (name.StartsWith(RulePrefix) && !name.EndsWith("PERMISO"))
                        rule.Enabled = enabled;
                }
                RealRuleNames(force: true);
                return true;
            }
            catch (Exception ex)
            {
                LastError = "No se pudieron actualizar las reglas: " + ex.Message;
                return false;
            }
        }

        private bool ToggleElevated(bool enabled)
            => RunElevated($"--fw-toggle {(enabled ? 1 : 0)}");

        public static int RunToggleMode(string arg, string resultFile)
        {
            try
            {
                Paths.Ensure();
                var svc = new FirewallService();
                bool ok = svc.ToggleLocal(arg == "1");
                WriteResult(resultFile, ok ? "ok" : (LastError.Length > 0 ? LastError : "Windows Firewall rechazó el cambio."));
                return ok ? 0 : 1;
            }
            catch (Exception ex)
            {
                WriteResult(resultFile, "La copia elevada falló: " + ex.Message);
                return 2;
            }
        }

        // ---------- Candado: cortar toda la salida de golpe y volverla igual de rápido ----------
        //
        // No es lo mismo que el bloqueo total. El bloqueo total es una política con lista
        // blanca que el usuario repasa; el candado es «me levanto de la silla» y no decide nada:
        // pone la salida de Windows en Block y deja TODAS nuestras reglas en pausa, apuntando
        // cuáles estaban activas. Al soltarlo se restaura exactamente esa foto, que es lo que
        // hace que nadie tenga miedo de usarlo.

        internal class CandadoEstado
        {
            public bool activo { get; set; }
            public bool salidaYaBloqueada { get; set; }
            public List<string> pausadas { get; set; } = new();
        }

        private static string CandadoFile => Path.Combine(Paths.Root, "candado.json");

        public static bool CandadoEnMarcador()
        {
            try
            {
                if (!File.Exists(CandadoFile)) return false;
                var e = JsonSerializer.Deserialize<CandadoEstado>(File.ReadAllText(CandadoFile));
                return e != null && e.activo;
            }
            catch { return false; }
        }

        private static CandadoEstado? LeerCandado()
        {
            try
            {
                if (!File.Exists(CandadoFile)) return null;
                return JsonSerializer.Deserialize<CandadoEstado>(File.ReadAllText(CandadoFile));
            }
            catch { return null; }
        }

        private static void EscribirCandado(CandadoEstado e)
        {
            try
            {
                Paths.Ensure();
                File.WriteAllText(CandadoFile, JsonSerializer.Serialize(e));
            }
            catch { }
        }

        public bool Candado(bool on) => IsAdmin ? CandadoLocal(on) : RunElevated($"--fw-lock {(on ? 1 : 0)}");

        public bool CandadoLocal(bool on)
        {
            try
            {
                dynamic policy = NewPolicy();

                if (on)
                {
                    if (IsOutboundDefaultBlocked())
                    {
                        // Ya está cortado de serie (bloqueo total en marcha): no hay nada que
                        // pausar, el candado no aporta nada encima.
                        LastError = "La salida ya está cortada por defecto: el candado no hace falta " +
                                    "con el bloqueo total activado.";
                        return false;
                    }
                    var estado = new CandadoEstado { activo = true, salidaYaBloqueada = false };
                    foreach (dynamic rule in policy.Rules)
                    {
                        string name = (string)rule.Name;
                        if (!name.StartsWith(RulePrefix)) continue;
                        if (!(bool)rule.Enabled) continue;
                        try { rule.Enabled = false; estado.pausadas.Add(name); } catch { }
                    }
                    // La foto se guarda ANTES de cortar: si el proceso se cae a mitad, al
                    // reiniciar PCJ hay forma de saber qué reglas estaban activas.
                    EscribirCandado(estado);
                    if (!ApplyOutboundDefaultLocal(true))
                    {
                        foreach (var n in estado.pausadas) { try { policy.Rules.Item(n).Enabled = true; } catch { } }
                        EscribirCandado(new CandadoEstado { activo = false });
                        return false;
                    }
                    RealRuleNames(force: true);
                    return true;
                }

                var previo = LeerCandado();
                bool volverBloqueado = previo != null && previo.salidaYaBloqueada;
                if (!ApplyOutboundDefaultLocal(volverBloqueado)) return false;

                var querian = previo != null && previo.pausadas.Count > 0
                    ? new HashSet<string>(previo.pausadas, StringComparer.OrdinalIgnoreCase)
                    : null;
                foreach (dynamic rule in policy.Rules)
                {
                    string name = (string)rule.Name;
                    if (!name.StartsWith(RulePrefix)) continue;
                    if (querian != null && !querian.Contains(name)) continue;
                    try { rule.Enabled = true; } catch { }
                }
                EscribirCandado(new CandadoEstado { activo = false });
                RealRuleNames(force: true);
                return true;
            }
            catch (Exception ex)
            {
                LastError = "Windows Firewall no aceptó el candado: " + ex.Message
                            + " [" + ex.GetType().Name + "]";
                return false;
            }
        }

        public static int RunLockMode(string arg, string resultFile)
        {
            try
            {
                Paths.Ensure();
                var svc = new FirewallService();
                bool ok = svc.CandadoLocal(arg == "1");
                WriteResult(resultFile, ok ? "ok" : (LastError.Length > 0 ? LastError : "Windows Firewall rechazó el cambio."));
                return ok ? 0 : 1;
            }
            catch (Exception ex)
            {
                WriteResult(resultFile, "La copia elevada falló: " + ex.Message);
                return 2;
            }
        }
    }
}
