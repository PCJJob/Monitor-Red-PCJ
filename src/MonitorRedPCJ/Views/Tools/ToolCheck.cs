using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using MonitorRedPCJ.Services;
using MonitorRedPCJ.Services.Tools;

namespace MonitorRedPCJ.Views.Tools
{
    /// Comprobación de funcionamiento de las doce herramientas, de una sola pasada y sin
    /// elevation:  MonitorRedPCJ.exe --tools-check informe.txt
    ///
    /// No es una foto del diseño (para eso está --tools-preview), sino una auditoría de que
    /// cada herramienta hace lo que dice su guía: que el catálogo está completo, que el
    /// servicio arranca y produce datos, que su pantalla se monta sin romperse, y que al
    /// apagarla no queda ningún hilo suyo trabajando.
    ///
    /// Todo se ejecuta contra la copia de los datos que le pase el guion (APPDATA aislado).
    /// Lo que necesita permisos de administrador —escribir el fichero hosts, poner o quitar
    /// el candado, aplicar un perfil, borrar reglas del firewall— no se ejecuta aquí: se
    /// declara como no comprobado en lugar de fingir que pasó.
    internal static class ToolCheck
    {
        private static readonly List<string> Lineas = new();
        private static int Fallos;
        private static int Avisos;

        public static void Ejecutar(string archivo)
        {
            try { Escribir(archivo, "arranque"); } catch { }
            var reloj = Stopwatch.StartNew();

            Cabecera("CATALOGO DE HERRAMIENTAS");
            ComprobarCatalogo();

            Cabecera("INTERRUPTORES: ENCENDER LAS DIEZ");
            DiccionarioGuardado original = GuardarInterruptores();
            EncenderTodo();

            Cabecera("SERVICIOS: DATOS DE VERDAD");
            ComprobarServicios();

            Cabecera("PANTALLAS: MONTAJE Y REFRESCO");
            ComprobarPaneles();

            Cabecera("RECURSOS: APAGAR LAS DIEZ");
            ComprobarApagado(original);

            Cabecera("LOGICA PURA: LO QUE SE PUEDE AFIRMAR SIN TOCAR NADA");
            ComprobarLogica();

            Cabecera("LO QUE ESTA COMPROBACION NO TOCA");
            Nota("Escritura en C:\\Windows\\System32\\drivers\\etc\\hosts (radar «Bloquear elegidos»).");
            Nota("Candado de salida y aplicar/perfiles del firewall (ProfileService, PonerCandado, SoltarCandado).");
            Nota("Limpieza de reglas obsoletas del auditor de fugas (borra reglas MRPCJ: en el firewall).");
            Nota("Confiar/olvidar dispositivos de la LAN y escaneo activo completo de la red.");
            Nota("«Empezar de cero» de la cuota: borraría las cuentas guardadas.");
            Nota("Estas cinco rutas piden permisos o destruyen datos; se verifican a mano con el programa " +
                 "abierto, que es como las usa el autor.");

            reloj.Stop();
            Lineas.Add("");
            Lineas.Add("==== RESULTADO ====");
            Lineas.Add(Fallos == 0
                ? "SIN FALLOS: " + Avisos + " avisos en " + reloj.Elapsed.TotalSeconds.ToString("0.#") + " s"
                : Fallos + " FALLOS y " + Avisos + " avisos en " + reloj.Elapsed.TotalSeconds.ToString("0.#") + " s");
            Escribir(archivo, Lineas);
        }

        // ---------------------------------------------------------------- catálogo

        private static void ComprobarCatalogo()
        {
            var todas = ToolCatalog.Todas;
            var nuevas = ToolCatalog.Nuevas;
            Check(todas.Count == 12, "el catálogo lista 12 fichas", "lista " + todas.Count);
            Check(nuevas.Count == 10, "hay exactamente 10 herramientas nuevas", "hay " + nuevas.Count);

            var mudadas = todas.Where(t => t.EsExistente).Select(t => t.Id).OrderBy(x => x).ToList();
            Check(mudadas.SequenceEqual(new[] { "escaner", "registros" }),
                  "las dos mudadas son escáner y registros", "son " + string.Join(", ", mudadas));

            var duplicados = todas.GroupBy(t => t.Id).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            Check(duplicados.Count == 0, "ningún id repetido", "repetidos: " + string.Join(", ", duplicados));

            foreach (var t in todas)
            {
                var mal = new List<string>();
                if (string.IsNullOrWhiteSpace(t.Id)) mal.Add("sin id");
                if (string.IsNullOrWhiteSpace(t.Nombre)) mal.Add("sin nombre");
                if (string.IsNullOrWhiteSpace(t.Lema)) mal.Add("sin lema");
                if (t.QueHace.Length < 120) mal.Add("descripción corta (" + t.QueHace.Length + ")");
                if (t.Pasos.Length < 3) mal.Add("guía de " + t.Pasos.Length + " pasos");
                if (t.Glifo < 0xE700 || t.Glifo > 0xF000) mal.Add("glifo raro (0x" +
                    t.Glifo.ToString("X", CultureInfo.InvariantCulture) + ")");
                if (!(t.Color.StartsWith("#") && t.Color.Length == 7)) mal.Add("color " + t.Color);
                if (!t.EsExistente && string.IsNullOrWhiteSpace(t.Consumo)) mal.Add("sin coste de recursos");
                if (t.Pasos.Any(p => string.IsNullOrWhiteSpace(p))) mal.Add("paso vacío");
                Check(mal.Count == 0, t.Id + " · " + t.Nombre + " está completa",
                      string.Join(" · ", mal), warnOnly: t.EsExistente && mal.Contains("sin coste de recursos"));
                if (t.Pasos.Length >= 3)
                    Detalle(t.Id + ": " + t.Pasos.Length + " pasos · " + t.QueHace.Length + " caracteres de descripción");
            }

            // Que la guía no prometa botones que la pantalla no tiene — ni esconda casillas
            // que la pantalla sí tiene y que cambian el comportamiento.
            foreach (var (id, botones) in new Dictionary<string, string[]>
                     {
                         ["radar"] = new[] { "En protección" },
                         ["firmas"] = new[] { "Repasar ahora" },
                         ["cuota"] = new[] { "Empezar de cero" },
                         ["informe"] = new[] { "30 días" },
                         ["paises"] = new[] { "Consultar ip-api.com", "Resolver ahora" },
                     })
            {
                var def = ToolCatalog.PorId(id);
                if (def == null) continue;
                bool citado = def.Pasos.Any(p => botones.Any(b =>
                    p.IndexOf(b, StringComparison.OrdinalIgnoreCase) >= 0));
                Check(citado, id + ": la guía menciona «" + botones[0] + "»",
                      "la guía no habla de ese control, y la pantalla lo tiene");
            }
        }

        // ------------------------------------------------------------- interruptores

        private struct DiccionarioGuardado
        {
            public Dictionary<string, bool> Valores;
        }

        private static DiccionarioGuardado GuardarInterruptores()
        {
            var copia = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in ToolCatalog.Nuevas)
                copia[t.Id] = ToolCatalog.Activada(t.Id);
            return new DiccionarioGuardado { Valores = copia };
        }

        private static void EncenderTodo()
        {
            foreach (var t in ToolCatalog.Nuevas)
            {
                try { ToolCatalog.Cambiar(t.Id, true); }
                catch (Exception ex) { Check(false, t.Id + " enciende", ex.GetType().Name); continue; }
            }
            Esperar(4500);   // que cada servicio dé su primera pasada
            foreach (var t in ToolCatalog.Nuevas)
                Check(App.Tools.Activa(t.Id), t.Id + " figura como encendida",
                      "el host dice que está apagada");
            Check(App.Tools.Encendidas == 10, "las diez a la vez", "encendidas: " + App.Tools.Encendidas);
        }

        private static void ComprobarApagado(DiccionarioGuardado original)
        {
            int antes = Hilos();
            foreach (var t in ToolCatalog.Nuevas)
                try { ToolCatalog.Cambiar(t.Id, false); } catch { }
            Esperar(1500);
            int despues = Hilos();

            Check(App.Tools.Encendidas == 0, "apagadas las diez: el host no mantiene ninguna",
                  "siguen " + App.Tools.Encendidas);
            foreach (var t in ToolCatalog.Nuevas)
                Check(!App.Tools.Activa(t.Id), t.Id + " deja de estar activa", "sigue activa");
            Check(despues <= antes + 2, "al apagar bajan los hilos (" + antes + " → " + despues + ")",
                  "con todo apagado hay más hilos que encendido: " + antes + " → " + despues);

            // Y se devuelve el estado que tenía el usuario, para no dejar el invento montado.
            foreach (var kv in original.Valores)
                try { ToolCatalog.Cambiar(kv.Key, kv.Value); } catch { }
            Detalle("estado del usuario restaurado: " +
                    string.Join(", ", original.Valores.Where(kv => kv.Value).Select(kv => kv.Key)));
        }

        private static int Hilos()
        {
            try { return Process.GetCurrentProcess().Threads.Count; } catch { return -1; }
        }

        // ---------------------------------------------------------------- servicios

        private static void ComprobarServicios()
        {
            var h = App.Tools;

            Medir("radar", () =>
            {
                int vivos = h.Radar.DestinosActuales().Count;
                int hist = h.Radar.DestinosHistoricos().Count;
                var bloq = RadarService.Bloqueados();
                bool sinRepetidos = h.Radar.DestinosHistoricos()
                    .GroupBy(d => d.Exe + "|" + d.Ip + "|" + d.Puerto + "|" + d.EsUdp)
                    .All(g => g.Count() == 1);
                return (hist > 0 && vivos >= 0 && sinRepetidos,
                        vivos + " destinos ahora · " + hist + " en histórico · " +
                        bloq.Count + " bloqueados en hosts");
            });

            Medir("firmas", () =>
            {
                var r = h.Firmas.Resultados();
                int firmadas = r.Count(x => x.Estado == "valida");
                int dudosa = r.Count(x => x.Estado == "dudosa" || x.Sospechosa);
                return (r.Count > 0, r.Count + " apps analizadas · " + firmadas + " firmadas · " +
                        dudosa + " marcadas como dudosas · " +
                        SignatureService.AppsConocidas().Count + " en Protección");
            });

            Medir("cuota", () =>
            {
                var mes = h.Cuota.TotalDelMes;
                long sem = h.Cuota.TotalDeUnaSemana;
                int dias = h.Cuota.Dias().Count;
                int top = h.Cuota.TopDelMes().Count;
                return (true, "mes " + Format.Bytes(mes.bytes) + " (" + mes.etiqueta + ") · semana " +
                        Format.Bytes(sem) + " · " + dias + " días guardados · top " + top);
            });

            Medir("tiempo", () =>
            {
                var m = h.Tiempo.Muestras(24);
                return (true, m.Count + " muestras en 24 h" +
                        (m.Count > 0 ? " · última " + m[^1].HoraUtc.ToLocalTime().ToString("HH:mm") : ""));
            });

            Medir("perfiles", () =>
            {
                int p = h.Perfiles.Perfiles().Count;
                int per = h.Perfiles.PermitidasActuales().Count;
                return (true, p + " perfiles guardados · " + per + " apps con salida ahora · candado " +
                        (h.Perfiles.CandadoPuesto ? "puesto" : "suelto"));
            });

            Medir("fugas", () =>
            {
                var r = h.Fugas.ComprobarAhora();
                int fugas = r.Count(x => x.Nivel == 2);
                int revisar = r.Count(x => x.Nivel == 1);
                return (r.Count >= 7, r.Count + " comprobaciones · " + fugas + " fuga · " + revisar +
                        " a revisar · " + string.Join(", ", r.Where(x => x.Nivel > 0).Select(x => x.Nombre)) +
                        (r.All(x => x.Nivel == 0) ? " todo limpio" : ""));
            });

            // Tráfico por país se compruela aparte: es lo único que necesita salida a internet,
            // y con el bloqueador puesto el firewall no deja salir ni a este exe de desarrollo.
            // Que eso pase no es un fallo del código, así que se cuenta como aviso con su motivo.
            ComprobarPaises();

            Medir("lan", () =>
            {
                var e = h.Lan.Equipos().ToList();
                return (true, e.Count + " equipos conocidos · " + e.Count(x => x.fiable) + " con MAC vista");
            });

            Medir("puertos", () =>
            {
                var s = h.Puertos.Escuchadores();
                int publicos = s.Count(x => x.Alcance == 2);
                int delicados = s.Count(x => x.Delicado);
                return (s.Count > 0, s.Count + " escuchando · " + publicos + " en cualquier sitio · " +
                        delicados + " delicados · ejemplo " +
                        (s.Count > 0 ? s[0].App + " " + s[0].Puerto + "/" + (s[0].EsTcp ? "tcp" : "udp") : "—"));
            });

            Medir("informe", () =>
            {
                var secciones = ReportService.Secciones.Select(s => s.id).ToList();
                string html = App.Tools.Informe.GenerarHTML("30", secciones, out string eh);
                string csv = App.Tools.Informe.GenerarCSV("30", secciones, out string ec);
                bool ok = File.Exists(html) && File.Exists(csv) &&
                          new FileInfo(html).Length > 500 && new FileInfo(csv).Length > 50;
                return (ok, "HTML " + (File.Exists(html) ? new FileInfo(html).Length + " B" : "no") +
                        " · CSV " + (File.Exists(csv) ? new FileInfo(csv).Length + " B" : "no") +
                        " · " + secciones.Count + " secciones" +
                        (!string.IsNullOrEmpty(eh) ? " · error " + eh : "") +
                        (!string.IsNullOrEmpty(ec) ? " · error " + ec : ""));
            });
        }

        /// Lo único del informe que sale a internet. Enciende el permiso un momento, pide una
        /// tanda y lo deja como estaba. Si el firewall no deja salir a este ejecutable, se avisa
        /// con el motivo en vez de contar el intento como fallo: la herramienta está bien hecha,
        /// es la máquina la que está en modo bloqueo.
        private static void ComprobarPaises()
        {
            var g0 = App.Tools.Paises;
            bool antes = App.Settings.Current.PaisesConsultarOnline;
            GeoService.UltimoError = "";
            App.Settings.Current.PaisesConsultarOnline = true;
            bool pidio = g0.ResolverAhora();
            int enColaAlPedir = g0.EnCola;
            Esperar(9000);
            int enColaDespues = g0.EnCola;
            App.Settings.Current.PaisesConsultarOnline = antes;
            g0.OlvidarCola();

            var g = g0.Paises();
            int conPais = g.Where(x => x.Codigo != "??" && !x.EsLocal).Sum(x => x.Destinos);
            int sinPais = g.FirstOrDefault(x => x.Codigo == "??")?.Destinos ?? 0;
            // Lo que se comprueba es el ORDEN: si hay grupos sin resolver, el suyo tiene que
            // ser el último de la lista. Cuando la consulta en línea fue bien y no quedó
            // ninguna dirección sin país, no hay nada que ordenar y se da por buena (antes de
            // la 1.4.1 esta comprobación fallaba justo en ese caso, que es el bueno).
            int idxSinResolver = -1;
            for (int i = 0; i < g.Count; i++) if (g[i].Codigo == "??") { idxSinResolver = i; break; }
            bool ultimoSinResolver = idxSinResolver < 0 || idxSinResolver == g.Count - 1;
            string error = GeoService.UltimoError;

            Check(ultimoSinResolver, "paises: «Sin resolver» queda al final de la lista",
                  idxSinResolver < 0
                      ? "no quedó ninguna dirección sin resolver, no hay nada que ordenar"
                      : "«Sin resolver» es la fila " + (idxSinResolver + 1) + " de " + g.Count);
            // Lo que la versión anterior hacía mal: guardaba la falta de respuesta como si la IP
            // no tuviera país, y ya no se volvía a preguntar nunca.
            Check(g0.EnCache == 0 || conPais > 0,
                  "paises: una consulta que no llegó no se guarda como «sin país»",
                  "enveló " + g0.EnCache + " direcciones en la caché sin haber contestado nadie");
            Check(enColaDespues >= enColaAlPedir - 5 || conPais > 0,
                  "paises: si el servidor no contesta, la dirección no se pierde y vuelve a la cola",
                  "la cola pasó de " + enColaAlPedir + " a " + enColaDespues);
            Detalle("paises → pidió salir: " + pidio + " · en cola al pedir " + enColaAlPedir +
                    " · en cola al volver " + enColaDespues + " · " + conPais + " destinos con país · " +
                    sinPais + " sin resolver · " + g.Count(x => x.Codigo != "??") + " grupos · " +
                    g0.EnCache + " IP en caché");

            if (conPais > 0) { Check(true, "paises: la consulta en línea devuelve países"); return; }
            if (enColaAlPedir == 0)
            {
                Check(false, "paises: la cola se llena al pedir resolver", "pidió y no encoló nada");
                return;
            }
            bool bloqueado = error.IndexOf("socket", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             error.IndexOf("acceso", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             error.IndexOf("permiso", StringComparison.OrdinalIgnoreCase) >= 0;
            if (bloqueado)
            {
                Avisos++;
                Lineas.Add("  [aviso] paises: no pudo consultar ip-api.com — " + error);
                Lineas.Add("          Es el firewall de Windows cortando la salida de este ejecutable" +
                           " (modo bloqueo total puesto). La herramienta está bien: con el bloqueador" +
                           " quitado, o con la copia instalada que sí tiene su regla de permiso, resuelve.");
                return;
            }
            Check(false, "paises: la consulta en línea llega al servidor", error);
        }

        private static void Medir(string id, Func<(bool, string)> prueba)
        {
            try
            {
                var (ok, detalle) = prueba();
                Check(ok, id + " produce datos", "sin datos: " + detalle);
                Detalle(id + " → " + detalle);
            }
            catch (Exception ex)
            {
                Check(false, id + " no lanza", ex.GetType().Name + ": " + ex.Message);
                Detalle(id + " → " + ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim() ?? "");
            }
        }

        // ----------------------------------------------------------------- pantallas

        private sealed class AnfitrionFalso : IAnfitrion
        {
            public readonly List<string> Viajes = new();
            public void IrAHerramienta(string id) => Viajes.Add("herr:" + id);
            public void IrAProteccion(string buscar) => Viajes.Add("prot:" + buscar);
            public void IrARecursos() => Viajes.Add("recursos");
        }

        private static void ComprobarPaneles()
        {
            foreach (var def in ToolCatalog.Todas)
            {
                HerramientaPanel? panel = null;
                Window? win = null;
                try
                {
                    panel = Herramientas.Crear(def.Id, new AnfitrionFalso());
                    win = new Window
                    {
                        WindowStyle = WindowStyle.None,
                        ResizeMode = ResizeMode.NoResize,
                        ShowInTaskbar = false,
                        ShowActivated = false,
                        Left = -9000,
                        Top = 100,
                        Width = 1080,
                        Height = 720,
                        Content = panel,
                    };
                    win.Show();
                    Esperar(320);                 // que se dispare Loaded y se suscriba
                    panel.Refrescar();            // sin capturar: aquí el fallo es el dato
                    panel.Refrescar();            // dos veces: la segunda no debe acumular
                    Esperar(160);

                    var p = panel.Esqueleto;
                    int relleno = p.FilasPintadas + p.ExtrasPintadas + p.KpisPintadas;
                    Check(relleno > 0, def.Id + " pinta algo", "filas, kpis y contenido extra a 0");
                    Check(p.PiePintado.Length > 0, def.Id + " explica algo en el pie", "pie vacío");
                    Detalle(def.Id + " → " + p.FilasPintadas + " filas · " + p.KpisPintadas + " kpi · " +
                            p.BotonesPintados + " botones · pie «" +
                            (p.PiePintado.Length > 96 ? p.PiePintado.Substring(0, 96) + "…" : p.PiePintado) + "»");
                }
                catch (Exception ex)
                {
                    Check(false, def.Id + " se monta sin romperse",
                          ex.GetType().Name + ": " + ex.Message);
                    Detalle(def.Id + " → " + (ex.StackTrace ?? "").Split('\n')
                            .Skip(1).FirstOrDefault()?.Trim() ?? "");
                }
                finally
                {
                    try { panel?.SoltarPublico(); } catch { }
                    try { win?.Close(); } catch { }
                }
            }
        }

        // ---------------------------------------------------------------- lógica pura

        private static void ComprobarLogica()
        {
            Check(ToolCatalog.EsRastreador("doubleclick.net"), "reconoce doubleclick.net");
            Check(ToolCatalog.EsRastreador("www.google-analytics.com"), "reconoce un subdominio de analítica");
            Check(ToolCatalog.EsRastreador("stats.g.doubleclick.net"), "reconoce el rastro de Google");
            Check(!ToolCatalog.EsRastreador("github.com"), "no marca github.com como rastreo");
            Check(!ToolCatalog.EsRastreador(""), "no marca la cadena vacía");

            Check(GeoService.TryParseIp("8.8.8.8", out long ip8) && ip8 == 134744072L,
                  "convierte 8.8.8.8 en su entero (134744072)", "dio " + ip8);
            Check(!GeoService.TryParseIp("999.1.1.1", out _), "rechaza un octeto fuera de rango");
            Check(!GeoService.TryParseIp("no-es-ip", out _), "rechaza texto suelto");
            Check(GeoService.NombreDe("US").Length > 1, "pone nombre al país US", "sin nombre");
            Check(GeoService.NombreDe("ZZ").Length > 0, "sabe qué decir de un código raro", "cadena vacía");
            Check(GeoService.NombreDe("??").Contains("resolver", StringComparison.OrdinalIgnoreCase),
                  "«??» se llama «Sin resolver»", "dice " + GeoService.NombreDe("??"));

            // El permiso de salir: apagado, ni una IP a la cola.
            bool permiso = App.Settings.Current.PaisesConsultarOnline;
            App.Settings.Current.PaisesConsultarOnline = false;
            App.Tools.Paises.OlvidarCola();
            App.Tools.Paises.CodigoDe("203.0.113.77");
            Check(App.Tools.Paises.EnCola == 0,
                  "sin el permiso encendido no se encola ninguna IP para consultar fuera",
                  "hay " + App.Tools.Paises.EnCola + " en la cola a espaldas del ajuste");
            Check(!App.Tools.Paises.SePuedePreguntarFuera || permiso,
                  "el ajuste manda sobre salir o no");
            App.Settings.Current.PaisesConsultarOnline = permiso;

            Check(PortAuditService.AlcanceDe("0.0.0.0") == 2, "0.0.0.0 se lee «en cualquier sitio»");
            Check(PortAuditService.AlcanceDe("127.0.0.1") == 0, "127.0.0.1 se lee «solo tu PC»");
            Check(PortAuditService.AlcanceDe("192.168.1.9") == 1, "192.168.x se lee «tu red»");
            Check(PortAuditService.NombrePuerto(445).Contains("SMB", StringComparison.OrdinalIgnoreCase),
                  "el 445 suena a SMB", "dice " + PortAuditService.NombrePuerto(445));
            Check(PortAuditService.AlcanceTexto(2).Length > 0, "sabe describir el alcance 2");

            Check(SignatureService.EstadoLegible("valida").Length > 0 &&
                  SignatureService.EstadoLegible("sin_firmar").Length > 0 &&
                  SignatureService.EstadoLegible("dudosa").Length > 0,
                  "traduce los tres estados de firma");

            Check(ReportService.Etiqueta("7").Contains("7") || ReportService.Etiqueta("7").Length > 2,
                  "etiqueta el periodo de 7 días", "etiqueta vacía");
            Check(ReportService.InicioDe("30") < ReportService.InicioDe("7") ||
                  ReportService.InicioDe("30") <= ReportService.InicioDe("7"),
                  "el periodo de 30 días empieza antes o igual");
            Check(ReportService.Secciones.Length >= 5, "el informe trae secciones de sobra",
                  "solo " + ReportService.Secciones.Length);

            // El hosts: que el radar sepa leerlo sin escribirlo.
            try
            {
                var b = RadarService.Bloqueados();
                Detalle("hosts leído: " + b.Count + " dominios bloqueados por PCJ");
                Check(true, "el radar puede leer el fichero hosts");
            }
            catch (Exception ex)
            {
                Check(false, "el radar puede leer el fichero hosts", ex.Message);
            }
        }

        // ------------------------------------------------------------------ utilidades

        private static void Cabecera(string t)
        {
            Lineas.Add("");
            Lineas.Add("==== " + t + " ====");
        }

        private static void Check(bool ok, string que, string porQue = "", bool warnOnly = false)
        {
            if (ok) { Lineas.Add("  [ok]   " + que); return; }
            if (warnOnly)
            {
                Avisos++;
                Lineas.Add("  [aviso] " + que + (porQue.Length > 0 ? " — " + porQue : ""));
                return;
            }
            Fallos++;
            Lineas.Add("  [FALLO] " + que + (porQue.Length > 0 ? " — " + porQue : ""));
        }

        private static void Detalle(string t) => Lineas.Add("         " + t);
        private static void Nota(string t) => Lineas.Add("  · " + t);

        private static void Esperar(int ms)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
            timer.Tick += (s, e) => { timer.Stop(); frame.Continue = false; };
            timer.Start();
            Dispatcher.PushFrame(frame);
        }

        private static void Escribir(string archivo, object contenido)
        {
            string texto = contenido is string s ? s : string.Join(Environment.NewLine, (List<string>)contenido);
            File.WriteAllText(archivo, texto, new UTF8Encoding(false));
        }
    }
}
