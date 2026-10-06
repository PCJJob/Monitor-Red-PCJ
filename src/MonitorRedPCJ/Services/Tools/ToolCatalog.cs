using System;
using System.Collections.Generic;
using System.Linq;

namespace MonitorRedPCJ.Services.Tools
{
    /// Definición de una herramienta de la caja: cómo se llama, qué hace, cómo se usa y
    /// cuánto cuesta tenerla encendida. La interfaz de «Más herramientas» se pinta a partir
    /// de estos datos, así que el texto vive aquí y no repartido por diez ventanas.
    public class ToolDef
    {
        public string Id { get; set; } = "";
        public string Nombre { get; set; } = "";
        public string Lema { get; set; } = "";            // una línea bajo el nombre
        public int Glifo { get; set; }                    // código de Segoe Fluent Icons
        public string Color { get; set; } = "#0FA3A3";    // color del mosaico del icono
        public string QueHace { get; set; } = "";         // descripción larga
        public string[] Pasos = Array.Empty<string>();
        public string Consumo { get; set; } = "";         // qué gasta cuando está encendida
        public bool EsExistente { get; set; }             // escáner y registros: no se apagan, solo se mudaron
    }

    public static class ToolCatalog
    {
        public static readonly List<ToolDef> Todas = new List<ToolDef>
        {
            new ToolDef
            {
                Id = "radar",
                Nombre = "Radar de destinos",
                Lema = "A quién llama cada programa",
                Glifo = 0xE774,
                Color = "#0FA3A3",
                QueHace = "Resuelve en inverso todas las direcciones IP con las que está hablando cada " +
                          "programa y las agrupa por aplicación: dejas de ver «3.145.22.8» y empiezas a ver " +
                          "«chrome → google-analytics.com». Encima compara cada destino con una lista de " +
                          "dominios de rastreo y telemetría (Microsoft, Google, Meta, Adobe, Unity, " +
                          "crashlytics…) y marca los que lo son. Si quieres cortar uno de verdad, el botón " +
                          "lo escribe en el fichero hosts de Windows, con su copia de seguridad y su " +
                          "marcha atrás.",
                Pasos = new[]
                {
                    "Enciende el interruptor de arriba. El radar empieza a resolver los destinos que ya " +
                    "están abiertos; los primeros tardan unos segundos.",
                    "Elige el alcance: «Ahora» mira solo lo conectado en este instante; «Histórico» repasa " +
                    "todos los destinos que PCJ guardó desde que instalaste el programa.",
                    "Pulsa una fila de la lista para ver, por esa aplicación, cada destino con su IP, su país " +
                    "y si está en la lista de rastreo.",
                    "Los destinos en coral son dominios de rastreo o telemetría. Si quieres bloquearlos, " +
                    "márcalos con la casilla y pulsa «Bloquear elegidos (hosts)».",
                    "Bloquear por hosts escribe esos dominios apuntando a 0.0.0.0 en " +
                    "C:\\Windows\\System32\\drivers\\etc\\hosts, dentro de un bloque propio con la marca PCJ. " +
                    "Nunca toca el resto del fichero.",
                    "«Quitar bloqueos» devuelve el hosts a como estaba. Si el programa se cierra de golpe, " +
                    "al volver a arrancar PCJ comprueba el bloque y lo repara.",
                    "Si prefieres cortar la aplicación entera y no solo el dominio, pulsa «En protección» en " +
                    "su fila: te lleva a la pestaña Protección con esa aplicación buscada, y ahí le quitas la salida.",
                },
                Consumo = "Un sondeo cada 5 segundos y resoluciones DNS inversas con caché. Sin lista " +
                          "activa, apenas se nota en CPU.",
            },

            new ToolDef
            {
                Id = "firmas",
                Nombre = "Identidad de las apps",
                Lema = "Firma digital y ruta de cada programa",
                Glifo = 0xE8D7,
                Color = "#4361EE",
                QueHace = "Comprueba la firma digital de cada ejecutable que sale en la lista de Protección " +
                          "y con qué editor está firmado. Es la diferencia entre «chrome.exe» y " +
                          "«chrome.exe firmado por Google LLC en C:\\Program Files\\Google\\Chrome». Un " +
                          "programa que se hace pasar por otro, o un chrome.exe en una carpeta temporal, " +
                          "salta aquí en rojo aunque en el resto del monitor parezca normal.",
                Pasos = new[]
                {
                    "Enciende el interruptor. La primera pasada firma las aplicaciones que PCJ ya conoce; " +
                    "puede tardar unos segundos porque Windows verifica una por una.",
                    "Mira la columna de estado: «firmada por X» en verde, «sin firmar» en ámbar y " +
                    "«firma rota o suplantada» en coral.",
                    "Filas en coral: abre el detalle para ver la ruta real del fichero y el nombre con el " +
                    "que está firmado. Si la ruta es una carpeta temporal o el nombre no cuadra con el " +
                    "editor, quítale la salida desde Protección.",
                    "«Repasar ahora» vuelve a comprobarlas todas; útil después de una actualización grande " +
                    "o de instalar algo nuevo.",
                    "El resultado se guarda en disco, así que una app ya analizada no se vuelve a firmar en " +
                    "cada arranque: solo cuando cambia su fichero.",
                },
                Consumo = "Solo trabaja cuando algo cambia: firma por lotes en segundo plano y caché en " +
                          "signature.json. Con la lista hecha, no consume nada.",
            },

            new ToolDef
            {
                Id = "cuota",
                Nombre = "Cuota de datos",
                Lema = "Lo que gastas día, semana y mes",
                Glifo = 0xEDB6,
                Color = "#E8912A",
                QueHace = "Lleva la cuenta acumulada de lo que entra y sale del equipo por día, semana y " +
                          "mes, partida por aplicación, y avisa cuando te acercas al límite que tú pongas. " +
                          "Sirve tanto si tienes datos móviles compartidos como si solo quieres saber a " +
                          "dónde se van los 300 GB del mes.",
                Pasos = new[]
                {
                    "Enciende el interruptor.",
                    "En «Cuota mensual» escribe tus gigas (0 deja el límite quitado, pero sigue contando).",
                    "En «Avisar al llegar al» pon el porcentaje: con 80 te saltará el aviso al 80 % de la " +
                    "cuota, una sola vez al mes.",
                    "Cambia el día de reinicio si tu facturación no empieza en día 1.",
                    "La barra grande es el mes en curso; debajo, las aplicaciones que más han gastado. " +
                    "«Este mes» y «Hoy» alternan lo que ves.",
                    "«Empezar de cero» borra las cuentas guardadas; no toca el histórico de tráfico.",
                },
                Consumo = "Suma lo que ya mide el monitor y escribe un número pequeño en cuota.json cada " +
                          "cierto rato. No añade sondeos nuevos.",
            },

            new ToolDef
            {
                Id = "tiempo",
                Nombre = "Máquina del tiempo",
                Lema = "Volver a un momento y ver qué estaba hablando",
                Glifo = 0xE917,
                Color = "#7C5CD6",
                QueHace = "Guarda cada poco tiempo una foto del tráfico por aplicación y te deja deslizar " +
                          "una línea de tiempo para ver qué estaba subiendo o bajando a las tres de la " +
                          "tarde de ayer. Es lo que necesitas cuando notas el PC lento o la red saturada y " +
                          "quieres saber quién fue, cuando ya pasó.",
                Pasos = new[]
                {
                    "Enciende el interruptor. Empieza a guardar muestras; hasta que no hay unos minutos de " +
                    "datos la línea estará corta.",
                    "Mueve el deslizador de arriba para viajar por el tiempo. La lista de la izquierda es " +
                    "lo que se movió en ese intervalo, no el acumulado.",
                    "Con «Ventana» eliges de cuánto en cuánto se mira (1, 5 o 15 minutos).",
                    "«Ahora» te devuelve al presente y sigue actualizando sola.",
                    "El tiempo que se conserva se manda con «Días a guardar» de Configuración: al pasar de " +
                    "ese número, las muestras viejas se borran solas.",
                    "En modo incógnito no se guarda ninguna muestra, igual que el resto del histórico.",
                },
                Consumo = "Una muestra cada 30 segundos a disco (líneas JSONL). Un día entero son unos " +
                          "pocos cientos de KB.",
            },

            new ToolDef
            {
                Id = "perfiles",
                Nombre = "Perfiles y candado",
                Lema = "Cambiar de reglas de un clic, y cortar todo",
                Glifo = 0xE8B7,
                Color = "#2E9E6B",
                QueHace = "Guarda conjuntos de permisos de salida con nombre —Casa, Trabajo, Público— y " +
                          "cámbialos de un clic sin repasar aplicación por aplicación. Y para levantarte de " +
                          "la silla, el candado corta toda la salida de golpe y la devuelve igual de rápido " +
                          "cuando vuelves.",
                Pasos = new[]
                {
                    "Enciende el interruptor.",
                    "«Guardar el estado actual como perfil» toma la lista de aplicaciones que ahora tienen " +
                    "salida y la guarda con el nombre que escribas.",
                    "«Aplicar» en un perfil deja el bloqueo total activo con exactamente esa lista de " +
                    "permitidas; lo que no esté, se queda sin salida.",
                    "Cada perfil dice cuándo lo guardaste y cuántas aplicaciones tenía.",
                    "El candado («Cortar toda la salida») no borra tus reglas: las deja aparte y «Soltar el " +
                    "candado» las devuelve tal cual.",
                    "Si el PC se apaga con el candado puesto, al arrancar PCJ lo suelta y te avisa, para no " +
                    "dejarte sin internet por un olvido.",
                    "Cambiar perfiles o el candado toca el firewall de Windows: se hace con tus permisos de " +
                    "administrador y queda anotado en el registro.",
                },
                Consumo = "No mira nada por sí solo: es una lista guardada y botones. Se apaga solo si " +
                          "cierras la ventana.",
            },

            new ToolDef
            {
                Id = "fugas",
                Nombre = "Auditor de fugas",
                Lema = "QUIC, DoH, DNS raro y reglas sucias",
                Glifo = 0xE945,
                Color = "#C4453F",
                QueHace = "Pasa una revisión de seguridad al equipo y te dice, en lenguaje claro, lo que " +
                          "está escapando a tu control: tráfico saliendo por UDP 443 (QUIC/HTTP3, que " +
                          "muchos filtros no ven), servidores DNS sobre HTTPS que saltan tu resolver, " +
                          "servidores DNS configurados que no son los que crees, reglas del firewall que " +
                          "apuntan a programas ya borrados o duplicadas, y aplicaciones con salida " +
                          "permitida que no necesitan internet.",
                Pasos = new[]
                {
                    "Enciende el interruptor y pulsa «Comprobar ahora». Tarda unos segundos y no cambia nada.",
                    "Cada línea sale en verde (correcto), ámbar (revisa) o coral (fuga clara), con la " +
                    "explicación de por qué importa.",
                    "«Limpiar reglas obsoletas» borra las MRPCJ cuyo ejecutable ya no existe; es lo único " +
                    "que el auditor escribe por su cuenta, y solo si pulsas el botón.",
                    "Para el QUIC, la recomendación es bloquear UDP 443 a las aplicaciones que no lo " +
                    "necesiten; el botón te lleva a esa fila en Protección.",
                    "«Revisar cada hora», si lo activas, añade una pasada automática en segundo plano y " +
                    "anota en el registro cualquier cosa nueva que aparezca.",
                    "El informe del auditor se copia entero con «Copiar resumen», por si quieres guardarlo.",
                },
                Consumo = "Sin «Revisar cada hora», solo trabaja cuando pulsas el botón. Con repaso " +
                          "horario, un vistazo a las tablas cada 60 minutos.",
            },

            new ToolDef
            {
                Id = "paises",
                Nombre = "Tráfico por país",
                Lema = "Dónde están los servidores con los que hablas",
                Glifo = 0xE7FC,
                Color = "#1F7A8C",
                QueHace = "Asocia cada dirección IP con la que hablas a un país y agrupa los destinos " +
                          "por país y por aplicación. Se nota enseguida una actualización que se va a un " +
                          "continente que no esperabas, o un programa que manda datos a un país donde no " +
                          "tienes nada. Avisos opcionales cuando aparece un país que no habías visto nunca. " +
                          "Saber el país cuesta una consulta fuera del equipo, así que viene apagada: " +
                          "encendida, PCJ manda cada IP una sola vez a ip-api.com y se queda en caché.",
                Pasos = new[]
                {
                    "Enciende el interruptor.",
                    "Marca «Consultar ip-api.com para saber el país de cada IP». Es la única forma de " +
                    "saber el país sin salir a internet: esa dirección se pregunta una vez por IP y se " +
                    "guarda en geo-cache.json, así que no vuelve a salir. Si prefieres no preguntar a " +
                    "nadie, deja la casilla apagada y suelta en la carpeta de datos tu propia base con el " +
                    "nombre geoipv4.csv (tres columnas: IP inicial, IP final, código de país); si existe, " +
                    "PCJ la usa y no sale para esto.",
                    "Con la casilla apagada y sin base local, casi todo sale como «Sin resolver»: no es un " +
                    "fallo, es que PCJ no ha preguntado. El pie te dice cuántos destinos faltan.",
                    "«Resolver ahora» manda de golpe las direcciones que faltan, de cien en cien en cada " +
                    "petición. La lista se va rellenando sola mientras la herramienta esté encendida.",
                    "La lista ordena los países por número de destinos vistos, y deja «Sin resolver» al " +
                    "final para que lo primero que veas sea lo que sí sabes. El volumen por país no se " +
                    "puede desglosar: PCJ mide los bytes por aplicación, no por dirección.",
                    "Activa «Avisar si aparece un país nuevo» para que cada estreno quede también en el " +
                    "registro.",
                    "El botón «Quitar la caché» borra lo consultado, por si quieres empezar de cero.",
                },
                Consumo = "Con la casilla apagada y base local: cero red extra. Encendida, una petición por " +
                          "cada cien IP desconocidas, con caché permanente en geo-cache.json.",
            },

            new ToolDef
            {
                Id = "lan",
                Nombre = "Guardián de la LAN",
                Lema = "Dispositivos nuevos, cambios y redes gemelas",
                Glifo = 0xE968,
                Color = "#B58A00",
                QueHace = "Vigila lo que hay en tu red: avisa cuando entra un dispositivo nuevo, cuando una " +
                          "misma MAC cambia de nombre o de fabricante, y cuando aparece un punto de acceso " +
                          "con el nombre de tu wifi pero distinta dirección de hardware (la trampa de la red " +
                          "gemela). Incluye el escáner completo de toda la subred.",
                Pasos = new[]
                {
                    "Enciende el interruptor.",
                    "«Escanear ahora» recorre la subred y actualiza la lista de equipos.",
                    "Cada equipo muestra nombre, dirección IP, MAC y el origen del nombre (resolución DNS o " +
                    "NetBIOS de la propia red).",
                    "Con «Avisar de dispositivos nuevos» cada entrada queda anotada en el registro y en un " +
                    "aviso flotante.",
                    "«Confiar» en un equipo lo deja marcado como conocido para que no vuelva a saltar.",
                    "Si usas wifi, el guardián compara las redes que ve el adaptador: misma SSID con otra " +
                    "BSSID sale como sospechosa.",
                    "«Olvidar todo» borra la lista de equipos guardada.",
                },
                Consumo = "Un ping barrido por la subred cuando escanea (unos segundos, 48 a la vez) y una " +
                          "lectura del wifi. Con el auto-escaneo apagado, solo trabaja si le das al botón.",
            },

            new ToolDef
            {
                Id = "puertos",
                Nombre = "Puertos expuestos",
                Lema = "Qué está escuchando desde tu red",
                Glifo = 0xE977,
                Color = "#5A6B7B",
                QueHace = "Lista todos los puertos que tienen programas en escucha y, lo que importa, desde " +
                          "dónde se puede entrar: un puerto atado a 127.0.0.1 solo lo ve tu PC; atado a " +
                          "0.0.0.0 lo ve cualquiera de tu red. Marca los servicios delicados (escritorio " +
                          "remoto, compartidos de archivos, PowerShell remoto, bases de datos) y avisa " +
                          "cuando aparece un escuchador que antes no estaba.",
                Pasos = new[]
                {
                    "Enciende el interruptor.",
                    "La tabla muestra programa, puerto, protocolo y alcance. «solo tu PC» es inofensivo; " +
                    "«en tu red» y «en cualquier sitio» son los que hay que mirar.",
                    "El color depende del firewall. Si la entrada está cortada por defecto, los puertos " +
                    "delicados salen en verde con la nota «firewall corta entrada»: escuchan, pero no se " +
                    "puede entrar. Solo se ponen en rojo si son de verdad alcanzables desde la red.",
                    "«Bloquear entrada» crea una regla de entrada que corta ese puerto concreto, sin apagar " +
                    "el servicio de Windows (pide UAC solo en ese momento). «Quitar bloqueo» la borra.",
                    "«Copiar» lleva la fila al portapapeles, por si quieres apuntarla.",
                    "Activa «Avisar si se abre un puerto nuevo» para enterarte en el momento; cada estreno " +
                    "queda también en el registro.",
                },
                Consumo = "Lee la tabla TCP/UDP que el monitor ya está leyendo; no añade sondeos propios.",
            },

            new ToolDef
            {
                Id = "informe",
                Nombre = "Informe de seguridad",
                Lema = "Resumen del mes, en HTML o CSV",
                Glifo = 0xE9F9,
                Color = "#0F8A7E",
                QueHace = "Monta un informe con lo que ha pasado: aplicaciones que más han salido, destinos " +
                          "de rastreo vistos, intentos bloqueados, dispositivos nuevos, puertos expuestos, " +
                          "países y decisiones pendientes. Lo guarda como página HTML para leerla cómoda o " +
                          "como CSV para abrirlo en una hoja de cálculo.",
                Pasos = new[]
                {
                    "Enciende el interruptor solo cuando lo vayas a usar: no mira nada mientras esté apagado.",
                    "Elige el periodo (hoy, 7 días, 30 días, todo).",
                    "Marca las secciones que quieras incluir.",
                    "«Generar HTML» o «Generar CSV» escribe el fichero en tu carpeta de datos y te pregunta " +
                    "si quieres abrirlo.",
                    "El informe no manda nada a ningún sitio: se hace con lo que PCJ ya tiene en disco.",
                    "«Ver el último generado» abre el que hiciste por última vez.",
                },
                Consumo = "Nulo fuera del momento de generarlo: lee los ficheros y escribe uno.",
            },

            // ---------- Las dos que se mudaron desde la ventana principal ----------
            new ToolDef
            {
                Id = "escaner",
                Nombre = "Escáner de red",
                Lema = "Los equipos de tu red, vista clásica",
                Glifo = 0xE7F4,
                Color = "#2E7D9A",
                EsExistente = true,
                QueHace = "La pantalla de siempre del escáner: la lista de dispositivos encontrados en la " +
                          "subred con su nombre, IP, MAC y cuándo se vio por última vez. Sigue siendo la " +
                          "misma; lo único que cambió es que ya no ocupa un hueco en la ventana principal.",
                Pasos = new[]
                {
                    "«Escanear» recorre la subred del adaptador activo.",
                    "«Auto-escaneo» deja que PCJ lo repita cada los minutos que pusiste en Configuración.",
                    "Para avisos de equipos nuevos y redes gemelas, usa el Guardián de la LAN.",
                },
                Consumo = "Siempre disponible: no tiene interruptor propio.",
            },

            new ToolDef
            {
                Id = "registros",
                Nombre = "Registros",
                Lema = "Avisos y decisiones, día a día",
                Glifo = 0xE7C3,
                Color = "#6A6FA8",
                EsExistente = true,
                QueHace = "El registro de todo lo que PCJ consideró importante: primeras conexiones, " +
                          "decisiones de salida, dispositivos nuevos, cambios de protección y avisos del " +
                          "auditor. Estaba en la ventana principal y se ha mudado aquí, que es donde se " +
                          "consulta de vez en cuando.",
                Pasos = new[]
                {
                    "Filtra por «Importantes» o «Todos», y por rango de tiempo.",
                    "«Marcar todo como leído» limpia el contador de avisos sin borrar nada.",
                    "Para vaciar el registro entero, usa «Borrar todo el histórico» en Configuración.",
                },
                Consumo = "Siempre disponible: no tiene interruptor propio.",
            },
        };

        public static ToolDef? PorId(string? id) =>
            string.IsNullOrEmpty(id) ? null : Todas.FirstOrDefault(t => t.Id == id);

        public static List<ToolDef> Nuevas => Todas.Where(t => !t.EsExistente).ToList();

        /// Estado de la herramienta. Las que se mudaron de sitio se consideran siempre activas.
        public static bool Activada(string id)
        {
            var def = PorId(id);
            if (def == null) return false;
            if (def.EsExistente) return true;
            var dict = App.Settings.Current.Herramientas;
            return dict != null && dict.TryGetValue(id, out var v) && v;
        }

        public static void Cambiar(string id, bool on)
        {
            var s = App.Settings.Current;
            s.Herramientas ??= new Dictionary<string, bool>();
            s.Herramientas[id] = on;
            App.Settings.Save();
            ToolHost.Instancia?.Aplicar(id, on);
        }

        // ---- Lista de dominios de rastreo y telemetría ----
        //
        // Curada a mano con lo que de verdad se cruza en un PC de casa: analíticas web,
        // telemetría de Windows y de ofimática, anuncios, crash report de móviles y SDKs de
        // juegos. Se compara por sufijo, asi que "doubleclick.net" vale para cualquier
        // subdominio. No es una lista comercial: es la que se puede uno mirar cara a cara.
        public static readonly string[] Rastreadores =
        {
            // Google / Firebase
            "google-analytics.com", "googletagmanager.com", "doubleclick.net", "googlesyndication.com",
            "adservice.google.com", "googleadservices.com", "app-measurement.com", "firebase-settings.crashlytics.com",
            "crashlytics.com", "firebaseio.com", "google.com/ads", "pagead2.googlesyndication.com",
            "stats.g.doubleclick.net", "analytics.google.com",
            // Microsoft / Windows / Office
            "settings-win.data.microsoft.com", "v10.events.data.microsoft.com", "v20.events.data.microsoft.com",
            "telemetry.microsoft.com", "watson.telemetry.microsoft.com", "self.events.data.microsoft.com",
            "login.live.com", "device.auth.xboxlive.com", "arc.msn.com", "config.svc.cloud.microsoft",
            "events.data.microsoft.com", "ssistats.microsoft.com", "odwebp.svc.ms",
            // Meta
            "facebook.net", "an.facebook.com", "connect.facebook.net", "graph.facebook.com",
            "facebook-hardware.com", "instagram.com/api", "mbasic.facebook.com",
            // Publicidad y analiticas sueltas
            "scorecardresearch.com", "quantserve.com", "adsafeprotected.com", "smartadserver.com",
            "criteo.com", "taboola.com", "outbrain.com", "moatads.com", "bluekai.com", "exoclick.com",
            "popads.net", "hotjar.com", "clarity.ms", "mixpanel.com", "segment.io", "segment.com",
            "amplitude.com", "chartbeat.com", "chartbeat.net", "parsely.com", "piwik.pro", "matomo",
            "yandex.ru/metrika", "mc.yandex.ru", "yandex.metrika", "cnzz.com", "umeng.com", "baidu.com/hm.html",
            "gorgias.chat", "intercom.io", "drift.com", "zendesk.com", "userreport.com", "licdn.com/px",
            // Juegos y motores
            "unity3d.com/ads", "unityads.unity3d.com", "config.unity3d.com", "analytics.samsungads.com",
            "gameguardian.net", "epicgames.com/api/analytics", "akamaihd.net",
            // Asistentes y SDKs varios
            "adjust.com", "appsflyer.com", "branch.io", "kochava.com", "tapstream.com", "flurry.com",
            "inmobi.com", "vungle.com", "ironsrc.com", "adcolony.com", "integral.com", "liftoff.io",
            "bidswitch.net", "rubiconproject.com", "pubmatic.com", "openx.net", "adnxs.com", "casalemedia.com",
            "amazon-adsystem.com", "adscale.de", "taboola.com", "serving-sys.com", "yieldmo.com",
            // Telemetria de periféricos y utilidades
            "logitechg.com/api", "logi.com/installers", "razer.com/telemetry", "corsair.com/mercury",
            "asus.com/aisuite", "adguard.com/vpn/stats", "ccproxy", "teamviewer.com/api",
        };

        /// ¿Este nombre de host (o su IP resuelta en texto) pinta a rastreo?
        public static bool EsRastreador(string host)
        {
            if (string.IsNullOrEmpty(host)) return false;
            string h = host.ToLowerInvariant().TrimEnd('.');
            foreach (var t in Rastreadores)
            {
                string needle = t.ToLowerInvariant();
                if (h == needle || h.EndsWith("." + needle) || h.Contains(needle)) return true;
            }
            return false;
        }
    }
}
