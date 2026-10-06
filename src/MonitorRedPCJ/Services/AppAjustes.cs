using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using MonitorRedPCJ.Models;

namespace MonitorRedPCJ.Services
{
    // ============================================================
    // Configuración adicional por aplicación (1.6.0)
    // ============================================================
    //
    // La pastilla de Salida solo dice «sale» o «no sale». Aquí se escribe el término medio:
    // cinco modos de salir, más dos refuerzos que se pueden combinar con ellos (quedarse en la
    // red local y heredar el permiso a los procesos que la aplicación engendra). La idea está
    // tomada de lo que ofrece TinyWall; la implementación es propia, sobre reglas de Windows
    // Firewall creadas por PCJ con nombre "MRPCJ: <app> AJUSTE ...".
    //
    // Dos cosas manda saber antes de leer las reglas de abajo:
    //
    //  · En Windows Firewall UN BLOQUEO SIEMPRE GANA A UN PERMISO que coincida con el mismo
    //    tráfico, da igual el orden. Por eso "permitir solo unos puertos" NO se puede hacer
    //    con "bloqueo de todo + permiso de los puertos": el bloqueo se comería el permiso. Se
    //    hace al revés, con UNA SOLA regla de bloqueo que cubre los puertos QUE NO están en la
    //    lista (el complementario, escrito como rangos). Así el tráfico permitido no casa con
    //    ningún bloqueo y sale, y el resto sí casa.
    //
    //  · Con el bloqueo total puesto, la salida está cortada por la política del perfil, no por
    //    una regla. Una regla de permiso propia sí vence a esa política, que es justo lo que
    //    hacen los modos que dejan salir.

    /// <summary>Una opción del grupo «modo de salida fino».</summary>
    public class ModoInfo
    {
        public string Id { get; init; } = "";
        public string Nombre { get; init; } = "";

        // Lo que se lee en la fila de Protección: tiene que caber en la pastilla.
        public string Corto { get; init; } = "";
        public string Detalle { get; init; } = "";

        // Si el modo necesita la caja de puertos para tener sentido.
        public bool NecesitaPuertos { get; init; }

        // Si al aplicarlo hay que quitar el bloqueo de salida de la pastilla, porque el modo
        // concede salida y el bloqueo viejo se comería el permiso nuevo.
        public bool ConcedeSalida { get; init; }

        // Si al aplicarlo la aplicación se queda sin ninguna regla de PCJ encima.
        public bool QuitaTodo { get; init; }
    }

    public static class AppAjustes
    {
        public const string SinConfigurar = "";

        // Las direcciones que PCJ llama "red local". LocalSubnet es la palabra clave de Windows
        // para lo que está en el mismo segmento; las cuatro privado-rangadas de atrás son para
        // la red enrutada de casa o de oficina, que LocalSubnet no alcanza. 127/8 para que un
        // programa que habla consigo mismo (base de datos local, proxy) no se corte a sí mismo.
        public const string RangoLocal =
            "LocalSubnet,10.0.0.0/8,172.16.0.0/12,192.168.0.0/16,169.254.0.0/16,127.0.0.0/8";

        public static readonly ModoInfo[] Modos =
        {
            new ModoInfo
            {
                Id = SinConfigurar,
                Nombre = "Sin configurar",
                Corto = "",
                Detalle = "No se añade ninguna regla fina: manda la pastilla de Salida de siempre " +
                          "(Permitido o Bloqueado). Es el estado con el que nace cada aplicación.",
            },
            new ModoInfo
            {
                Id = "bloquearTodo",
                Nombre = "Bloquear siempre todo el tráfico",
                Corto = "corte total",
                Detalle = "La aplicación no puede ni enviar ni recibir, aunque la pastilla de Salida " +
                          "esté en Permitido y aunque se quite el bloqueo total. Vale para un programa " +
                          "que no necesitas desconectado del todo, sino imposibilitado: es la regla " +
                          "más fuerte que hay y no la deshace ningún permiso. Si luego quieres " +
                          "devolverle la red, vuelve aquí y pon «Sin configurar».",
            },
            new ModoInfo
            {
                Id = "soloPuertos",
                Nombre = "Permitir solo los puertos especificados",
                Corto = "solo puertos",
                NecesitaPuertos = true,
                ConcedeSalida = true,
                Detalle = "La aplicación solo puede sacar tráfico por los puertos que escribas; por " +
                          "cualquier otro queda cortada. Es el modo de un servidor o de un programa de " +
                          "telemetría que no tiene por qué hablar por donde no toca. Se escribe " +
                          "separado por comas, con rangos con dos puntos, y con /udp detrás si el puerto " +
                          "es UDP: «443, 80, 8000:8100, 53/udp, 123/udp». Sin /udp se toma como TCP.",
            },
            new ModoInfo
            {
                Id = "salidaTcpUdp",
                Nombre = "Permitir tráfico saliente de UDP y TCP",
                Corto = "sale TCP/UDP",
                ConcedeSalida = true,
                Detalle = "La aplicación sale por TCP y por UDP sin límite de puertos, pero nada más: " +
                          "los demás protocolos (ICMP del ping, túneles, otros) siguen cortados por la " +
                          "política. Es el permiso normal de un programa de red, dicho en fino, y con " +
                          "él no hace falta tener la pastilla en Permitido.",
            },
            new ModoInfo
            {
                Id = "sinRestriccionTcpUdp",
                Nombre = "Sin restricciones de tráfico UDP y TCP",
                Corto = "sin límite",
                ConcedeSalida = true,
                Detalle = "La aplicación sale por cualquier protocolo y cualquier puerto, y se le quitan " +
                          "los cortes finos que tuviera puestos (puertos concretos, red local). Es la " +
                          "salida plena: útil con el bloqueo total activado, cuando quieres que esta " +
                          "programa pase sin condiciones sin tener que marcarlo en el asistente.",
            },
            new ModoInfo
            {
                Id = "sinRestricciones",
                Nombre = "Sin restricciones",
                Corto = "sin reglas de PCJ",
                QuitaTodo = true,
                Detalle = "PCJ se aparta de esta aplicación: borra todas sus reglas nuestras, también " +
                          "las de siempre, y Windows decide con la política general. Ojo con el orden: " +
                          "con el modo normal activado se queda con salida libre; con el bloqueo total " +
                          "activado se queda SIN salida, porque ya no hay permiso que la defienda. " +
                          "Sirve para dejar una app como si nunca se hubiera tocado.",
            },
        };

        public static ModoInfo PorId(string? id)
            => Modos.FirstOrDefault(m => m.Id == (id ?? "")) ?? Modos[0];

        public static string NombreDe(string? id) => PorId(id).Nombre;

        // ---------- Lectura escrita en la fila desplegada ----------

        // La configuración de una app dicha en una línea, para verla sin abrir el diálogo.
        public static string Resumen(TrackedApp? app)
        {
            if (app == null) return "";
            var partes = new List<string>();
            var m = PorId(app.ModoAdicional);
            if (!string.IsNullOrEmpty(app.ModoAdicional))
                partes.Add(m.Id == "soloPuertos" && app.PuertosAdicionales.Trim().Length > 0
                    ? $"{m.Corto} · {app.PuertosAdicionales}"
                    : m.Corto);
            if (app.SoloRedLocal) partes.Add("solo red local");
            if (app.HeredarAHijos) partes.Add("hereda a los procesos hijo");
            return partes.Count == 0 ? "" : string.Join(" · ", partes);
        }

        // El texto largo que se pega debajo del tráfico en la fila abierta. Si la configuración
        // no la puso el usuario en esta aplicación sino que la heredó de la que la abrió, se dice
        // aquí: un corte que aparece solo en la lista sin explicar de dónde viene parece un fallo.
        public static string DetalleFila(TrackedApp? app)
        {
            string r = Resumen(app);
            if (r.Length == 0) return "";
            if (app!.HeredadaDe.Trim().Length > 0)
                r += " · heredada de " + Path.GetFileNameWithoutExtension(app.HeredadaDe);
            return "Configuración adicional: " + r;
        }

        // ---------- Qué hacer cuando el usuario cambia la configuración ----------

        /// <summary>
        /// Escribe las reglas finas de una aplicación. Se parte de cero: las nueve posibles se
        /// borran y se crean solo las que pide el modo elegido, para que cambiar de un modo a
        /// otro no deje cortes olvidados. Devuelve el listado de reglas y, si algo no cuadra
        /// (la lista de puertos, por ejemplo), un mensaje en español que se puede mostrar tal cual.
        /// </summary>
        public static bool Construir(string exePath, TrackedApp cfg,
                                     List<string> borrar, List<FirewallService.ReglaSpec> crear,
                                     out string error)
        {
            error = "";
            string exeName = Path.GetFileNameWithoutExtension(exePath);
            foreach (string s in FirewallService.AjusteSufijos)
                borrar.Add(FirewallService.AjusteRuleName(exePath, s));

            var m = PorId(cfg.ModoAdicional);

            // Los modos que conceden salida tienen que deshacer el bloqueo de la pastilla, o el
            // bloqueo de siempre (que no mira puertos) se comería el permiso fino.
            if (m.ConcedeSalida)
                borrar.Add(FirewallService.RuleName(exePath, Direction.Out));

            // El corte total se escribe también como la regla de siempre, para que la pastilla de
            // Salida diga «Bloqueado» sin tener que leer las reglas finas: hay que quitar el
            // permiso que pudiera estar puesto, y borrar antes de crear con el mismo nombre.
            if (m.Id == "bloquearTodo")
            {
                borrar.Add(FirewallService.RuleName(exePath, Direction.Out));
                borrar.Add(FirewallService.AllowRuleName(exePath, Direction.Out));
            }

            if (m.QuitaTodo)
            {
                // "Sin restricciones": PCJ se aparta de esta app. Se borran las reglas de siempre en
                // los dos sentidos; no se crea nada.
                borrar.Add(FirewallService.RuleName(exePath, Direction.Out));
                borrar.Add(FirewallService.AllowRuleName(exePath, Direction.Out));
                borrar.Add(FirewallService.RuleName(exePath, Direction.In));
                borrar.Add(FirewallService.AllowRuleName(exePath, Direction.In));
                return true;
            }

            switch (m.Id)
            {
                case "bloquearTodo":
                    crear.Add(SpecNombre(FirewallService.RuleName(exePath, Direction.Out),
                        "Bloqueo aplicado por Monitor de Red PCJ",
                        dir: 2, act: 0, exe: exePath));
                    crear.Add(Spec(exePath, "ENTRADA BLOQUEO FINA",
                        "Monitor de Red PCJ: nadie puede conectar con " + exeName,
                        dir: 1, act: 0, orden: FirewallService.OrdenBloqueo));
                    // Con el corte total no hay refuerzo que valga: no se escriben ni los de
                    // red local, que pedirían permiso justo encima de este bloqueo.
                    return true;

                case "soloPuertos":
                    string puertos = (cfg.PuertosAdicionales ?? "").Trim();
                    if (puertos.Length == 0)
                    {
                        error = "Escribe los puertos que quieres permitir; sin lista no hay " +
                                "modo «solo los puertos especificados».";
                        return false;
                    }
                    if (!ParsearPuertos(puertos, out var tcp, out var udp, out error)) return false;

                    // Cada protocolo va por su cuenta: una regla de permiso con los puertos
                    // marcados y UNA regla de bloqueo con todo lo demás. Las dos con protocolo
                    // concreto, nunca 255.
                    if (!CrearComplementario(exePath, "SALIDA TCP PERMISO FINO", "SALIDA TCP BLOQUEO FINO",
                            "TCP", 6, tcp, crear, out error)) return false;
                    if (!CrearComplementario(exePath, "SALIDA UDP PERMISO FINO", "SALIDA UDP BLOQUEO FINO",
                            "UDP", 17, udp, crear, out error)) return false;
                    break;

                case "salidaTcpUdp":
                    crear.Add(Spec(exePath, "SALIDA TCP PERMISO FINO",
                        "Monitor de Red PCJ: " + exeName + " sale por TCP",
                        dir: 2, act: 1, proto: "6", orden: FirewallService.OrdenPermiso));
                    crear.Add(Spec(exePath, "SALIDA UDP PERMISO FINO",
                        "Monitor de Red PCJ: " + exeName + " sale por UDP",
                        dir: 2, act: 1, proto: "17", orden: FirewallService.OrdenPermiso));
                    break;

                case "sinRestriccionTcpUdp":
                    // Sin protocolo y sin puertos: "cualquiera", que es como Windows guarda la
                    // salida plena.
                    crear.Add(Spec(exePath, "SALIDA PERMISO FINO",
                        "Monitor de Red PCJ: " + exeName + " sale sin límite de puertos",
                        dir: 2, act: 1, orden: FirewallService.OrdenPermiso));
                    break;
            }

            // El refuerzo de red local va después del modo, y no choca: permite las direcciones
            // de la casa y corta las de internet, que son conjuntos que no se solapan. Con el
            // modo «solo puertos» los puertos siguen mandando en lo que sale a la red local.
            if (cfg.SoloRedLocal)
            {
                crear.Add(Spec(exePath, "SALIDA LOCAL PERMISO FINO",
                    "Monitor de Red PCJ: " + exeName + " habla con la red local",
                    dir: 2, act: 1, ra: RangoLocal, orden: FirewallService.OrdenPermiso));
                crear.Add(Spec(exePath, "SALIDA LOCAL BLOQUEO FINO",
                    "Monitor de Red PCJ: " + exeName + " no sale a internet",
                    dir: 2, act: 0, ra: "Internet", orden: FirewallService.OrdenBloqueo));
            }

            return true;
        }

        private static FirewallService.ReglaSpec Spec(string exePath, string sufijo, string desc,
            int dir, int act, string proto = "", string rp = "", string ra = "", int orden = 0)
            => SpecNombre(FirewallService.AjusteRuleName(exePath, sufijo), desc,
                dir: dir, act: act, proto: proto, rp: rp, ra: ra, exe: exePath, orden: orden);

        // La misma regla pero con el nombre entero ya elegido, para las reglas que no son finas
        // (el corte total comparte nombre con la pastilla de Salida de siempre).
        private static FirewallService.ReglaSpec SpecNombre(string nombre, string desc,
            int dir, int act, string proto = "", string rp = "", string ra = "",
            string exe = "", int orden = 0)
            => new FirewallService.ReglaSpec
            {
                n = nombre,
                d = desc,
                dir = dir,
                act = act,
                proto = proto,
                rp = rp,
                ra = ra,
                exe = exe,
                orden = orden,
            };

        // Permiso con los puertos marcados + bloqueo con el resto, para un protocolo.
        private static bool CrearComplementario(string exePath, string sufPermiso, string sufBloqueo,
            string nombreProto, int protoNum, List<(int a, int b)> puertos,
            List<FirewallService.ReglaSpec> crear, out string error)
        {
            error = "";
            string exeName = Path.GetFileNameWithoutExtension(exePath);

            // Sin puertos de este protocolo, no hace falta el permiso: todo el protocolo se corta
            // con una sola regla.
            string lista = Intervalos(puertos);
            if (lista.Length > 0)
                crear.Add(Spec(exePath, sufPermiso,
                    $"Monitor de Red PCJ: {exeName} sale por {nombreProto} solo por {lista}",
                    dir: 2, act: 1, proto: protoNum.ToString(), rp: lista,
                    orden: FirewallService.OrdenPermiso));

            string resto = Complementario(puertos);
            if (resto.Length > 0)
            {
                if (resto.Length > 240)
                {
                    error = "La lista de puertos permitidos es tan larga que el resto no cabe en " +
                            "una sola regla de Windows. Deja menos puertos o usa un rango suelto.";
                    return false;
                }
                crear.Add(Spec(exePath, sufBloqueo,
                    $"Monitor de Red PCJ: {exeName} no sale por {nombreProto} fuera de los puertos elegidos",
                    dir: 2, act: 0, proto: protoNum.ToString(), rp: resto,
                    orden: FirewallService.OrdenBloqueo));
            }
            return true;
        }

        // ---------- La lista de puertos escrita a mano ----------

        /// <summary>
        /// Lee algo como "443, 80, 8000:8100, 53/udp" y lo reparte en dos listas de intervalos,
        /// una por protocolo. Acepta coma, punto y coma y espacio como separadores, rango con
        /// dos puntos o con guion, y el sufijo /udp o /tcp (o /U, /T) en cada trozo.
        /// </summary>
        public static bool ParsearPuertos(string texto, out List<(int a, int b)> tcp,
            out List<(int a, int b)> udp, out string error)
        {
            tcp = new List<(int, int)>();
            udp = new List<(int, int)>();
            error = "";

            string[] trozos = (texto ?? "").Split(new[] { ',', ';', ' ' },
                StringSplitOptions.RemoveEmptyEntries);
            if (trozos.Length == 0)
            {
                error = "No se ha entendido la lista de puertos.";
                return false;
            }

            foreach (string bruto in trozos)
            {
                string t = bruto.Trim().ToLowerInvariant();
                bool esUdp = false;
                int barra = t.IndexOf('/');
                if (barra >= 0)
                {
                    string suf = t.Substring(barra + 1).Trim();
                    if (suf == "udp" || suf == "u") esUdp = true;
                    else if (suf == "tcp" || suf == "t") esUdp = false;
                    else
                    {
                        error = $"En «{bruto}» solo cabe /tcp o /udp después del puerto.";
                        return false;
                    }
                    t = t.Substring(0, barra).Trim();
                }

                // El guion de -1024 no es un rango: es el signo, y aquí no tiene sitio.
                int sep = t.IndexOfAny(new[] { ':', '-' }, 1);
                int desde, hasta;
                if (sep < 0)
                {
                    if (!Numero(t, out desde))
                    {
                        error = $"«{bruto}» no es un puerto. Escribe un número entre 1 y 65535, " +
                                "un rango como 8000:8100, y /udp detrás si es UDP.";
                        return false;
                    }
                    hasta = desde;
                }
                else
                {
                    string a = t.Substring(0, sep), b = t.Substring(sep + 1);
                    if (!Numero(a, out desde) || !Numero(b, out hasta))
                    {
                        error = $"El rango «{bruto}» no está bien escrito. Se pone 8000:8100.";
                        return false;
                    }
                }

                if (desde < 1 || hasta > 65535 || desde > hasta)
                {
                    error = $"«{bruto}» se sale de los puertos que existen (1 a 65535).";
                    return false;
                }
                (esUdp ? udp : tcp).Add((desde, hasta));
            }

            if (tcp.Count == 0 && udp.Count == 0)
            {
                error = "No se ha entendido la lista de puertos.";
                return false;
            }
            Ordenar(tcp);
            Ordenar(udp);
            return true;
        }

        private static bool Numero(string s, out int v)
            => int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out v);

        // Une los solapamientos para que la lista escrita sea la mínima.
        private static void Ordenar(List<(int a, int b)> l)
        {
            l.Sort((x, y) => x.a != y.a ? x.a.CompareTo(y.a) : x.b.CompareTo(y.b));
            for (int i = l.Count - 1; i > 0; i--)
            {
                if (l[i].a <= l[i - 1].b + 1)
                {
                    int a = l[i - 1].a, b = Math.Max(l[i - 1].b, l[i].b);
                    l[i - 1] = (a, b);
                    l.RemoveAt(i);
                }
            }
        }

        // Lista de puertos como la entiende Windows: separada por comas y SIN espacios.
        private static string Intervalos(List<(int a, int b)> l)
            => string.Join(",", l.Select(p => p.a == p.b ? p.a.ToString() : $"{p.a}-{p.b}"));

        // Todo lo que NO está en la lista, dentro de 1..65535, escrito como rangos.
        private static string Complementario(List<(int a, int b)> permitidos)
        {
            var fuera = new List<(int a, int b)>();
            int desde = 1;
            foreach (var (a, b) in permitidos)
            {
                if (a > desde) fuera.Add((desde, a - 1));
                desde = Math.Max(desde, b + 1);
                if (desde > 65535) break;
            }
            if (desde <= 65535) fuera.Add((desde, 65535));
            return Intervalos(fuera);
        }

        // ---------- Aplicar de verdad ----------

        /// <summary>
        /// Guarda la configuración en el histórico y escribe sus reglas en Windows Firewall. Un
        /// solo viaje a la copia elevada, aunque el modo necesite seis reglas. Devuelve false con
        /// el motivo en <paramref name="error"/> cuando Windows o el usuario dicen que no.
        /// </summary>
        public static bool Aplicar(TrackedApp cfg, out string error)
        {
            error = "";
            string exe = cfg.ExePath;

            var borrar = new List<string>();
            var crear = new List<FirewallService.ReglaSpec>();
            if (!Construir(exe, cfg, borrar, crear, out error)) return false;
            var m = PorId(cfg.ModoAdicional);

            // Aquí todavía no se toca apps.json: si Windows rechaza las reglas, la configuración
            // guardada tiene que seguir contando lo que de verdad está puesto.
            bool ok = App.Firewall.ApplySpecs(borrar, crear);
            if (!ok)
            {
                error = FirewallService.LastError.Length > 0
                    ? FirewallService.LastError
                    : "Windows Firewall no aceptó las reglas de la configuración adicional.";
                return false;
            }

            // La coherencia de la pastilla: el corte total y los modos que dejan salida han
            // tocado la regla de siempre dentro del lote, así que aquí solo se rearma el
            // marcador propio. No se llama a SetDecision: pediría otra ventana de UAC para no
            // hacer nada, porque el firewall ya está como debe.
            if (m.ConcedeSalida || m.Id == "bloquearTodo" || m.QuitaTodo)
                App.Firewall.RefreshMarker(exe, Direction.Out);

            Guardar(cfg);
            if (FirewallService.LastWarning.Length > 0)
                error = "Algunas reglas no se pudieron escribir: " + FirewallService.LastWarning;
            return true;
        }

        // Apunta la configuración en el histórico de la aplicación. Los programas que nunca
        // llegaron a conectar no tienen fila en apps.json: NoteManualApp se las crea.
        public static void Guardar(TrackedApp cfg)
        {
            var destino = App.Traffic.FindApp(cfg.ExePath);
            if (destino == null) destino = App.Traffic.NoteManualApp(cfg.ExePath);
            destino.ModoAdicional = cfg.ModoAdicional ?? "";
            destino.PuertosAdicionales = cfg.PuertosAdicionales ?? "";
            destino.SoloRedLocal = cfg.SoloRedLocal;
            destino.HeredarAHijos = cfg.HeredarAHijos;
            App.Traffic.NoteAppConfig();
        }

        /// <summary>
        /// Si el padre tiene algo que pasarle a sus hijos. Con la configuración sin tocar y sin
        /// corte explícito, heredar no significa nada y no se escribe ni una regla.
        /// </summary>
        public static bool NadaQueHeredar(TrackedApp padre)
            => string.IsNullOrEmpty(padre.ModoAdicional) && !padre.SoloRedLocal &&
               !App.Firewall.HasRealRule(FirewallService.RuleName(padre.ExePath, Direction.Out));

        /// <summary>
        /// Las reglas de un programa hijo, copiadas de las de su padre. Lo usa el vigilante de
        /// procesos: cuando aparece un hijo de una app con «heredar» puesto, su salida queda como
        /// la de quien le abrió. El hijo NO sigue la cadena (su propia marca de heredar se deja
        /// apagada), para que un nieto no haga de biznieto y el repaso no crezca solo.
        /// También se copia el corte de la pastilla, que es lo que más se nota: si al padre le
        /// está cortada la salida con regla propia, al hijo se la corta igual.
        /// </summary>
        public static void ConstruirHijo(string exeHijo, TrackedApp padre,
                                         List<string> borrar, List<FirewallService.ReglaSpec> crear)
        {
            var copia = new TrackedApp
            {
                ExePath = exeHijo,
                ModoAdicional = padre.ModoAdicional,
                PuertosAdicionales = padre.PuertosAdicionales,
                SoloRedLocal = padre.SoloRedLocal,
                HeredarAHijos = false,
            };
            var borrarHijo = new List<string>();
            var crearHijo = new List<FirewallService.ReglaSpec>();
            // El modo del padre puede no ser construible para el hijo (una lista de puertos que
            // ya no cabe): en ese caso se hereda solo el corte, que no depende de los puertos.
            if (Construir(exeHijo, copia, borrarHijo, crearHijo, out string err) && err.Length == 0)
            {
                borrar.AddRange(borrarHijo);
                crear.AddRange(crearHijo);
            }

            string nombreHijo = Path.GetFileNameWithoutExtension(exeHijo);
            string nombrePadre = Path.GetFileNameWithoutExtension(padre.ExePath);
            string baseHijo = FirewallService.RuleName(exeHijo, Direction.Out);
            if (App.Firewall.HasRealRule(FirewallService.RuleName(padre.ExePath, Direction.Out)))
            {
                borrar.Add(baseHijo);
                crear.Add(SpecNombre(baseHijo,
                    $"Monitor de Red PCJ: {nombreHijo} hereda el corte de {nombrePadre}",
                    dir: 2, act: 0, exe: exeHijo));
            }
        }

        /// <summary>
        /// Qué procesos de los que hay ahora en marcha deben heredar, y de quién. Es la parte que
        /// se puede equivocar de las cuatro: emparejar el proceso con su padre. El padre tiene que
        /// estar en la lista, tener la herencia pedida y tener algo que pasar; el hijo no puede ser
        /// el propio monitor, ni el mismo programa que le abrió, ni hijo de un padre que ya no está.
        /// Sale hijo → padre, con la ruta del ejecutable como clave de los dos.
        /// </summary>
        public static Dictionary<string, string> HijosHeredables(
            Dictionary<int, string> pidExe, Dictionary<int, int> padreDe,
            Func<string, TrackedApp?> buscar, string? propioExe = null)
        {
            var deseados = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in pidExe)
            {
                string hijo = kv.Value;
                if (hijo.Length == 0 || deseados.ContainsKey(hijo)) continue;
                if (propioExe != null && string.Equals(hijo, propioExe, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!padreDe.TryGetValue(kv.Key, out int pidPadre)) continue;
                if (!pidExe.TryGetValue(pidPadre, out string? padre) || padre.Length == 0) continue;
                if (string.Equals(padre, hijo, StringComparison.OrdinalIgnoreCase)) continue;

                var cfg = buscar(padre);
                if (cfg == null || !cfg.HeredarAHijos) continue;
                if (NadaQueHeredar(cfg)) continue;
                deseados[hijo] = padre;
            }
            return deseados;
        }

        // ============================================================
        // Autocomprobación (--ajuste-check)
        // ============================================================
        //
        // Es lógica pura: construye las reglas sobre el papel y las mira. No se llama a ApplySpecs
        // en ningún momento, así que se puede ejecutar sin aprobación de administrador y sin
        // tocar el firewall de la máquina. Lo que comprueba es lo que no se ve en una fotografía:
        // que cada modo escriba justo las reglas que promete y que la lista de puertos se lea y se
        // complementarias bien.
        public static class Check
        {
            private static readonly List<string> fallos = new();
            private static int aciertos;

            public static void Ejecutar(string archivo)
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("Comprobación de la configuración adicional — " +
                              DateTime.Now.ToString("dd/MM/yyyy HH:mm"));
                sb.AppendLine();

                ModosCoherentes();
                PuertosBien();
                PuertosMal();
                ReglasDeCadaModo();
                RedLocal();
                Hijos();
                HijosHeredablesBien();
                PadresReales();
                ViajeElevado();

                sb.AppendLine();
                sb.AppendLine(fallos.Count == 0
                    ? "TODO CORRECTO · " + aciertos + " comprobaciones"
                    : "FALLOS: " + fallos.Count + " de " + (aciertos + fallos.Count));
                foreach (string f in fallos) sb.AppendLine("  - " + f);
                try { File.WriteAllText(archivo, sb.ToString()); } catch { }
            }

            private static void Mira(bool ok, string que, string detalle = "")
            {
                if (ok) { aciertos++; return; }
                fallos.Add(que + (detalle.Length > 0 ? " [" + detalle + "]" : ""));
            }

            // ---------- Catálogo ----------

            private static void ModosCoherentes()
            {
                Mira(Modos.Length == 6, "seis modos en el catálogo", "hay " + Modos.Length);
                Mira(PorId(null!).Id == SinConfigurar, "un id desconocido cae en «sin configurar»");
                Mira(PorId("noexiste").Id == SinConfigurar, "ídem con un nombre raro");

                // Que ningún nombre fino termine como las reglas de siempre: si lo hiciera, el
                // asistente de salida y los perfiles contarían un permiso que no es.
                foreach (var m in Modos)
                {
                    Mira(m.Nombre.Length > 0 && m.Detalle.Length > 40,
                        m.Id + " tiene nombre y descripción larga");
                }
                foreach (string s in FirewallService.AjusteSufijos)
                {
                    string n = FirewallService.AjusteRuleName("C:\\app.exe", s);
                    Mira(!n.EndsWith(FirewallService.AllowSuffixOut) &&
                         !n.EndsWith(FirewallService.AllowSuffixIn) &&
                         n.Contains(" AJUSTE "),
                         "el sufijo fino no se confunde con una regla de siempre", s);
                }
                Mira(FirewallService.AjusteSufijos.Length == 9,
                    "nueve sufijos finos, los que Construir sabe escribir",
                    FirewallService.AjusteSufijos.Length.ToString());
            }

            // ---------- La lista de puertos ----------

            private static void PuertosBien()
            {
                Mira(ParsearPuertos("443, 80, 8000:8100, 53/udp, 123/udp",
                    out var tcp, out var udp, out string e1) && e1.Length == 0,
                    "se lee la lista de ejemplo", e1);
                Mira(tcp.Count == 3, "tres trozos TCP", "son " + tcp.Count);
                Mira(udp.Count == 2, "dos trozos UDP", "son " + udp.Count);
                Mira(tcp[0].a == 80 && tcp[1].a == 443 && tcp[2].a == 8000 && tcp[2].b == 8100,
                    "ordenados y con el rango cerrado", Texto(tcp));
                Mira(udp[0].a == 53 && udp[1].a == 123, "los UDP por su lado", Texto(udp));

                // Adjacentes se unen: 80 y 81 son un rango, no dos reglas de relleno.
                ParsearPuertos("80,81,443", out var t2, out _, out _);
                Mira(t2.Count == 2 && t2[0].b == 81, "80 y 81 se sueldan en 80-81", Texto(t2));

                // Solapados también.
                ParsearPuertos("100-200,150-300", out var t3, out _, out _);
                Mira(t3.Count == 1 && t3[0].b == 300, "dos rangos que se tocan se unen", Texto(t3));

                // Mayúsculas, punto y coma y espacios sueltos.
                Mira(ParsearPuertos("443/UDP;80 , 8080", out var t4, out var u4, out string e4) &&
                     t4.Count == 2 && u4.Count == 1, "se admiten mayúsculas, ; y espacios", e4);

                // Comas de más: se perdonan, porque escribir «443,» es lo que hace todo el mundo.
                Mira(ParsearPuertos("443,,80,", out var t9, out _, out string e9) && t9.Count == 2,
                    "las comas vacías se ignoran", e9 + " · son " + t9.Count);

                // El complementario de la lista de ejemplo, que es la regla que de verdad corta.
                ParsearPuertos("443, 80, 53/udp", out var t5, out var u5, out _);
                string comp = ComplementarioPublico(t5);
                Mira(comp == "1-79,81-442,444-65535", "el complementario TCP es exacto", comp);
                Mira(ComplementarioPublico(u5) == "1-52,54-65535",
                    "el complementario UDP es exacto", ComplementarioPublico(u5));

                // Permitir todos los puertos: no hace falta ninguna regla de corte.
                ParsearPuertos("1:65535", out var t6, out _, out _);
                Mira(ComplementarioPublico(t6).Length == 0,
                    "con 1-65535 permitido no queda nada que cortar", ComplementarioPublico(t6));

                // Que la lista más corta posible (un puerto) dé el complementario más largo, y que
                // siga cabiendo en una regla.
                ParsearPuertos("443", out var t7, out _, out _);
                string c7 = ComplementarioPublico(t7);
                Mira(c7 == "1-442,444-65535", "un puerto suelto deja dos rangos", c7);
                Mira(c7.Length <= 240, "y el complementario cabe en una regla", c7.Length.ToString());
            }

            private static void PuertosMal()
            {
                string[] malos =
                {
                    "", "0", "65536", "abc", "80:70", "443/x", "-80", "1-2-3", "443 udp",
                };
                foreach (string m in malos)
                {
                    // "443 udp" se parte por el espacio en "443" y "udp", y "udp" no es número.
                    bool ok = ParsearPuertos(m, out _, out _, out string err);
                    Mira(!ok && err.Length > 0, "se rechaza «" + m + "»", ok ? "aceptado" : "sin mensaje");
                }
                // El vacío no es una lista: tiene que avisar, no quedarse callado.
                Mira(!ParsearPuertos("   ", out _, out _, out _), "los espacios sueltos no valen");
            }

            private static string Texto(List<(int a, int b)> l)
                => string.Join(" ", l.Select(p => p.a + "-" + p.b));

            // El complementario por sí solo, para poder comprobarlo sin montar reglas.
            public static string ComplementarioPublico(List<(int a, int b)> permitidos)
                => Complementario(permitidos);

            // ---------- Las reglas de cada modo ----------

            // El resultado de construir sobre el papel, para poder mirarlo entero.
            private class Sondeo
            {
                public bool Ok;
                public string Error = "";
                public readonly List<string> Borrar = new();
                public readonly List<FirewallService.ReglaSpec> Crear = new();
                public FirewallService.ReglaSpec? Uno(string terminacion)
                    => Crear.FirstOrDefault(s => s.n.EndsWith(terminacion));
            }

            private static Sondeo ConstruirDe(string exe, string modo, string puertos = "",
                bool local = false)
            {
                var cfg = new TrackedApp
                {
                    ExePath = exe,
                    ModoAdicional = modo,
                    PuertosAdicionales = puertos,
                    SoloRedLocal = local,
                };
                var s = new Sondeo();
                s.Ok = Construir(exe, cfg, s.Borrar, s.Crear, out s.Error);
                return s;
            }

            private static void ReglasDeCadaModo()
            {
                const string exe = "C:\\Program Files\\Prueba\\app.exe";

                // Sin configurar: borra las nueve finas y no crea nada, y no toca la pastilla.
                {
                    var r = ConstruirDe(exe, SinConfigurar);
                    Mira(r.Ok && r.Error.Length == 0 && r.Crear.Count == 0 && r.Borrar.Count == 9,
                        "«sin configurar» no crea nada",
                        "crea " + r.Crear.Count + ", borra " + r.Borrar.Count);
                    Mira(!r.Borrar.Contains(FirewallService.RuleName(exe, Direction.Out)),
                        "«sin configurar» respeta el bloqueo de la pastilla");
                }

                // Corte total: la regla de siempre de salida + la fina de entrada.
                {
                    var r = ConstruirDe(exe, "bloquearTodo");
                    Mira(r.Ok && r.Error.Length == 0, "el corte total se construye", r.Error);
                    Mira(r.Crear.Count == 2, "el corte total son dos reglas",
                        "son " + r.Crear.Count);
                    Mira(r.Crear.Any(s =>
                            s.n == FirewallService.RuleName(exe, Direction.Out) && s.act == 0 && s.dir == 2),
                        "y una es el bloqueo de salida de siempre");
                    Mira(r.Crear.Any(s => s.dir == 1 && s.act == 0), "y la otra corta la entrada");
                    Mira(r.Borrar.Contains(FirewallService.AllowRuleName(exe, Direction.Out)),
                        "y quita el permiso viejo de la pastilla");
                }

                // Solo puertos: permiso con lo elegido + bloqueo con el resto, por protocolo.
                {
                    var r = ConstruirDe(exe, "soloPuertos", "443, 53/udp");
                    Mira(r.Ok && r.Error.Length == 0, "solo puertos se construye", r.Error);
                    var tcpPerm = r.Uno("SALIDA TCP PERMISO FINO");
                    var tcpBloq = r.Uno("SALIDA TCP BLOQUEO FINO");
                    Mira(tcpPerm != null && tcpPerm.rp == "443" && tcpPerm.proto == "6" && tcpPerm.act == 1,
                        "permiso TCP con el puerto justo", tcpPerm?.rp ?? "no está");
                    Mira(tcpBloq != null && tcpBloq.rp == "1-442,444-65535" && tcpBloq.act == 0,
                        "bloqueo TCP con el complementario", tcpBloq?.rp ?? "no está");
                    Mira(r.Uno("SALIDA UDP PERMISO FINO") is { proto: "17", rp: "53" },
                        "el UDP va en su regla");
                    Mira(r.Borrar.Contains(FirewallService.RuleName(exe, Direction.Out)),
                        "y quita el corte de la pastilla, que se comería el permiso");
                    foreach (var s in r.Crear)
                        Mira(s.proto != "255", "ninguna regla deja el protocolo en 255", s.n);
                }

                // Sin lista de puertos no hay modo: lo dice, no escribe un corte a ciegas.
                {
                    var r = ConstruirDe(exe, "soloPuertos", "");
                    Mira(!r.Ok && r.Error.Length > 0, "solo puertos sin lista avisa", r.Error);
                }

                // Salida TCP/UDP: dos permisos, uno por protocolo, sin puertos.
                {
                    var r = ConstruirDe(exe, "salidaTcpUdp");
                    Mira(r.Ok && r.Crear.Count == 2 && r.Error.Length == 0,
                        "salida TCP/UDP son dos permisos", "son " + r.Crear.Count);
                    Mira(r.Crear.All(s => s.act == 1 && s.dir == 2 && s.rp.Length == 0),
                        "permisos de salida sin límite de puertos");
                    var protos = r.Crear.Select(s => s.proto).ToList();
                    Mira(protos.Count == 2 && protos.Contains("6") && protos.Contains("17"),
                        "uno por TCP y otro por UDP",
                        string.Join("/", protos));
                }

                // Sin restricción TCP/UDP: un solo permiso sin protocolo.
                {
                    var r = ConstruirDe(exe, "sinRestriccionTcpUdp");
                    Mira(r.Ok && r.Crear.Count == 1 && r.Crear[0].proto.Length == 0 &&
                         r.Crear[0].act == 1,
                        "sin límite es un permiso abierto y sin protocolo");
                }

                // Sin restricciones: borra las de siempre y no crea nada.
                {
                    var r = ConstruirDe(exe, "sinRestricciones");
                    Mira(r.Ok && r.Crear.Count == 0 && r.Error.Length == 0,
                        "sin restricciones no crea nada");
                    string[] cuatro =
                    {
                        FirewallService.RuleName(exe, Direction.Out),
                        FirewallService.AllowRuleName(exe, Direction.Out),
                        FirewallService.RuleName(exe, Direction.In),
                        FirewallService.AllowRuleName(exe, Direction.In),
                    };
                    foreach (string n in cuatro)
                        Mira(r.Borrar.Contains(n),
                            "y borra la regla de siempre " + n.Substring(n.LastIndexOf(' ') + 1));
                }

                // Dentro de un mismo lote no puede haber dos reglas con el mismo nombre.
                {
                    var r = ConstruirDe(exe, "soloPuertos", "443");
                    var nombres = r.Crear.Select(s => s.n).ToList();
                    Mira(nombres.Distinct().Count() == nombres.Count,
                        "sin reglas repetidas dentro del mismo lote");
                }
            }

            private static void RedLocal()
            {
                const string exe = "C:\\app.exe";
                var r = ConstruirDe(exe, "salidaTcpUdp", local: true);
                Mira(r.Ok && r.Crear.Count == 4 && r.Error.Length == 0,
                    "la red local suma dos reglas", "son " + r.Crear.Count);
                var permiso = r.Uno("LOCAL PERMISO FINO");
                var corte = r.Uno("LOCAL BLOQUEO FINO");
                Mira(permiso != null && permiso.act == 1 && permiso.ra == RangoLocal,
                    "el permiso local dice las direcciones privadas", permiso?.ra ?? "no está");
                Mira(corte != null && corte.act == 0 && corte.ra == "Internet",
                    "y el corte dice Internet");

                // Con el corte total no se escribe: pediría salida encima de un corte a propósito.
                var r2 = ConstruirDe(exe, "bloquearTodo", local: true);
                Mira(!r2.Crear.Any(s => s.n.Contains("LOCAL")),
                    "el corte total no lleva refuerzo de red local");
            }

            private static void Hijos()
            {
                const string padre = "C:\\padre.exe";
                const string hijo = "C:\\hijo.exe";

                // Nada que heredar con el padre sin tocar (y sin corte puesto).
                Mira(NadaQueHeredar(new TrackedApp { ExePath = padre }),
                    "un padre sin configurar no tiene nada que heredar");
                Mira(!NadaQueHeredar(new TrackedApp
                    { ExePath = padre, ModoAdicional = "salidaTcpUdp" }),
                    "un padre con modo sí tiene");
                Mira(!NadaQueHeredar(new TrackedApp { ExePath = padre, SoloRedLocal = true }),
                    "un padre solo con red local también tiene");

                var borrar = new List<string>();
                var crear = new List<FirewallService.ReglaSpec>();
                ConstruirHijo(hijo, new TrackedApp
                {
                    ExePath = padre,
                    ModoAdicional = "soloPuertos",
                    PuertosAdicionales = "443",
                    HeredarAHijos = true,
                }, borrar, crear);
                Mira(crear.Any(s => s.exe == hijo && s.n.EndsWith("TCP PERMISO FINO")),
                    "el hijo recibe las reglas del padre, con su propio ejecutable");
                Mira(crear.All(s => s.exe == hijo && !s.n.Contains("padre")),
                    "y nunca con el nombre del padre");
                Mira(crear.All(s => !s.n.EndsWith("ENTRADA BLOQUEO FINA") ||
                                    s.exe == hijo),
                    "las reglas finas son todas del hijo");

                // La cadena se para: el hijo no hereda-a-hijos del padre.
                var borrar2 = new List<string>();
                var crear2 = new List<FirewallService.ReglaSpec>();
                ConstruirHijo(hijo, new TrackedApp
                {
                    ExePath = padre,
                    ModoAdicional = "bloquearTodo",
                    HeredarAHijos = true,
                }, borrar2, crear2);
                Mira(crear2.Any(s => s.n == FirewallService.RuleName(hijo, Direction.Out) && s.act == 0),
                    "un padre cortado corta también al hijo");
            }

            // ---------- A quién se le copian las reglas, dicho con procesos de mentira ----------

            private static void HijosHeredablesBien()
            {
                const string monitor = "C:\\pcj\\MonitorRedPCJ.exe";
                const string padre = "C:\\padre.exe";
                const string hijo = "C:\\hijo.exe";
                const string nieto = "C:\\nieto.exe";

                var fichas = new Dictionary<string, TrackedApp>(StringComparer.OrdinalIgnoreCase)
                {
                    [padre] = new TrackedApp
                    {
                        ExePath = padre,
                        ModoAdicional = "salidaTcpUdp",
                        HeredarAHijos = true,
                    },
                };
                TrackedApp? Buscar(string e) => fichas.TryGetValue(e, out var a) ? a : null;

                // El padre (pid 10) abre dos hijos (11 y 12), y el primero abre al segundo: la
                // cadena solo puede copiar un salto, y aquí aún no se mira quién es nieto.
                var pids = new Dictionary<int, string>
                {
                    [10] = padre, [11] = hijo, [12] = nieto, [13] = monitor, [14] = "",
                };
                var padres = new Dictionary<int, int>
                {
                    [10] = 1, [11] = 10, [12] = 11, [13] = 1, [14] = 1,
                };
                var salida = HijosHeredables(pids, padres, Buscar, monitor);
                Mira(salida.TryGetValue(hijo, out string? de) && de == padre,
                    "el hijo directo del padre con herencia entra en la lista");
                Mira(!salida.ContainsKey(nieto),
                    "el nieto no se apunta al abuelo: solo se mira al padre inmediato");
                Mira(!salida.ContainsKey(monitor), "el propio monitor no hereda de nadie");
                Mira(!salida.ContainsKey(padre), "el padre no se copia a sí mismo");
                Mira(!salida.ContainsKey(""), "un proceso sin ruta leída no entra");
                Mira(salida.Count == 1, "y nada más", "entraron " + salida.Count);

                // Sin la marca puesta, aunque la ficha exista, no se copia nada.
                fichas[padre].HeredarAHijos = false;
                Mira(HijosHeredables(pids, padres, Buscar, monitor).Count == 0,
                    "un padre sin la marca no suelta reglas heredadas");
                fichas[padre].HeredarAHijos = true;

                // Con la marca pero sin nada que pasar (configuración intacta y sin corte): tampoco.
                fichas[padre].ModoAdicional = "";
                fichas[padre].SoloRedLocal = false;
                Mira(HijosHeredables(pids, padres, Buscar, monitor).Count == 0,
                    "una ficha sin configurar no tiene nada que heredar");
                fichas[padre].ModoAdicional = "salidaTcpUdp";

                // El padre murió: el hijo sigue vivo pero ya no hay de quién copiar.
                var huerfano = new Dictionary<int, string> { [11] = hijo };
                Mira(HijosHeredables(huerfano, padres, Buscar, monitor).Count == 0,
                    "si el padre ya no está en la tabla, no se hereda");

                // Dos procesos del mismo ejecutable cuentan una sola vez, y las mayúsculas no
                // dividen la clave: Windows las trata como iguales.
                var dos = new Dictionary<int, string> { [10] = padre, [11] = hijo, [12] = "C:\\HIJO.exe" };
                var dosPadres = new Dictionary<int, int> { [10] = 1, [11] = 10, [12] = 10 };
                var r2 = HijosHeredables(dos, dosPadres, Buscar, monitor);
                Mira(r2.Count == 1, "dos procesos del mismo ejecutable son una sola entrada",
                    "salieron " + r2.Count);

                // Un hijo que abre su propio ejecutable (chrome que lanza chrome) no se copia a sí
                // mismo: la comparación es contra la ruta del padre, no contra el nombre.
                var mismo = new Dictionary<int, string> { [10] = padre, [11] = padre };
                var mismoPadre = new Dictionary<int, int> { [10] = 1, [11] = 10 };
                Mira(HijosHeredables(mismo, mismoPadre, Buscar, monitor).Count == 0,
                    "el proceso que es el mismo programa que su padre queda fuera");
            }

            // La ascendencia sale de una llamada a kernel32 con una estructura a medida. Si el
            // trazado de PROCESSENTRY32 estuviera mal, el desplazamiento del padre saldría
            // corrido y se heredaría de un proceso equivocado, así que se comprueba contra los
            // procesos de verdad de esta máquina: es lo único que la lógica pura no puede ver.
            private static void PadresReales()
            {
                var mapa = Native.NativeProcs.ParentMap();
                Mira(mapa.Count > 10, "la instantánea de procesos trae ascendencia",
                    "pid=" + mapa.Count);
                int yo = Environment.ProcessId;
                Mira(mapa.ContainsKey(yo), "y en esa instantánea está este propio proceso");
                if (mapa.TryGetValue(yo, out int miPadre))
                    Mira(miPadre > 0 && miPadre != yo,
                        "el padre propio tiene un pid con sentido", "padre=" + miPadre);
                Mira(mapa.All(kv => kv.Value > 0), "ningún padre sale a 0 o negativo");
                Mira(mapa.Count(kv => kv.Value == 4) >= 1,
                    "algo cuelga de System (4), como cuelga en cualquier Windows");
            }

            // El viaje que hacen las reglas cuando hay que pedírselas a Windows: se escriben a
            // JSON, se meten en base64, la copia elevada las vuelve a leer. Si algún campo se
            // perdiera por el camino, la regla elevada llegaría distinta a la que calculamos.
            private static void ViajeElevado()
            {
                var borrar = new List<string>();
                var crear = new List<FirewallService.ReglaSpec>();
                Construir("C:\\viaje.exe", new TrackedApp
                {
                    ExePath = "C:\\viaje.exe",
                    ModoAdicional = "soloPuertos",
                    PuertosAdicionales = "443,53/udp",
                    SoloRedLocal = true,
                }, borrar, crear, out _);

                var payload = new { borrar, crear };
                string b64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
                    System.Text.Json.JsonSerializer.Serialize(payload)));

                try
                {
                    var doc = System.Text.Json.JsonDocument.Parse(System.Text.Encoding.UTF8.GetString(
                        Convert.FromBase64String(b64)));
                    var leidas = new List<FirewallService.ReglaSpec>();
                    foreach (var x in doc.RootElement.GetProperty("crear").EnumerateArray())
                        leidas.Add(x.Deserialize<FirewallService.ReglaSpec>() ?? new());
                    var borradas = doc.RootElement.GetProperty("borrar").EnumerateArray()
                        .Select(y => y.GetString() ?? "").ToList();

                    Mira(leidas.Count == crear.Count && leidas.Count > 0,
                        "todas las reglas sobreviven al viaje", $"{crear.Count} → {leidas.Count}");
                    for (int i = 0; i < leidas.Count && i < crear.Count; i++)
                    {
                        string q = crear[i].n;
                        Mira(Igual(crear[i], leidas[i]), "el viaje conserva la regla " + q,
                            Diferencia(crear[i], leidas[i]));
                    }
                    Mira(borradas.Count == borrar.Count &&
                         borradas.SequenceEqual(borrar),
                        "la lista de borrado llega igual", $"{borrar.Count} → {borradas.Count}");
                }
                catch (Exception ex)
                {
                    Mira(false, "el viaje JSON sobrevive", ex.Message);
                }
            }

            private static bool Igual(FirewallService.ReglaSpec a, FirewallService.ReglaSpec b)
                => a.n == b.n && a.d == b.d && a.dir == b.dir && a.act == b.act &&
                   a.proto == b.proto && a.lp == b.lp && a.rp == b.rp && a.ra == b.ra &&
                   a.exe == b.exe && a.orden == b.orden;

            private static string Diferencia(FirewallService.ReglaSpec a, FirewallService.ReglaSpec b)
            {
                var sb = new System.Text.StringBuilder();
                void C(string nom, object? x, object? y)
                { if (!Equals(x, y)) sb.Append($"  {nom}: «{x}» ≠ «{y}»"); }
                C("d", a.d, b.d); C("dir", a.dir, b.dir); C("act", a.act, b.act);
                C("proto", a.proto, b.proto); C("lp", a.lp, b.lp); C("rp", a.rp, b.rp);
                C("ra", a.ra, b.ra); C("exe", a.exe, b.exe); C("orden", a.orden, b.orden);
                return sb.ToString().Trim();
            }
        }
    }
}
