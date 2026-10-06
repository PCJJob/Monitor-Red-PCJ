using System;
using System.Collections.Generic;

namespace MonitorRedPCJ.Models
{
    public enum RuleAction { Allow, Block }
    public enum Direction { In, Out }

    // Decisión del usuario sobre la salida de una app. "Pendiente" = todavía no tocó
    // ninguna tecla (en modo estricto eso significa sin salida).
    public enum Decision { Permitido = 0, Bloqueado = 1, Pendiente = 2 }

    public class TrackedApp
    {
        public string ExePath { get; set; } = "";
        public string Name { get; set; } = "";
        public DateTime FirstSeenUtc { get; set; }
        public RuleAction OutAction { get; set; } = RuleAction.Allow;
        public RuleAction InAction { get; set; } = RuleAction.Allow;
        public bool IsKnown { get; set; }          // el usuario ya decidió
        // El aviso se cerró sin decidir nada. Sirve para no repetir la misma pregunta por
        // cada arranque en el modo normal (donde la app ya sale); con el bloqueo total sí
        // se vuelve a preguntar, porque ahí la ausencia de decisión significa sin salida.
        public bool AskedWithoutDeciding { get; set; }
        // El usuario pulsó "Que vuelva a preguntar": el siguiente arranque de este programa
        // salta el aviso aunque en el modo normal ya estuviera callado. Dura un solo aviso.
        public bool AskOnNextLaunch { get; set; }
        public bool IsRunning { get; set; }        // recalculado en vivo
        public long TotalSent { get; set; }
        public long TotalReceived { get; set; }
        public double SentBps { get; set; }
        public double ReceivedBps { get; set; }
        // Seteable a propósito: con solo getter, System.Text.Json no rellena la lista al leer
        // apps.json y los destinos se perdían cada vez que se recargaba el histórico (o al
        // recuperar la foto del modo incógnito).
        public HashSet<string> Hosts { get; set; } = new HashSet<string>();

        // ---------- Configuración adicional (1.6.0) ----------
        //
        // El modo fino de salida de esta aplicación, copiado de lo que ofrece TinyWall. Es una
        // cadena y no un enum para que un valor de una versión futura no rompa al leer un
        // apps.json viejo; los valores posibles están en AppModoAdicional.
        // Vacío = sin configurar: manda la pastilla de Salida de siempre.
        public string ModoAdicional { get; set; } = "";

        // Los puertos de destino que se permiten cuando el modo es "soloPuertos", escritos como
        // "443, 80, 5353:5360" y con sufijo /udp cuando hace falta ("53/udp").
        public string PuertosAdicionales { get; set; } = "";

        // Solo deja que esta aplicación hable con aparatos de la red local; el resto se corta.
        public bool SoloRedLocal { get; set; }

        // Al abrirse, los procesos que esta aplicación engendra heredan sus reglas.
        public bool HeredarAHijos { get; set; }

        // Ruta del programa del que este proceso heredó su configuración, si es que heredó. Se
        // guarda para poder decirlo en la fila («heredada de Chrome»): sin esto, un proceso hijo
        // aparecería con un corte que el usuario no le puso a él, y parecería un fallo.
        public string HeredadaDe { get; set; } = "";

        // ---------- Ventana de actividad (1.6.2) ----------
        //
        // Las dos marcas que faltaban para poder enseñar solo lo de ahora. El histórico de
        // apps.json iba creciendo para siempre, y a las dos semanas la lista de Protección era
        // un cementerio de programas que ya ni existían. Con estas dos fechas la lista se puede
        // recortar a «abiertas ahora» o «pidieron salida en los últimos N minutos» sin borrar
        // nada: la decisión que el usuario tomó sigue guardada en su sitio.
        public DateTime LastAliveUtc { get; set; }
        public DateTime LastEgressUtc { get; set; }
    }

    public class ConnectionInfo
    {
        public bool IsTcp;
        public string LocalAddress = "";
        public int LocalPort;
        public string RemoteAddress = "";
        public int RemotePort;
        public int Pid;
        // Estado de la fila TCP, segun el enum MIB_TCP_STATE de iprtrmib.h:
        //   1 CERRADA · 2 ESCUCHA · 3 SYN-ENVIADO · 4 SYN-RECIBIDO · 5 ESTABLECIDA
        //   6/7 FIN-WAIT · 8 CLOSING · 9 TIME-WAIT · 10 LAST-ACK
        // Para UDP vale 0 (las filas UDP no traen estado).
        // Solo las TCP ESTABLECIDAS transportan datos de verdad; el resto (TIME_WAIT,
        // CLOSE_WAIT, ESCUCHA, sockets UDP abiertos) no genera trafico, y contarlas
        // repartia trafico fantasma entre apps cerradas o bloqueadas.
        // OJO: ESTABLECIDA es 5, no 1. Comparar con 1 coincidia con CERRADA, que nunca
        // aparece en la tabla: el peso salia siempre 0 y toda la lista marcaba 0 B/s.
        public int State;
        public const int EstabState = 5;
        public bool Established => IsTcp && State == EstabState;
    }

    public enum EventKind { FirstConnection, RuleChanged, NewDevice, DeviceGone, ProtectionToggled, Info }

    public class NetEvent
    {
        public DateTime TimeUtc { get; set; }
        public EventKind Kind { get; set; }
        public string Message { get; set; } = "";
        public string Detail { get; set; } = "";
        public bool Read { get; set; }
        public bool Important { get; set; }
    }

    public class DeviceInfo
    {
        public string Ip { get; set; } = "";
        public string Mac { get; set; } = "";
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public DateTime LastSeenUtc { get; set; }
    }

    // Un punto del histórico: segundos desde el inicio de la sesión + bytes acumulados por app
    public class TrafficSample
    {
        public DateTime TimeUtc { get; set; }
        public long TotalSent { get; set; }
        public long TotalReceived { get; set; }
        public Dictionary<string, long[]> PerApp { get; set; } = new Dictionary<string, long[]>();
    }

    public class AppSettings
    {
        public bool RunOnStartup { get; set; } = true;
        public bool ProtectionEnabled { get; set; } = false;  // aplicación de reglas de bloqueo
        public bool StrictMode { get; set; } = false;         // sin salida por defecto (lista blanca)
        public bool AlertsEnabled { get; set; } = true;
        public bool IncognitoMode { get; set; } = false;
        public bool AutoScanNetwork { get; set; } = true;
        // La explicación va cerrada por defecto: su resumen ya se lee en la propia cabecera,
        // y abierta le quita casi 150 px de alto a la lista de aplicaciones.
        public bool ShowAccessLegend { get; set; } = false;

        // Ventana de la lista de Protección, en minutos: 0 = enseñar el histórico entero. Con el
        // valor que sea, la decisión (permitir o cortar) de las apps que quedan fuera se sigue
        // guardando igual; solo dejan de aparecer en la lista.
        public int ProtectWindowMinutes { get; set; } = 15;
        // Filtro de estado elegido en Protección: 0 = todos · 1 en ejecución · 2 permitidas ·
        // 3 bloqueadas · 4 sin decidir. Se recuerda igual que la ventana de minutos.
        public int ProtectFiltroEstado { get; set; } = 0;
        public int ScanIntervalMinutes { get; set; } = 30;
        public int HistoryRetentionDays { get; set; } = 30;
        public bool AutoUpdates { get; set; } = false;
        public string BandwidthUnits { get; set; } = "B/s";

        // ---------- Aspecto (1.5.0) ----------
        //
        // Id de la paleta de colores en uso. Sus valores posibles son los del catálogo de
        // ThemeService: "aurora" es el aspecto con el que el programa siempre se vió, y es el
        // valor por defecto — si el fichero de ajustes no trae esta línea (porque viene de una
        // versión anterior), se aplica aurora y nadie pierde nada.
        // Se cambia sin reiniciar: ThemeService sustituye el diccionario de la paleta y todos
        // los estilos, que piden su color por DynamicResource, se repintan solos.
        public string Tema { get; set; } = "aurora";
        public string LastWifiSsid { get; set; } = "";
        public double? AlertLeft { get; set; }
        public double? AlertTop { get; set; }

        // ---------- Caja de herramientas (1.4.0) ----------
        //
        // Cada herramienta nace apagada: mientras no este en el diccionario (o este a false)
        // su servicio no se arranca, no mira la red y no gasta ni un hilo.
        public Dictionary<string, bool> Herramientas { get; set; } = new Dictionary<string, bool>();

        // Id de la herramienta que ocupa un hueco en la barra de pestanas de la ventana
        // principal. Vacio = no se pone ninguna.
        public string HerramientaEnPrincipal { get; set; } = "";

        // Cuota mensual en gigas (0 = sin limite) y el porcentaje a partir del cual avisa.
        public double CuotaMensualGB { get; set; } = 0;
        public int AvisoCuotaPorCiento { get; set; } = 80;

        // Que los dominios de rastreo marcados esten escritos en el fichero hosts.
        public bool BloqueoHostsActivo { get; set; } = false;

        // Preferencias sueltas de las herramientas que tienen repaso propio o aviso propio.
        public bool AuditorCadaHora { get; set; } = false;      // fugas
        public bool PuertosAvisarNuevos { get; set; } = false;   // puertos
        public bool PaisesAvisarNuevos { get; set; } = false;    // paises

        // Consultar el pais sale del equipo: manda la IP destino a ip-api.com sin cifrar.
        // Por eso viene apagado y cada cual lo enciende; con esto apagado PCJ no pregunta
        // a nadie y solo usa la base local geoipv4.csv si la pones en la carpeta de datos.
        public bool PaisesConsultarOnline { get; set; } = false;  // paises

        // ---------- Excepciones y refuerzos del sistema (1.6.0) ----------
        //
        // Las excepciones vienen del catálogo de ExcepcionesService: cada id son un puñado de
        // reglas propias de PCJ (puertos y programas concretos), no los grupos de reglas que
        // trae Windows, porque ésos cambian de nombre con el idioma del sistema.
        // Todo apagado por defecto: nadie abre un puerto sin que se lo pidan.
        public Dictionary<string, bool> ExcepcionesSistema { get; set; } = new Dictionary<string, bool>();

        // Con el bloqueo total puesto, el tráfico con otros aparatos de casa (impresora, NAS,
        // el móvil) también se corta. Este interruptor lo vuelve a permitir: añade una regla
        // que deja pasar lo que va a las redes locales y multicast.
        public bool TraficoLocalLibre { get; set; } = false;

        // Lista de dominios maliciosos descargada de internet y escrita en el fichero hosts.
        // Apagada por defecto: toca un fichero del sistema y necesita administrador.
        public bool BloqueoMalwareActivo { get; set; } = false;
        // La fuente de abajo viene en formato hosts (una línea por dominio con su 127.0.0.1) y se
        // renueva cada pocos minutos. MalwareDomainList, StevenBlack y compañía también valen: el
        // lector acepta formato hosts y lista pelada de dominios.
        public string MalwareListaUrl { get; set; } =
            "https://urlhaus.abuse.ch/downloads/hostfile/";
        public DateTime MalwareActualizadoUtc { get; set; }
        public int MalwareEntradas { get; set; }
    }
}
