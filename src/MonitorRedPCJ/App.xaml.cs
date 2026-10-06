using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using MonitorRedPCJ.Services;
using MonitorRedPCJ.Services.Tools;

namespace MonitorRedPCJ
{
    public partial class App : Application
    {
        private Mutex? _mutex;
        private int _shutdownPrepared;
        private int _shutdownCalled;
        private static int _isShuttingDown;

        // Id de tema escrito en la línea de mandos de los modos de comprobación, si lo trae.
        // Sirve para fotografiar la misma pantalla con los tres aspectos seguidos.
        private static string? _temaPedido;

        public static bool IsShuttingDown => Volatile.Read(ref _isShuttingDown) != 0;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            // El tema, antes que nada: los modos de comprobación de diseño abren ventanas y las
            // photographían, y han de salir con el aspecto que se les pidió. Se guarda el id
            // pedido y cada sitio que crea servicios lo aplica; en el arranque normal se usa el
            // que estuviera guardado en settings.json.
            _temaPedido = TemaEnArgs(e.Args);

            // Modos auxiliares invocados desde el proceso elevado (UAC)
            if (e.Args.Length >= 2 && e.Args[0] == "--fw-apply")
            {
                Environment.Exit(FirewallService.RunApplyMode(e.Args[1], e.Args.Length >= 3 ? e.Args[2] : ""));
                return;
            }
            if (e.Args.Length >= 2 && e.Args[0] == "--fw-toggle")
            {
                Environment.Exit(FirewallService.RunToggleMode(e.Args[1], e.Args.Length >= 3 ? e.Args[2] : ""));
                return;
            }
            if (e.Args.Length >= 2 && e.Args[0] == "--fw-strict")
            {
                Environment.Exit(FirewallService.RunStrictMode(e.Args[1], e.Args.Length >= 3 ? e.Args[2] : ""));
                return;
            }
            if (e.Args.Length >= 2 && e.Args[0] == "--fw-allow")
            {
                Environment.Exit(FirewallService.RunAllowMode(e.Args[1], e.Args.Length >= 3 ? e.Args[2] : ""));
                return;
            }
            // «Borrar todas las decisiones» desde la copia sin permisos: quita nuestras reglas de
            // app y vacía los marcadores, todo dentro de la copia elevada.
            if (e.Args.Length >= 2 && e.Args[0] == "--fw-forget")
            {
                Environment.Exit(FirewallService.RunForgetMode(e.Args[1], e.Args.Length >= 3 ? e.Args[2] : ""));
                return;
            }
            if (e.Args.Length >= 2 && e.Args[0] == "--fw-lock")
            {
                Environment.Exit(FirewallService.RunLockMode(e.Args[1], e.Args.Length >= 3 ? e.Args[2] : ""));
                return;
            }
            if (e.Args.Length >= 2 && e.Args[0] == "--fw-port")
            {
                Environment.Exit(FirewallService.RunPortMode(e.Args[1], e.Args.Length >= 3 ? e.Args[2] : ""));
                return;
            }
            // Reglas sueltas: las excepciones del sistema y la configuración adicional por
            // aplicación llegan aquí desde FirewallService.ApplySpecs.
            if (e.Args.Length >= 2 && e.Args[0] == "--fw-rules")
            {
                Environment.Exit(FirewallService.RunRulesMode(e.Args[1], e.Args.Length >= 3 ? e.Args[2] : ""));
                return;
            }
            // Comprobación de diseño: pinta el menú de la bandeja a un PNG y se va, sin abrir
            // ventana ni tocar el firewall. Solo sirve para revisar el aspecto.
            //   MonitorRedPCJ.exe --menu-preview archivo.png [visible] [siesta] [incognito]
            // El id de un tema suelto en cualquier posición hace que las tres paletas se
            // puedan fotografiar por separado; sin esto el menú saldría siempre con la
            // paleta que App.xaml trae puesta, y las tres fotografías saldrían iguales.
            if (e.Args.Length >= 2 && e.Args[0] == "--menu-preview")
            {
                try
                {
                    if (_temaPedido != null) ThemeService.Aplicar(_temaPedido, guardar: false);
                    Views.TrayMenuBody.RenderPreview(e.Args[1],
                        Flag(e.Args, 2), Flag(e.Args, 3), Flag(e.Args, 4));
                }
                catch (Exception ex)
                {
                    try { System.IO.File.WriteAllText(e.Args[1] + ".err", ex.ToString()); } catch { }
                }
                Environment.Exit(0);
                return;
            }
            // Y este abre el menú en pantalla un momento para poder fotografiarlo.
            //   MonitorRedPCJ.exe --menu-show milisegundos [auto|dentro] [x y]
            //   auto   = el menú sale lejos del puntero, el vigía debe cerrarlo
            //   dentro = el menú sale justo debajo del puntero (x,y), debe quedarse abierto
            if (e.Args.Length >= 2 && e.Args[0] == "--menu-show")
            {
                int ms = int.TryParse(e.Args[1], out var v) ? v : 2500;
                string modo = e.Args.Length >= 3 ? e.Args[2] : "";
                int px = e.Args.Length >= 5 && int.TryParse(e.Args[3], out var xx) ? xx : 0;
                int py = e.Args.Length >= 5 && int.TryParse(e.Args[4], out var yy) ? yy : 0;
                Views.TrayMenuBody.ShowPreview(ms, modo, px, py);
                return;
            }
            // Comprobación de diseño de la pestaña de Protección: monta la vista con los procesos
            // reales de este PC y la guarda como PNG. Solo lee — no cambia modo ni reglas—.
            //   MonitorRedPCJ.exe --protect-preview archivo.png [ms] [orden] [strict] [chip] [min] [ancho]
            //   orden: 1 salida permitidas arriba · 2 salida bloqueadas arriba
            //          3 entrada permitidas arriba · 4 entrada bloqueadas arriba · 5 detalle abierto
            //   chip:  1 en ejecución · 2 permitidas · 3 bloqueadas · 4 sin decidir
            //   min:   la ventana de la barra de minutos (-1 en vivo, 1, 5, 10, 15, 30, 60, 120,
            //          240 y 0 = todas). Sin argumento se queda con la ventana guardada.
            //   ancho: ancho de la ventana de foto (por defecto 1180), para revisar la cabecera
            //          estrecha sin tener que redimensionar el programa a mano.
            if (e.Args.Length >= 2 && e.Args[0] == "--protect-preview")
            {
                int ms = e.Args.Length >= 3 && int.TryParse(e.Args[2], out var v) ? v : 9000;
                int ord = e.Args.Length >= 4 && int.TryParse(e.Args[3], out var o) ? o : 0;
                bool strict = e.Args.Length >= 5 && Flag(e.Args, 4);
                int chip = e.Args.Length >= 6 && int.TryParse(e.Args[5], out var c) ? c : 0;
                int min = e.Args.Length >= 7 && int.TryParse(e.Args[6], out var mm) ? mm : int.MinValue;
                int ancho = e.Args.Length >= 8 && int.TryParse(e.Args[7], out var aw) ? aw : 1180;
                try
                {
                    PreparePreviewServices(strict);
                    Views.ProtectView.RenderPreview(e.Args[1], ms, ord, chip, min, ancho);
                }
                catch (Exception ex)
                {
                    // Sin este vuelco no hay forma de saber por qué no sale el PNG: el modo se
                    // lanza desde un guion y el mensaje en pantalla no se vería nunca.
                    try { System.IO.File.WriteAllText(e.Args[1] + ".err", ex.ToString()); } catch { }
                }
                Environment.Exit(0);
                return;
            }

            // Fotografía el desplegable de los modos con sus cuatro opciones, sacado del Popup de
            // la pestaña: es la forma de revisar el texto y el icono de «Borrar todas las
            // decisiones» sin abrir el programa. Solo lee.
            //   MonitorRedPCJ.exe --modos-preview modos.png [nocturna]
            if (e.Args.Length >= 2 && e.Args[0] == "--modos-preview")
            {
                try
                {
                    PreparePreviewServices(false);
                    Views.ProtectView.ModesPreview(e.Args[1]);
                }
                catch (Exception ex)
                {
                    try { System.IO.File.WriteAllText(e.Args[1] + ".err", ex.ToString()); } catch { }
                }
                Environment.Exit(0);
                return;
            }

            // Parte de la ventana de minutos y de los filtros de Protección: cuántas filas deja
            // cada posición de la barra y cada chip, con los procesos reales de este PC. Solo lee.
            //   MonitorRedPCJ.exe --protect-window-check parte.txt
            if (e.Args.Length >= 2 && e.Args[0] == "--protect-window-check")
            {
                try
                {
                    PreparePreviewServices(false);
                    Views.ProtectView.WindowCheck(e.Args[1]);
                }
                catch (Exception ex)
                {
                    try { System.IO.File.WriteAllText(e.Args[1] + ".err", ex.ToString()); } catch { }
                }
                Environment.Exit(0);
                return;
            }

            // A quién pregunta el modo «Preguntar para conectar»: lista los programas abiertos que
            // hoy entrarían en la cola de avisos y comprueba que salen de uno en uno. Solo lee: sin
            // ventana principal el aviso no tiene destinatario, y el modo comprobación trae
            // prohibida la escritura de reglas y del histórico.
            //   MonitorRedPCJ.exe --cola-check parte.txt [strict]
            if (e.Args.Length >= 2 && e.Args[0] == "--cola-check")
            {
                string destino = e.Args[1];
                try
                {
                    PreparePreviewServices(Flag(e.Args, 2));
                    Task.Run(() =>
                    {
                        try { System.IO.File.WriteAllText(destino, Traffic.ReporteDeCola()); }
                        catch (Exception ex2)
                        {
                            try { System.IO.File.WriteAllText(destino + ".err", ex2.ToString()); } catch { }
                        }
                        Environment.Exit(0);
                    });
                }
                catch (Exception ex)
                {
                    try { System.IO.File.WriteAllText(destino + ".err", ex.ToString()); } catch { }
                    Environment.Exit(0);
                }
                return;
            }

            // Medida del buscador de Protección: cuánto bloquea la interfaz escribir una
            // palabra letra a letra. Escribe un parte de texto y sale sin tocar nada.
            //   MonitorRedPCJ.exe --search-bench parte.txt [palabra]
            if (e.Args.Length >= 2 && e.Args[0] == "--search-bench")
            {
                try
                {
                    PreparePreviewServices(false);
                    Views.ProtectView.SearchBench(e.Args[1], e.Args.Length >= 3 ? e.Args[2] : "chrome");
                }
                catch (Exception ex)
                {
                    try { System.IO.File.WriteAllText(e.Args[1] + ".err", ex.ToString()); } catch { }
                }
                Environment.Exit(0);
                return;
            }

            // Comprobación de diseño de la caja de herramientas: pinta la ficha pedida a PNG.
            //   MonitorRedPCJ.exe --tools-preview archivo.png [id] [milisegundos]
            // Se lanza con la herramienta pedida encendida, para que la pantalla salga con
            // datos de verdad y no con el aviso de "apagada".
            if (e.Args.Length >= 2 && e.Args[0] == "--tools-preview")
            {
                string id = e.Args.Length >= 3 ? e.Args[2] : "";
                int ms = e.Args.Length >= 4 && int.TryParse(e.Args[3], out var v) ? v : 3500;
                try
                {
                    PreparePreviewServices(false);
                    if (!string.IsNullOrEmpty(id))
                    {
                        Settings.Current.Herramientas ??= new System.Collections.Generic.Dictionary<string, bool>();
                        Settings.Current.Herramientas[id] = true;
                    }
                    Tools.Arrancar();
                    Views.ToolsWindow.RenderPreview(e.Args[1], id, ms);
                }
                catch (Exception ex)
                {
                    // Si la foto falla, que quede el motivo en un fichero junto al PNG: estos
                    // modos se lanzan desde un guion y el error en pantalla no se vería.
                    try { System.IO.File.WriteAllText(e.Args[1] + ".err", ex.ToString()); } catch { }
                }
                Environment.Exit(0);
                return;
            }

            // Comprobación de diseño de la ventana principal: barra de pestañas y herramienta
            // embebida en su hueco. Solo lee.
            //   MonitorRedPCJ.exe --main-preview archivo.png [traffic|protect|hardware|extra] [herramienta] [ms]
            if (e.Args.Length >= 2 && e.Args[0] == "--main-preview")
            {
                string pest = e.Args.Length >= 3 ? e.Args[2] : "traffic";
                string herra = e.Args.Length >= 4 ? e.Args[3] : "";
                int ms = e.Args.Length >= 5 && int.TryParse(e.Args[4], out var v2) ? v2 : 5000;
                try
                {
                    PreparePreviewServices(false);
                    if (!string.IsNullOrEmpty(herra))
                    {
                        Settings.Current.Herramientas ??= new System.Collections.Generic.Dictionary<string, bool>();
                        Settings.Current.Herramientas[herra] = true;
                    }
                    Tools.Arrancar();
                    MonitorRedPCJ.MainWindow.RenderPreview(e.Args[1], pest, herra, ms);
                }
                catch (Exception ex)
                {
                    try { System.IO.File.WriteAllText(e.Args[1] + ".err", ex.ToString()); } catch { }
                }
                Environment.Exit(0);
                return;
            }

            // Comprobación de diseño de Configuración: pinta la pestaña pedida a PNG sin abrir
            // nada de verdad. Solo lee.
            //   MonitorRedPCJ.exe --settings-preview archivo.png
            //        [0 general · 1 protección · 2 excepciones · 3 escáner · 4 herramientas ·
            //         5 temas · 6 acerca de]
            if (e.Args.Length >= 2 && e.Args[0] == "--settings-preview")
            {
                int ix = e.Args.Length >= 3 && int.TryParse(e.Args[2], out var i) ? i : 0;
                try
                {
                    PreparePreviewServices(false);
                    Views.SettingsWindow.RenderPreview(e.Args[1], ix);
                }
                catch (Exception ex)
                {
                    try { System.IO.File.WriteAllText(e.Args[1] + ".err", ex.ToString()); } catch { }
                }
                Environment.Exit(0);
                return;
            }

            // Comprobación de diseño de la ventana de «Configuración adicional»: pinta la ventana
            // con el modo pedido a PNG. No escribe reglas ni abre UAC: solo lee.
            //   MonitorRedPCJ.exe --ajuste-preview archivo.png [idDeModo]
            //   ids: sinConfigurar · bloquearTodo · soloPuertos · salidaTcpUdp ·
            //        sinRestriccionTcpUdp · sinRestricciones
            if (e.Args.Length >= 2 && e.Args[0] == "--ajuste-preview")
            {
                string modo = e.Args.Length >= 3 ? e.Args[2] : "";
                try
                {
                    PreparePreviewServices(false);
                    Views.AjusteAppWindow.RenderPreview(e.Args[1], modo);
                }
                catch (Exception ex)
                {
                    try { System.IO.File.WriteAllText(e.Args[1] + ".err", ex.ToString()); } catch { }
                }
                Environment.Exit(0);
                return;
            }

            // Comprobación de la lógica de la configuración adicional: las reglas que saca cada
            // modo y el parser de puertos. Todo es cálculo puro contra el catálogo; no se escribe
            // nada en el firewall.
            //   MonitorRedPCJ.exe --ajuste-check informe.txt
            if (e.Args.Length >= 2 && e.Args[0] == "--ajuste-check")
            {
                try
                {
                    PreparePreviewServices(false);
                    AppAjustes.Check.Ejecutar(e.Args[1]);
                }
                catch (Exception ex)
                {
                    try { System.IO.File.WriteAllText(e.Args[1] + ".err", ex.ToString()); } catch { }
                }
                Environment.Exit(0);
                return;
            }

            // Comprobación del catálogo de excepciones del sistema: que las quince existan, cada
            // una escriba permisos y no cortes, y que apagarlas las borre todas. Tampoco escribe.
            //   MonitorRedPCJ.exe --excepciones-check informe.txt
            if (e.Args.Length >= 2 && e.Args[0] == "--excepciones-check")
            {
                try
                {
                    PreparePreviewServices(false);
                    AppExcepciones.Check.Ejecutar(e.Args[1]);
                }
                catch (Exception ex)
                {
                    try { System.IO.File.WriteAllText(e.Args[1] + ".err", ex.ToString()); } catch { }
                }
                Environment.Exit(0);
                return;
            }

            // Comprobación de la lista de malware: análisis de la descarga, tope de entradas y
            // idas y vueltas del bloque del hosts sobre un texto de ejemplo. No baja nada de
            // internet y no escribe el hosts de verdad.
            //   MonitorRedPCJ.exe --malware-check informe.txt
            if (e.Args.Length >= 2 && e.Args[0] == "--malware-check")
            {
                try
                {
                    PreparePreviewServices(false);
                    MalwareService.Check.Ejecutar(e.Args[1]);
                }
                catch (Exception ex)
                {
                    try { System.IO.File.WriteAllText(e.Args[1] + ".err", ex.ToString()); } catch { }
                }
                Environment.Exit(0);
                return;
            }

            // La misma comprobación, pero bajando la lista de verdad: escribe solo la caché en la
            // carpeta de datos de PCJ. El fichero hosts no se toca por ninguna vía de comprobación;
            // ese lo escribe el usuario cuando suelta el interruptor de Ajustes.
            //   MonitorRedPCJ.exe --malware-fetch informe.txt [dirección]
            // Con dirección se prueba una fuente de muestra (por ejemplo el servidor de casa en
            // 127.0.0.1), que es la única forma de ver los avances de la descarga por escrito sin
            // depender de que la lista pública esté despierta.
            if (e.Args.Length >= 2 && e.Args[0] == "--malware-fetch")
            {
                string url = e.Args.Length >= 3 && !string.IsNullOrWhiteSpace(e.Args[2])
                    ? e.Args[2] : App.Settings.Current.MalwareListaUrl;
                var sb = new System.Text.StringBuilder();
                var pluma = new ProgresoEnHilo();
                MalwareService.DescargaResultado r = new();
                bool ok = false;
                try
                {
                    PreparePreviewServices(false);
                    r = MalwareService.DescargarAsync(url, pluma,
                            System.Threading.CancellationToken.None)
                        .GetAwaiter().GetResult();
                    ok = r.Ok;
                }
                catch (Exception ex) { r.Aviso = ex.ToString(); }
                sb.AppendLine("Descarga de la lista de malware — " +
                              DateTime.Now.ToString("dd/MM/yyyy HH:mm"));
                sb.AppendLine("fuente: " + url);
                sb.AppendLine("resultado: " + (ok ? r.Entradas + " dominios" : "falló") +
                              (r.Aviso.Length > 0 ? " — " + r.Aviso : ""));
                sb.AppendLine("bytes: " + r.Bytes + " · líneas: " + r.Lineas +
                              " · descartadas: " + r.Descartadas +
                              " · tiempo: " + r.Tiempo.TotalSeconds.ToString("0.00", MalwareService.Es) + " s");
                sb.AppendLine();
                sb.AppendLine("FASES CONTADAS");
                foreach (var l in pluma.Lineas) sb.AppendLine("  " + l);
                sb.AppendLine();
                sb.AppendLine("caché: " + MalwareService.ArchivoCache +
                              (MalwareService.HayCache() ? " · " + MalwareService.InfoCache()
                                                         : " (todavía no existe)"));
                sb.AppendLine();
                try
                {
                    if (MalwareService.HayCache())
                        foreach (var l in System.IO.File.ReadAllLines(MalwareService.ArchivoCache).Take(15))
                            sb.AppendLine(l);
                }
                catch { }
                try { System.IO.File.WriteAllText(e.Args[1], sb.ToString()); } catch { }
                Environment.Exit(0);
                return;
            }

            // Foto del panel de la lista de malware, con los números puestos a mano: sirve para
            // mirar el diseño de los cuatro estados sin bajar nada y sin escribir nada.
            //   MonitorRedPCJ.exe --malware-preview archivo.png [0 en reposo · 1 bajando ·
            //        2 terminado · 3 caído · 4 bajado a medias]
            if (e.Args.Length >= 2 && e.Args[0] == "--malware-preview")
            {
                int caso = e.Args.Length >= 3 && int.TryParse(e.Args[2], out var i) ? i : 0;
                try
                {
                    PreparePreviewServices(false);
                    Views.MalwareWindow.RenderPreview(e.Args[1], caso);
                }
                catch (Exception ex)
                {
                    try { System.IO.File.WriteAllText(e.Args[1] + ".err", ex.ToString()); } catch { }
                }
                Environment.Exit(0);
                return;
            }

            // Comprobación de funcionamiento de las doce herramientas: catálogo, servicios,
            // pantallas, apagado y lógica pura. Escribe un informe de texto y sale.
            //   MonitorRedPCJ.exe --tools-check informe.txt
            if (e.Args.Length >= 2 && e.Args[0] == "--tools-check")
            {
                try
                {
                    PreparePreviewServices(false);
                    Tools.Arrancar();
                    Views.Tools.ToolCheck.Ejecutar(e.Args[1]);
                }
                catch (Exception ex)
                {
                    try { System.IO.File.WriteAllText(e.Args[1] + ".err", ex.ToString()); } catch { }
                }
                Environment.Exit(0);
                return;
            }

            // Comprobación del motor de temas: pasa las tres paletas y escribe un informe de
            // claves (cuántas tiene cada una, cuáles le faltan a alguna). No abre ventana ni
            // toca el firewall; solo lee XAML compilado.
            //   MonitorRedPCJ.exe --theme-check informe.txt
            if (e.Args.Length >= 2 && e.Args[0] == "--theme-check")
            {
                try
                {
                    System.IO.File.WriteAllText(e.Args[1], ThemeService.Comprobar());
                }
                catch (Exception ex)
                {
                    try { System.IO.File.WriteAllText(e.Args[1] + ".err", ex.ToString()); } catch { }
                }
                Environment.Exit(0);
                return;
            }

            // Arranque siempre como administrador: si esta copia no tiene permisos y no viene ya
            // de la tarea elevada (--via-task), dispara la tarea —que sí arranca elevada y sin
            // volver a preguntar— y se cierra. Si la tarea no existe (instalación antigua), sigue
            // su curso normal sin permisos, como siempre.
            bool viaTask = e.Args.Any(a => a == "--via-task");
            if (!FirewallService.IsAdmin && !viaTask && ElevationTask.TaskExists() &&
                !OtraCopiaAbierta() && ElevationTask.LaunchElevated() && EsperaCopiaNueva())
            {
                Shutdown();
                return;
            }

            // Con permisos ya puestos, y si por lo que sea falta la tarea, la creamos ahora para
            // que los próximos arranques sean silenciosos. Se hace detrás, sin frenar la ventana.
            if (FirewallService.IsAdmin)
                Task.Run(() => { try { ElevationTask.EnsureRegistered(); } catch { } });

            // Instancia única
            _mutex = new Mutex(true, "MonitorRedPCJ_SingleInstance", out bool isNew);
            if (!isNew)
            {
                var existing = System.Diagnostics.Process.GetProcesses()
                    .FirstOrDefault(p => p.Id != System.Diagnostics.Process.GetCurrentProcess().Id &&
                                         p.ProcessName == "MonitorRedPCJ");

                // Si esta copia sí tiene permisos y la abierta no, ofrecer el relevo:
                // sin eso, "ejecutar como administrador" no hacía nada y el firewall seguía bloqueado.
                bool weAreAdmin = FirewallService.IsAdmin;
                bool theyAreAdmin = existing != null && Native.Elevation.IsProcessElevated(existing);

                if (weAreAdmin && existing != null && !theyAreAdmin)
                {
                    var answer = MessageBox.Show(
                        "Ya hay un Monitor de Red PCJ abierto sin permisos de administrador, y por eso no puede " +
                        "aplicar reglas del firewall.\n\n¿Cerrarlo y dejar abierta esta copia con permisos?",
                        "Monitor de Red PCJ", MessageBoxButton.YesNo, MessageBoxImage.Question);

                    if (answer == MessageBoxResult.Yes)
                    {
                        try { existing.Kill(); existing.WaitForExit(8000); } catch { }
                        try { _mutex.Dispose(); } catch { }
                        _mutex = new Mutex(true, "MonitorRedPCJ_SingleInstance", out _);
                    }
                    else
                    {
                        Shutdown();
                        return;
                    }
                }
                else
                {
                    MessageBox.Show("Monitor de Red PCJ ya está en ejecución (revisa la bandeja).",
                        "Monitor de Red PCJ", MessageBoxButton.OK, MessageBoxImage.Information);
                    Shutdown();
                    return;
                }
            }

            Paths.Ensure();

            Settings = new SettingsService();
            // El aspecto guardado, puesto antes de crear la primera ventana: si se hiciera
            // después, la ventana nacería con Aurora y se repintaría un instante luego, y eso
            // se nota como un parpadeo en cada arranque.
            ThemeService.Aplicar(_temaPedido ?? Settings.Current.Tema, guardar: false);
            // Antes de crear nada que guarde: si la última sesión quedó en incógnito, desde
            // el primer evento no debe escribirse histórico en disco.
            Recording.Set(!Settings.Current.IncognitoMode);
            Events = new EventStore();
            Firewall = new FirewallService();
            Traffic = new TrafficService(Settings, Events, Firewall);
            Scanner = new NetworkScannerService(Events);
            Hardware = new HardwareService();
            Tools = new ToolHost();

            // Si esta sesión arranca ya en incógnito (la anterior cerró así), la foto de lo que
            // había en disco se toma ahora: es lo que habrá que recuperar al desactivarlo.
            if (Settings.Current.IncognitoMode)
            {
                Events.ApplyIncognito(true);
                Scanner.ApplyIncognito(true);
            }

            Traffic.Start();

            // Con el bloqueo total puesto, PCJ es un programa más: si la regla que le da salida se
            // quedó apuntando a otra copia del ejecutable, ni siquiera puede descargarse su propia
            // lista de malware. Se arregla solo, sin preguntar, y se cuenta en el registro para que
            // no parezca que el monitor se concede permisos a escondidas.
            _ = Task.Run(() =>
            {
                try
                {
                    string vieja = Firewall.ReparaReglaPropia();
                    if (vieja.Length == 0) return;
                    Events.Add(MonitorRedPCJ.Models.EventKind.RuleChanged,
                        "Monitor de Red PCJ actualizó su propio permiso de salida: la regla apuntaba " +
                        "a otra copia del programa (" + vieja + ") y ahora apunta a la que está en " +
                        "marcha, que es la que necesitaba bajar la lista.",
                        Environment.ProcessPath ?? "", important: false);
                }
                catch { }
            });

            // Las diez herramientas de la caja: solo se arranca la que el usuario dejó encendida.
            // Con todas apagadas no hay ni un hilo ni una lectura de red añadida por PCJ.
            Tools.Arrancar();

            var win = new MainWindow();
            MainWindow = win;
            win.Show();
            Tray = new TrayService(win, this);
            SessionEnding += (s, e) => CerrarPorCierreDeSesion();
        }

        // ¿Hay ya otra copia del monitor abierta? Si la hay, no se dispara la tarea: quien
        // tiene que responder es el mutex de siempre, no dos ventanas peleándose.
        private static bool OtraCopiaAbierta()
        {
            int myPid = System.Diagnostics.Process.GetCurrentProcess().Id;
            var prs = System.Diagnostics.Process.GetProcessesByName("MonitorRedPCJ");
            try { return prs.Any(p => p.Id != myPid); }
            finally { foreach (var p in prs) { try { p.Dispose(); } catch { } } }
        }

        // Windows a veces acepta el "schtasks /Run" y luego no arranca nada (permisos, tarea
        // deshabilitada). Se esperan hasta ~6 s: si no aparece una copia nueva, esta NO se
        // cierra, para que el monitor no desaparezca del todo.
        private static bool EsperaCopiaNueva()
        {
            int myPid = System.Diagnostics.Process.GetCurrentProcess().Id;
            for (int i = 0; i < 12; i++)
            {
                Thread.Sleep(500);
                var prs = System.Diagnostics.Process.GetProcessesByName("MonitorRedPCJ");
                bool otra = prs.Any(p => { try { return p.Id != myPid && !p.HasExited; } catch { return false; } });
                foreach (var p in prs) { try { p.Dispose(); } catch { } }
                if (otra) return true;
            }
            return false;
        }

        // Bandera del modo de comprobación --menu-preview: "1", "si" o "true".
        private static bool Flag(string[] args, int idx)
            => args.Length > idx && (args[idx] == "1" || args[idx] == "si" || args[idx] == "true");

        // ¿Alguno de los argumentos dice el nombre de un tema? Los modos de comprobación de
        // diseño aceptan el id suelto en cualquier posición
        //   MonitorRedPCJ.exe --protect-preview p.png 9000 0 nocturna
        // en vez de añadir un interruptor más a cada uno y respetar un orden nuevo: los nombres
        // de tema no se parecen a ningún otro argumento, así que no hay confusión posible.
        private static string? TemaEnArgs(string[] args)
        {
            foreach (var a in args)
                if (ThemeService.Buscar(a) != null) return a;
            return null;
        }

        // Servicios que necesita la comprobación de diseño. El sondeo se arranca para que la
        // lista salga con los procesos reales de este PC, pero con la grabación apagada: así
        // ninguna escritura de este modo se mezcla con el histórico de la copia instalada.
        // (Se lanza además con APPDATA apuntando a una carpeta propia, por si acaso.)
        // El Progress<T> de costumbre manda los avisos al hilo que lo creó, y en un modo de
        // comprobación por línea de comandos ese hilo está bloqueado esperando el resultado: ningún
        // aviso llegaría. Esta versión los apunta en el momento, en el hilo de la descarga, que es
        // lo que hace falta para dejar las fases por escrito en el informe.
        sealed class ProgresoEnHilo : System.IProgress<MalwareService.DescargaProgreso>
        {
            readonly System.Collections.Generic.List<string> _lineas = new();
            readonly object _cejo = new();

            public void Report(MalwareService.DescargaProgreso p)
            {
                lock (_cejo)
                    _lineas.Add(p.Fase + " · " + p.Bytes + "/" + p.TotalBytes + " B · " +
                                p.Lineas + " líneas · " + p.Dominios + " dominios · " + p.Texto);
            }

            public System.Collections.Generic.IReadOnlyList<string> Lineas
            {
                get { lock (_cejo) return new System.Collections.Generic.List<string>(_lineas); }
            }
        }

        private static void PreparePreviewServices(bool strict)
        {
            Paths.Ensure();
            Settings = new SettingsService();
            // El tema pedido en la línea de mandos, o si no trae ninguno, el que el programa
            // tuviera guardado: así las fotografías salen con el aspecto que se quiere revisar.
            ThemeService.Aplicar(_temaPedido ?? Settings.Current.Tema, guardar: false);
            if (strict) Settings.Current.StrictMode = true;   // solo en memoria: no se guarda
            Recording.Set(false);
            FirewallService.EscrituraProhibida = true;   // una foto no escribe reglas
            Events = new EventStore();
            Firewall = new FirewallService();
            Traffic = new TrafficService(Settings, Events, Firewall);
            Scanner = new NetworkScannerService(Events);
            Hardware = new HardwareService();
            Tools = new ToolHost();
            Traffic.Start();
        }

        public static SettingsService Settings = null!;
        public static EventStore Events = null!;
        public static FirewallService Firewall = null!;
        public static TrafficService Traffic = null!;
        public static NetworkScannerService Scanner = null!;
        public static HardwareService Hardware = null!;
        public static ToolHost Tools = null!;
        public static TrayService? Tray;

        // Al apagar o cerrar sesión Windows hay que irse de verdad: con la X escondida en la
        // bandeja, el cierre de sesión se quedaría esperando a una ventana que no se cierra.
        private void CerrarPorCierreDeSesion()
        {
            if (MainWindow is MainWindow w)
            {
                w.RequestExit();
                try { w.Close(); } catch { }
            }
            try { ShutdownCompletely(); } catch { }
        }

        // Encendido y apagado del modo incógnito en un solo sitio: los tres sitios que guardan
        // histórico (tráfico por programa, registro de eventos y dispositivos) hacen su foto al
        // entrar y la recuperan al salir. Las reglas del firewall no se tocan: cortar o dejar
        // salir una app es una decisión, no un dato de uso.
        public static void SetIncognito(bool on)
        {
            Events.ApplyIncognito(on);
            Scanner.ApplyIncognito(on);
            // El último: es el que abre o cierra la grabación y avisa a la interfaz.
            Traffic.ApplyIncognito(on);
        }

        public void PrepareForShutdown()
        {
            if (Interlocked.Exchange(ref _shutdownPrepared, 1) != 0) return;

            Interlocked.Exchange(ref _isShuttingDown, 1);
            try { Traffic?.Stop(); } catch { }
            try { Tools?.Parar(); } catch { }
            try
            {
                Tray?.Dispose();
                Tray = null;
            }
            catch { }

            foreach (Window window in Windows.Cast<Window>().Where(w => w != MainWindow).ToArray())
            {
                try { window.Close(); } catch { }
            }
        }

        public void ShutdownCompletely()
        {
            PrepareForShutdown();
            if (Interlocked.Exchange(ref _shutdownCalled, 1) == 0)
                Shutdown();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            PrepareForShutdown();
            try { _mutex?.ReleaseMutex(); } catch { }
            try { _mutex?.Dispose(); } catch { }
            base.OnExit(e);
        }
    }
}
