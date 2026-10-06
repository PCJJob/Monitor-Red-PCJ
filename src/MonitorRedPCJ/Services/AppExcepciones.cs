using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MonitorRedPCJ.Models;

namespace MonitorRedPCJ.Services
{
    // ============================================================
    // Excepciones del sistema (1.6.0)
    // ============================================================
    //
    // Con el bloqueo total puesto, Windows deja de actualizar, de resolver nombres y de hablar
    // con el router: sus servicios son programas como cualquier otro y PCJ les cortó la salida
    // por no tener permiso. Estas son las salidas que el propio Windows necesita, dichas una a
    // una y con su descripción, para que el usuario encienda las que haga falta y apague las que
    // no, en vez de tener que abrirle la red a todo el equipo.
    //
    // Están copiadas en espíritu de lo que ofrece TinyWall. Cada una escribe reglas de PERMISO
    // por puerto, casi siempre colgadas de svchost.exe (que es donde corren los servicios de
    // Windows), y todas con el token " EXC " en el nombre para que los recuentos de la banda de
    // Protección, la pausa de la protección y el limpiado de reglas no las confundan con las
    // reglas de una aplicación. Ver FirewallService.ExcRuleName.
    // ============================================================
    public class ExcepcionInfo
    {
        public string Id = "";
        public string Nombre = "";

        // "recomendada" = sin ella el equipo se rompe de forma visible; "opcional" = solo hace
        // falta si usas esa función. La agrupación es lo que ordena la lista en Ajustes.
        public string Grupo = "opcional";

        // Para qué sirve y qué pasa si se queda apagada, en español, dicho al usuario.
        public string Detalle = "";

        // Qué se escribe en Windows Firewall al encenderla. Se calcula en el momento porque
        // depende de lo que haya instalado en ESTE equipo (la Tienda, WSL, Defender…).
        public Func<List<FirewallService.ReglaSpec>> Reglas = () => new();
    }

    public static class AppExcepciones
    {
        // Techo de reglas por excepción. El borrado parte de cero cada vez —se quitan las doce
        // posiciones posibles— para que cambiar una excepción de sitio no deje reglas viejas
        // sueltas. Si alguna vez una excepción necesitase más, se sube aquí el número.
        public const int MaxReglasPorExcepcion = 12;

        private const string Local = "LocalSubnet";

        private static string Windir =>
            Environment.GetFolderPath(Environment.SpecialFolder.Windows) ?? "";
        private static string ArchivosProgramas =>
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) ?? "";

        // Los servicios de Windows no son ejecutables propios: casi todos viven dentro de
        // svchost.exe, y por eso las reglas de las excepciones van contra ese archivo.
        private static string Svchost => Path.Combine(Windir, "System32", "svchost.exe");

        // HTTP.sys (las descargas que hace el propio sistema, no un servicio) se nombra "System"
        // en el firewall; es el único caso en que Windows acepta esa palabra como programa.
        private static readonly string Sistema = "System";

        // ---------- Catálogo ----------

        public static readonly ExcepcionInfo[] Lista =
        {
            new ExcepcionInfo
            {
                Id = "wu",
                Nombre = "Actualizaciones de Windows",
                Grupo = "recomendada",
                Detalle =
                    "Que el servicio de Windows Update pueda bajar parches por los puertos 80 y 443. " +
                    "Con el bloqueo total puesto y esta apagada, el equipo deja de actualizarse en " +
                    "silencio: verás el error 0x80072efe o «no podemos conectarte a Windows Update» " +
                    "en Configuración. Es la primera que conviene dejar encendida; apagarla solo " +
                    "tiene sentido si actualizas a mano un día concreto.",
                Reglas = Wu,
            },
            new ExcepcionInfo
            {
                Id = "wustore",
                Nombre = "Actualizaciones de la Microsoft Store",
                Grupo = "recomendada",
                Detalle =
                    "Lo mismo, pero para las aplicaciones que vienen de la Tienda (Fotos, Calculadora, " +
                    "la propia Tienda…). Su carpeta cambia de nombre con cada versión de Windows, así " +
                    "que PCJ la busca en este equipo y apunta la regla al ejecutable que hay. Si no la " +
                    "encuentra, la excepción no escribe nada: las descargas de fondo ya las cubre la " +
                    "excepción de arriba.",
                Reglas = Store,
            },
            new ExcepcionInfo
            {
                Id = "dhcp",
                Nombre = "Cliente DHCP",
                Grupo = "recomendada",
                Detalle =
                    "Cómo se pide la dirección IP al router al conectarse (UDP 67 y 68). Si la apagas " +
                    "con el bloqueo total puesto, al reiniciar puedes quedarte sin IP o con una " +
                    "169.254.x autoasignada, y entonces no hay red de ninguna clase hasta que la " +
                    "vuelvas a encender desde otro sitio. En un portátil que cambia de red, no la " +
                    "apagues.",
                Reglas = () =>
                {
                    var c = new Constructor("dhcp");
                    c.Permitir("el router reparta dirección", dir: 2, "17", "67,68", Svchost);
                    return c.Reglas;
                },
            },
            new ExcepcionInfo
            {
                Id = "dns",
                Nombre = "Cliente DNS",
                Grupo = "recomendada",
                Detalle =
                    "Traduce los nombres a direcciones (UDP 53, y TCP 53 para las respuestas grandes). " +
                    "Sin ella no se resuelve nada: el navegador dirá «no se encontró la página» aunque " +
                    "el equipo tenga red, y PCJ empezará a avisar de conexiones fallidas. Es la que " +
                    "antes se nota apagar.",
                Reglas = () =>
                {
                    var c = new Constructor("dns");
                    c.Permitir("resolver nombres por UDP", dir: 2, "17", "53", Svchost);
                    c.Permitir("resolver nombres por TCP", dir: 2, "6", "53", Svchost);
                    return c.Reglas;
                },
            },
            new ExcepcionInfo
            {
                Id = "netdescubrimiento",
                Nombre = "Descubrimiento de redes",
                Grupo = "recomendada",
                Detalle =
                    "Que el equipo vea —y se deje ver— en el apartado «Red» del Explorador: impresoras, " +
                    "NAS, Chromecast, mandos. Usa SSDP (UDP 1900), Web Services Discovery (UDP 3702 y " +
                    "TCP 5357) y el descubrimiento de funciones de red. Apagada, lo que no tenga " +
                    "dirección fija deja de aparecer en la lista; conectándote por IP seguirá " +
                    "funcionando igual.",
                Reglas = () =>
                {
                    var c = new Constructor("netdescubrimiento");
                    c.Permitir("anunciar y buscar aparatos", dir: 2, "17", "1900,3702,5353", Svchost);
                    c.Permitir("que lo encuentren en la red local", dir: 1, "17", "1900,3702,5353",
                               Svchost, Local);
                    c.Permitir("los eventos de descubrimiento", dir: 1, "6", "5357", Svchost, Local);
                    return c.Reglas;
                },
            },
            new ExcepcionInfo
            {
                Id = "icmp",
                Nombre = "Tráfico ICMP (ping)",
                Grupo = "recomendada",
                Detalle =
                    "El ICMP es el protocolo del ping y de los avisos de ruta («el destino no existe», " +
                    "«la conexión va cortada»). PCJ lo corta junto con todo lo demás, así que sin esto " +
                    "no puedes llamar a otro equipo ni el router te explica por qué no llegó. No mueve " +
                    "datos de aplicaciones: abrirlo no le da salida a ningún programa.",
                Reglas = () =>
                {
                    var c = new Constructor("icmp");
                    c.Permitir("salir a hacer ping", dir: 2, "1", "");
                    c.Permitir("que te contesten los avisos de ruta", dir: 1, "1", "");
                    return c.Reglas;
                },
            },
            new ExcepcionInfo
            {
                Id = "reloj",
                Nombre = "Sincronización del reloj",
                Grupo = "recomendada",
                Detalle =
                    "El servicio W32Time pregunta la hora exacta por UDP 123. Si el reloj se desmanda " +
                    "fallan los certificados TLS, y con ellos las actualizaciones, la Tienda y " +
                    "prácticamente cualquier web segura. Es barata de dejar encendida: un par de " +
                    "paquetes cada muchos días.",
                Reglas = () =>
                {
                    var c = new Constructor("reloj");
                    c.Permitir("poner el reloj en hora", dir: 2, "17", "123", Svchost);
                    return c.Reglas;
                },
            },
            new ExcepcionInfo
            {
                Id = "compartidas",
                Nombre = "Archivos e impresoras compartidas",
                Grupo = "opcional",
                Detalle =
                    "El servicio LanmanServer: que otros equipos de tu casa lean las carpetas que hayas " +
                    "compartido e impriman por las impresoras compartidas. Entra y sale por TCP 139 y " +
                    "445 y UDP 137 y 138, y PCJ lo deja limitado a la red local —desde internet no se " +
                    "alcanza. Ábrela en la red de casa, no en el wifi del aeropuerto.",
                Reglas = () =>
                {
                    var c = new Constructor("compartidas");
                    c.Permitir("que lean tus carpetas compartidas", dir: 1, "6", "139,445", Svchost, Local);
                    c.Permitir("el nombre de equipo en la red", dir: 1, "17", "137,138", Svchost, Local);
                    c.Permitir("que tú leas las de otros", dir: 2, "6", "139,445", Svchost, Local);
                    return c.Reglas;
                },
            },
            new ExcepcionInfo
            {
                Id = "asistencia",
                Nombre = "Asistencia remota",
                Grupo = "opcional",
                Detalle =
                    "La Asistencia remota clásica de Windows (msra), la de «invita a alguien de " +
                    "confianza a ver tu escritorio». Abre el TCP 5555 del servicio de asistencia " +
                    "mientras la sesión está en curso. No es el Asistente rápido de Windows 11, que " +
                    "sale por 443 y ya lo cubre la excepción de la Tienda.",
                Reglas = () =>
                {
                    var c = new Constructor("asistencia");
                    c.Permitir("que te asistan desde otro equipo", dir: 1, "6", "5555", Svchost);
                    c.Permitir("invitar a quien te asista", dir: 2, "6", "5555", Svchost);
                    return c.Reglas;
                },
            },
            new ExcepcionInfo
            {
                Id = "escritorio",
                Nombre = "Escritorio remoto",
                Grupo = "opcional",
                Detalle =
                    "Que otro equipo entre aquí por RDP (TCP y UDP 3389), solo desde tu red local. Es la " +
                    "puerta por la que más entra donde no debe: tenla apagada salvo que la uses de " +
                    "verdad, y si la enciendes, con contraseña buena y en tu casa. PCJ no la abre a " +
                    "internet.",
                Reglas = () =>
                {
                    var c = new Constructor("escritorio");
                    c.Permitir("que entren por escritorio remoto", dir: 1, "6", "3389", Svchost, Local);
                    c.Permitir("el escritorio remoto fluido", dir: 1, "17", "3389", Svchost, Local);
                    return c.Reglas;
                },
            },
            new ExcepcionInfo
            {
                Id = "pingmaquina",
                Nombre = "Ping a esta máquina",
                Grupo = "opcional",
                Detalle =
                    "Contesta al ping que hacen otros equipos de tu red, y nada más: no abre ningún " +
                    "puerto de ninguna aplicación. Sirve para comprobar desde el router o desde otro " +
                    "PC si este está vivo. Apagada, el equipo no contesta aunque esté encendido y con " +
                    "red.",
                Reglas = () =>
                {
                    var c = new Constructor("pingmaquina");
                    c.Permitir("que te hagan ping desde casa", dir: 1, "1", "", "", Local);
                    c.Permitir("devolver la contestación", dir: 2, "1", "", "", Local);
                    return c.Reglas;
                },
            },
            new ExcepcionInfo
            {
                Id = "vpnl2tp",
                Nombre = "VPN L2TP/IPSec",
                Grupo = "opcional",
                Detalle =
                    "Las VPN de empresa de toda la vida: ISAKMP por UDP 500, NAT-T por 4500 y el túnel " +
                    "L2TP por 1701, más el tráfico cifrado ESP (protocolo 50). Sin esto la conexión " +
                    "levanta y cae, o se queda en «negociando la seguridad». Si tu empresa usa su " +
                    "propio cliente (GlobalProtect, AnyConnect), ese cliente necesita su regla y esta " +
                    "no hace falta.",
                Reglas = () =>
                {
                    var c = new Constructor("vpnl2tp");
                    c.Permitir("negociar y montar el túnel", dir: 2, "17", "500,1701,4500", Svchost);
                    c.Permitir("que el otro lado responda", dir: 1, "17", "500,1701,4500", Svchost);
                    c.Permitir("el tráfico ESP cifrado", dir: 2, "50", "", Svchost);
                    c.Permitir("el tráfico ESP entrante", dir: 1, "50", "", Svchost);
                    return c.Reglas;
                },
            },
            new ExcepcionInfo
            {
                Id = "vpnpptp",
                Nombre = "VPN PPTP",
                Grupo = "opcional",
                Detalle =
                    "El protocolo de VPN antiguo: TCP 1723 más el canal GRE (protocolo 47). Está " +
                    "obsoleto y se le conocen fallos de cifrado; úsalo solo si tu empresa no ofrece " +
                    "otra cosa. Si no conectas por PPTP, deja esto apagada.",
                Reglas = () =>
                {
                    var c = new Constructor("vpnpptp");
                    c.Permitir("montar la conexión PPTP", dir: 2, "6", "1723", Svchost);
                    c.Permitir("el canal GRE de datos", dir: 2, "47", "", Svchost);
                    c.Permitir("el canal GRE entrante", dir: 1, "47", "", Svchost);
                    return c.Reglas;
                },
            },
            new ExcepcionInfo
            {
                Id = "defender",
                Nombre = "Windows Defender",
                Grupo = "opcional",
                Detalle =
                    "Que el antivirus pueda actualizar sus definiciones y avisar (baja por 80 y 443). " +
                    "Con el bloqueo total y esta apagada, Defender seguirá protegiendo con la última " +
                    "lista que tuvo, pero cada día está más ciego, y Windows marcará el equipo como " +
                    "«potencialmente no protegido».",
                Reglas = Defender,
            },
            new ExcepcionInfo
            {
                Id = "wsl2",
                Nombre = "WSL 2",
                Grupo = "opcional",
                Detalle =
                    "Las distribuciones de Linux que van por WSL 2 no salen por sí mismas: lo hacen a " +
                    "través de un proceso de este Windows (wslrelay, wslservice, vpnsock). Con el " +
                    "bloqueo total PCJ las cortaría, porque en la lista aparecen como un proceso raro " +
                    "sin permiso. Esta excepción deja salir a los que existan en este equipo. Si no " +
                    "usas WSL, no la actives: no escribe nada si no están instalados.",
                Reglas = Wsl,
            },
        };

        // Windows Update baja por svchost (el servicio y la Entrega Optimizada) y por HTTP.sys,
        // que en el firewall se nombra "System". Las dos cosas, o no baja nada.
        private static List<FirewallService.ReglaSpec> Wu()
        {
            var c = new Constructor("wu");
            c.Permitir("bajar actualizaciones", dir: 2, "6", "80,443", Svchost);
            c.Permitir("las descargas que hace el propio sistema", dir: 2, "6", "80,443", Sistema);
            return c.Reglas;
        }

        // La Tienda vive en %ProgramFiles%\WindowsApps\Microsoft.WindowsStore_<versión>\, y la
        // versión cambia con cada actualización. Se busca aquí, en el momento de escribir la
        // regla, porque un nombre fijo apuntaría a una carpeta que ya no existe.
        private static List<FirewallService.ReglaSpec> Store()
        {
            var c = new Constructor("wustore");
            foreach (string exe in RutasDe("WindowsApps", "Microsoft.WindowsStore_", "WinStore.App.exe", 2))
                c.Permitir("actualizar las apps de la Tienda", dir: 2, "6", "443", exe);
            return c.Reglas;
        }

        private static List<FirewallService.ReglaSpec> Defender()
        {
            var c = new Constructor("defender");
            var posibles = new List<string>
            {
                Path.Combine(ArchivosProgramas, "Windows Defender", "MpCmdRun.exe"),
                Path.Combine(ArchivosProgramas, "Windows Defender", "MsMpEng.exe"),
                Path.Combine(ArchivosProgramas, "Windows Defender", "NisSrv.exe"),
                Path.Combine(Windir, "System32", "SecurityHealthService.exe"),
            };
            foreach (string exe in posibles.Where(File.Exists))
                c.Permitir("actualizar el antivirus", dir: 2, "6", "80,443", exe);
            return c.Reglas;
        }

        private static List<FirewallService.ReglaSpec> Wsl()
        {
            var c = new Constructor("wsl2");
            var posibles = new List<string>
            {
                Path.Combine(ArchivosProgramas, "WSL", "wslrelay.exe"),
                Path.Combine(ArchivosProgramas, "WSL", "wslservice.exe"),
                Path.Combine(ArchivosProgramas, "WSL", "vpnsock.exe"),
                Path.Combine(Windir, "System32", "lxss", "tools", "wslrelay.exe"),
            };
            foreach (string exe in posibles.Where(File.Exists))
            {
                c.Permitir("que la distro salga por TCP", dir: 2, "6", "", exe);
                c.Permitir("que la distro salga por UDP", dir: 2, "17", "", exe);
            }
            return c.Reglas;
        }

        // Rutas de paquetes cuya carpeta cambia con la versión: se busca el prefijo dentro de
        // subdirectorio y se devuelven los ejecutables que hay ahora mismo (como mucho `max`).
        private static IEnumerable<string> RutasDe(string subdirectorio, string prefijo,
                                                  string archivo, int max)
        {
            var raiz = Path.Combine(ArchivosProgramas, subdirectorio);
            if (!Directory.Exists(raiz)) yield break;
            string[] carpetas;
            try { carpetas = Directory.GetDirectories(raiz, prefijo + "*"); }
            catch { yield break; }   // WindowsApps está reservado: sin permiso no se lee.

            foreach (string carpeta in carpetas.OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                string exe = Path.Combine(carpeta, archivo);
                if (!File.Exists(exe)) continue;
                yield return exe;
                if (--max <= 0) yield break;
            }
        }

        // ---------- Escribir el estado en Windows ----------

        // El constructor de reglas de una excepción. Se ocupa del número de regla, de que todas
        // salgan como permiso (una excepción nunca corta nada: para cortar están las apps) y de
        // poner los puertos en el lado que toca: en la salida son remotos, en la entrada son
        // locales, y al revés de lo que uno escribiría a mano.
        private sealed class Constructor
        {
            private readonly string id;
            private int n;
            public readonly List<FirewallService.ReglaSpec> Reglas = new();

            public Constructor(string id) { this.id = id; }

            public void Permitir(string que, int dir, string proto, string puertos,
                                 string exe = "", string direcciones = "")
            {
                if (n >= MaxReglasPorExcepcion) return;
                Reglas.Add(new FirewallService.ReglaSpec
                {
                    n = FirewallService.ExcRuleName(id, n++),
                    d = "Monitor de Red PCJ · excepción del sistema · " + que,
                    dir = dir,
                    act = 1,
                    proto = proto,
                    rp = dir == 2 ? puertos : "",
                    lp = dir == 1 ? puertos : "",
                    ra = direcciones,
                    exe = exe,
                    orden = FirewallService.OrdenPermiso,
                });
            }
        }

        /// <summary>
        /// El plan completo: qué borrar y qué crear para que el firewall quede como dice `estado`.
        /// Se borran siempre las doce posiciones de todas las excepciones, no solo las apagadas,
        /// para que una excepción que cambia de reglas no deje las viejas detrás.
        /// </summary>
        public static void Construir(IDictionary<string, bool> estado,
                                     List<string> borrar, List<FirewallService.ReglaSpec> crear)
        {
            foreach (var e in Lista)
                for (int n = 0; n < MaxReglasPorExcepcion; n++)
                    borrar.Add(FirewallService.ExcRuleName(e.Id, n));

            foreach (var e in Lista)
            {
                if (estado is null || !estado.TryGetValue(e.Id, out bool puesta) || !puesta) continue;
                List<FirewallService.ReglaSpec> suyas;
                try { suyas = e.Reglas() ?? new(); } catch { continue; }
                foreach (var s in suyas.Take(MaxReglasPorExcepcion)) crear.Add(s);
            }
        }

        /// <summary>Escribe las excepciones en Windows Firewall. Un solo viaje elevado.</summary>
        public static bool Aplicar(IDictionary<string, bool> estado, out string error)
        {
            error = "";
            var borrar = new List<string>();
            var crear = new List<FirewallService.ReglaSpec>();
            Construir(estado, borrar, crear);

            if (!App.Firewall.ApplySpecs(borrar, crear))
            {
                error = FirewallService.LastError.Length > 0
                    ? FirewallService.LastError
                    : "Windows Firewall no aceptó las excepciones del sistema.";
                return false;
            }
            error = FirewallService.LastWarning;
            return true;
        }

        // ---------- El tráfico con los aparatos de casa ----------

        // No está en el catálogo de arriba porque no es una excepción del sistema: es una
        // excepción del bloqueo. Con el corte total puesto, PCJ no deja salir a ninguna parte, y
        // eso incluye a la impresora, al NAS y al móvil que están en el pasillo. Estas cuatro
        // reglas vuelven a abrir lo local sin abrir internet.
        //
        // Son cuatro porque Windows no tiene un "y también" para las direcciones: la subred local
        // (LocalSubnet) no cubre el multicast ni el broadcast, y el descubrimiento de aparatos
        // va por ahí. Y cada una va en los dos sentidos: de salida para que el programa alcance
        // el aparato, de entrada para que el aparato alcance el programa (compartir archivos,
        // imprimir, que te vea el Chromecast).

        /// <summary>El plan de las reglas de red local, sin escribir nada.</summary>
        public static void ConstruirRedLocal(bool activa, List<string> borrar,
                                             List<FirewallService.ReglaSpec> crear)
        {
            // Se borran las doce posiciones, como con las excepciones: si mañana son seis, las
            // viejas no pueden quedarse abiertas de rondón.
            for (int n = 0; n < MaxReglasPorExcepcion; n++)
                borrar.Add(FirewallService.ExcRuleName("redlocal", n));
            if (!activa) return;

            var c = new Constructor("redlocal");
            c.Permitir("hablar con los aparatos de tu red", dir: 2, "", "", "", "LocalSubnet");
            c.Permitir("hablar con multicast y broadcast de tu red", dir: 2, "", "", "",
                       "224.0.0.0/4,255.255.255.255");
            c.Permitir("que los aparatos de tu red te alcancen", dir: 1, "", "", "", "LocalSubnet");
            c.Permitir("que te llegue el multicast de tu red", dir: 1, "", "", "", "224.0.0.0/4");
            crear.AddRange(c.Reglas);
        }

        /// <summary>Escribe o quita las reglas de red local. Un solo viaje al firewall.</summary>
        public static bool AplicarRedLocal(bool activa, out string error)
        {
            error = "";
            var borrar = new List<string>();
            var crear = new List<FirewallService.ReglaSpec>();
            ConstruirRedLocal(activa, borrar, crear);

            if (!App.Firewall.ApplySpecs(borrar, crear))
            {
                error = FirewallService.LastError.Length > 0
                    ? FirewallService.LastError
                    : "Windows Firewall no aceptó el cambio de la red local.";
                return false;
            }
            error = FirewallService.LastWarning;
            return true;
        }

        public static int Totales => Lista.Length;

        public static int Encendidas(IDictionary<string, bool>? estado)
            => estado is null ? 0 : Lista.Count(e => estado.TryGetValue(e.Id, out bool p) && p);

        public static IEnumerable<ExcepcionInfo> PorGrupo(string grupo)
            => Lista.Where(e => e.Grupo == grupo);

        // El interruptor de Ajustes solo lleva el id en su etiqueta; para escribir el
        // registro hace falta volver a la ficha y leer su nombre largo.
        public static ExcepcionInfo? PorId(string id)
            => Lista.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));

        // Dicho en una línea, para la tarjeta de Ajustes y para el registro.
        public static string Resumen(IDictionary<string, bool>? estado)
        {
            int n = Encendidas(estado);
            return n == 0
                ? "ninguna activada"
                : n + " de " + Totales + " activas";
        }

        // ============================================================
        // Autocomprobación (--excepciones-check)
        // ============================================================
        //
        // Lógica pura sobre el catálogo: que cada excepción escriba permisos y no cortes, que
        // ningún nombre de regla se parezca al de una aplicación, que los puertos vayan sin
        // espacios, que el protocolo nunca sea 255 y que apagar una excepción la borre entera.
        // Lo que NO comprueba es si las reglas funcionan de verdad en Windows: eso solo se ve
        // activando el bloqueo total a mano, que es lo que hace el autor en sus pruebas.
        // ============================================================
        public static class Check
        {
            private static readonly List<string> fallos = new();
            private static int aciertos;

            public static void Ejecutar(string archivo)
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("Comprobación de las excepciones del sistema — " +
                              DateTime.Now.ToString("dd/MM/yyyy HH:mm"));
                sb.AppendLine();

                Catalogo();
                ReglasDeCadaUna();
                EncendidoYApagado();
                RedLocal();

                sb.AppendLine();
                sb.AppendLine(fallos.Count == 0
                    ? "TODO CORRECTO · " + aciertos + " comprobaciones"
                    : "FALLOS: " + fallos.Count + " de " + (aciertos + fallos.Count));
                foreach (string f in fallos) sb.AppendLine("  - " + f);
                sb.AppendLine();
                sb.AppendLine("LO QUE ESTA COMPROBACION NO TOCA");
                sb.AppendLine("  - Escribir las reglas en Windows Firewall: hace falta permisos y, con");
                sb.AppendLine("    el bloqueo total puesto, se prueba a mano aflojando una excepción.");
                sb.AppendLine("  - Que el servicio de Windows al que abre cada excepción responda de");
                sb.AppendLine("    verdad: eso solo se ve con el equipo bloqueado y usando la función.");
                try { File.WriteAllText(archivo, sb.ToString()); } catch { }
            }

            private static void Mira(bool ok, string que, string detalle = "")
            {
                if (ok) { aciertos++; return; }
                fallos.Add(que + (detalle.Length > 0 ? " [" + detalle + "]" : ""));
            }

            private static void Catalogo()
            {
                Mira(Lista.Length == 15, "quince excepciones en el catálogo",
                     "hay " + Lista.Length);
                Mira(Lista.Select(e => e.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() ==
                     Lista.Length, "los identificadores no se repiten");
                Mira(PorGrupo("recomendada").Count() == 7, "siete recomendadas",
                     "hay " + PorGrupo("recomendada").Count());
                Mira(PorGrupo("opcional").Count() == 8, "ocho opcionales",
                     "hay " + PorGrupo("opcional").Count());

                foreach (var e in Lista)
                {
                    Mira(e.Id.Length > 0 && e.Id.All(c => char.IsLower(c) || char.IsDigit(c)),
                         e.Id + ": identificador en minúsculas, sin espacios");
                    Mira(e.Nombre.Length > 3, e.Id + ": tiene nombre");
                    Mira(e.Detalle.Length > 120, e.Id + ": su descripción explica para qué sirve",
                         e.Detalle.Length + " caracteres");
                    Mira(e.Grupo is "recomendada" or "opcional", e.Id + ": grupo conocido", e.Grupo);
                }
            }

            private static void ReglasDeCadaUna()
            {
                foreach (var e in Lista)
                {
                    List<FirewallService.ReglaSpec> r;
                    try { r = e.Reglas(); }
                    catch (Exception ex) { Mira(false, e.Id + ": sus reglas se construyen sin excepción", ex.Message); continue; }

                    Mira(r.Count <= MaxReglasPorExcepcion, e.Id + ": no pasa del techo de reglas",
                         "son " + r.Count);
                    foreach (var s in r)
                    {
                        Mira(s.n.StartsWith(FirewallService.RulePrefix), e.Id + ": regla nuestra", s.n);
                        Mira(FirewallService.EsExcepcion(s.n), e.Id + ": lleva el token de excepción", s.n);
                        // El detalle que sostiene todo: si una de estas terminara como las reglas
                        // de una aplicación, el asistente de salida la contaría como un permiso
                        // normal y el bloqueo total se la llevaría puesta.
                        Mira(!s.n.EndsWith("SALIDA PERMISO") && !s.n.EndsWith("ENTRADA PERMISO"),
                             e.Id + ": no se disfraza de permiso de aplicación", s.n);
                        Mira(s.act == 1, e.Id + ": una excepción siempre es un permiso", "act=" + s.act);
                        Mira(s.d.Length > 10 && s.d.StartsWith("Monitor de Red PCJ"),
                             e.Id + ": describe qué es en Windows", s.d);
                        Mira(s.proto != "255" && (s.proto == "" || int.TryParse(s.proto, out _)),
                             e.Id + ": protocolo escribible", s.proto);
                        Mira(s.proto.Length == 0 || s.rp.IndexOf(' ') < 0,
                             e.Id + ": puertos remotos sin espacios", s.rp);
                        Mira(s.lp.IndexOf(' ') < 0, e.Id + ": puertos locales sin espacios", s.lp);
                        Mira(s.dir is 1 or 2, e.Id + ": dirección válida", "dir=" + s.dir);
                        // Un permiso sin puertos y sin protocolo sería «dejar salir de todo», y
                        // eso no lo decide una excepción del sistema.
                        Mira(s.proto.Length > 0 || (s.rp.Length == 0 && s.lp.Length == 0),
                             e.Id + ": no abre todo sin decir protocolo", s.proto + "/" + s.rp);
                        if (s.rp.Length > 0)
                            Mira(s.rp.Split(',').All(p => int.TryParse(p.Trim(), out int v) && v is > 0 and <= 65535),
                                 e.Id + ": puertos remotos en rango", s.rp);
                        if (s.lp.Length > 0)
                            Mira(s.lp.Split(',').All(p => int.TryParse(p.Trim(), out int v) && v is > 0 and <= 65535),
                                 e.Id + ": puertos locales en rango", s.lp);
                    }
                }

                // Las que sí o sí tienen que escribir algo en cualquier Windows.
                foreach (string id in new[] { "dhcp", "dns", "reloj", "icmp", "wu" })
                    Mira(Lista.First(e => e.Id == id).Reglas().Count > 0,
                         id + ": escribe reglas aunque el equipo esté limpio");
            }

            private static void EncendidoYApagado()
            {
                var todas = Lista.ToDictionary(e => e.Id, _ => true);
                var borrar = new List<string>();
                var crear = new List<FirewallService.ReglaSpec>();
                Construir(todas, borrar, crear);
                Mira(borrar.Count == Lista.Length * MaxReglasPorExcepcion,
                     "el borrado cubre todas las posiciones", borrar.Count + " nombres");
                Mira(crear.Count > 0, "con todo encendido hay reglas que crear", crear.Count + "");
                Mira(crear.All(s => s.act == 1), "y ninguna de ellas corta nada");

                // Apagadas todas: no se crea nada, pero el borrado sigue siendo completo, que es
                // lo que quita las que estuvieran puestas antes.
                var borrar2 = new List<string>();
                var crear2 = new List<FirewallService.ReglaSpec>();
                Construir(new Dictionary<string, bool>(), borrar2, crear2);
                Mira(crear2.Count == 0, "con todo apagado no se crea ninguna regla",
                     crear2.Count + " creadas");
                Mira(borrar2.Count == borrar.Count, "y el borrado es igual de completo apagado");

                // Una sola encendida: solo sus posiciones.
                var borrar3 = new List<string>();
                var crear3 = new List<FirewallService.ReglaSpec>();
                Construir(new Dictionary<string, bool> { ["dns"] = true }, borrar3, crear3);
                Mira(crear3.Count == 2 && crear3.All(s => s.n.Contains(" EXC dns ")),
                     "encender DNS escribe solo sus dos reglas", crear3.Count + "");

                // El identificador de la red local no puede repetirse con los del catálogo.
                Mira(!Lista.Any(e => FirewallService.ExcRuleName(e.Id, 0) ==
                                     FirewallService.LocalTrafficRuleName()),
                     "la regla de red local no choca con ninguna excepción");

                Mira(Resumen(todas) == Lista.Length + " de " + Lista.Length + " activas",
                     "el resumen dice cuántas están puestas", Resumen(todas));
                Mira(Resumen(new Dictionary<string, bool>()) == "ninguna activada",
                     "y dice «ninguna» con el catálogo intacto");
            }

            // Las cuatro reglas de la red local: que sean permiso, que vayan en los dos sentidos,
            // que hablen solo de direcciones locales y que apagadas no dejen ninguna.
            private static void RedLocal()
            {
                var borrar = new List<string>();
                var crear = new List<FirewallService.ReglaSpec>();
                ConstruirRedLocal(true, borrar, crear);

                Mira(crear.Count == 4, "la red local escribe cuatro reglas", crear.Count + "");
                Mira(borrar.Contains(FirewallService.LocalTrafficRuleName()),
                     "el borrado incluye la regla de siempre");
                Mira(borrar.Count == MaxReglasPorExcepcion,
                     "y repasa sus doce posiciones por si antes había más", borrar.Count + "");
                Mira(crear.All(s => s.act == 1), "todas son permiso");
                Mira(crear.All(s => FirewallService.EsExcepcion(s.n)),
                     "todas llevan el token de excepción");
                Mira(crear.Count(s => s.dir == 1) == 2 && crear.Count(s => s.dir == 2) == 2,
                     "dos de salida y dos de entrada");
                Mira(crear.All(s => s.ra.Length > 0 && s.ra.IndexOf(' ') < 0),
                     "sus direcciones van sin espacios");
                Mira(crear.All(s => s.ra.Contains("LocalSubnet") || s.ra.Contains("224.0.0.0/4") ||
                                    s.ra.Contains("255.255.255.255")),
                     "y ninguna abre algo que no sea la red de casa");
                Mira(crear.All(s => s.rp.Length == 0 && s.lp.Length == 0),
                     "no abren puerto suelto: abren la red local entera");
                Mira(crear.Select(s => s.n).Distinct().Count() == crear.Count,
                     "sin nombres repetidos");

                var borrar2 = new List<string>();
                var crear2 = new List<FirewallService.ReglaSpec>();
                ConstruirRedLocal(false, borrar2, crear2);
                Mira(crear2.Count == 0, "apagada no escribe ninguna", crear2.Count + "");
                Mira(borrar2.Count == borrar.Count, "apagada borra las mismas posiciones");
            }
        }
    }
}
