using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MonitorRedPCJ.Services;
using MonitorRedPCJ.Services.Tools;

namespace MonitorRedPCJ.Views
{
    public partial class SettingsWindow : Window
    {
        // La rejilla de «Pestaña en la ventana principal» se pinta con estas fichas: es lo único
        // que necesita la plantilla de la XAML (mosaico, glifo, nombre y lema).
        public class OpcionHerramienta
        {
            public string Id { get; set; } = "";
            public string Nombre { get; set; } = "";
            public string Lema { get; set; } = "";
            public string Glifo { get; set; } = "";
            public Brush Mosaico { get; set; } = Brushes.Transparent;
            public Brush Tinta { get; set; } = Brushes.Black;
        }

        // Lo que se aplicará al pulsar Aceptar. Se guarda aparte porque esta ventana tiene
        // Cancelar: elegir una pestaña no debe tener efecto hasta que se confirme.
        private string _herramientaElegida = "";
        private bool _listaLista;

        public SettingsWindow()
        {
            InitializeComponent();
            var s = App.Settings.Current;
            ChkStartup.IsChecked = StartupHelper.IsEnabled();
            ChkAlerts.IsChecked = s.AlertsEnabled;
            ChkIncognito.IsChecked = s.IncognitoMode;
            TxtRetention.Text = s.HistoryRetentionDays.ToString();
            TxtProtectState.Text = "Bloqueo total: " + (s.StrictMode ? "activo" : "desactivado") +
                                   " · excepciones del sistema: " +
                                   AppExcepciones.Resumen(s.ExcepcionesSistema);
            ChkAutoScan.IsChecked = s.AutoScanNetwork;
            TxtScanInterval.Text = s.ScanIntervalMinutes.ToString();

            MontarHerramientas();
            MontarTemas();
            MontarExcepciones();

            // La ruta real, con la variable ya deshecha: quien mira esta pantalla suele
            // estar buscando dónde están sus datos para copiarlos o borrarlos.
            TxtRutaDatos.Text = MonitorRedPCJ.Services.Paths.Root;

            // La versión se lee del ensamblado: ponerla a mano en la XAML se quedaba desactualizada.
            TxtAboutVersion.Text = "Monitor de Red PCJ v" +
                (System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?");

            ThemeService.Cambiado += OnTemaCambia;
            Unloaded += (snd, ev) => ThemeService.Cambiado -= OnTemaCambia;
        }

        // ---------- Sección «Temas» ----------
        //
        // Un tema se aplica con el clic, sin esperar a Aceptar: es la única manera de ver
        // el cambio de verdad, y deshacerlo cuesta otro clic. Por eso la tarjeta en uso se
        // remarca y el pie del menú dice cuál está puesto.

        private void MontarTemas()
        {
            ThemeHost.Children.Clear();
            string puesto = ThemeService.Actual;
            foreach (var tema in ThemeService.Lista)
                ThemeHost.Children.Add(TarjetaTema(tema, tema.Id == puesto));
            TxtTemaPie.Text = "Tema: " + ThemeService.Corriente.Nombre;
            BtnTemaBase.IsEnabled = !string.Equals(puesto, ThemeService.Predeterminado,
                                                   StringComparison.OrdinalIgnoreCase);
            TxtTemaHint.Text = BtnTemaBase.IsEnabled
                ? "Aurora es el tema original de PCJ: si pruebas otro y no te convence, con este botón vuelves a él."
                : "Ya estás en Aurora, el tema con el que PCJ se ha visto siempre.";
        }

        // La maqueta diminuta: una ventanita con su banda, tres filas y su barra de
        // tráfico, pintada toda con los pinceles de ESE tema. Se construye a mano porque
        // los colores hay que leerlos de una paleta que no está montada en la aplicación.
        private Border TarjetaTema(ThemeInfo tema, bool enUso)
        {
            Brush Tinta(string clave) => ThemeService.Tinta(tema.Id, clave) ?? Brushes.Transparent;
            CornerRadius Radio(string clave, double porSi)
                => TryFindResource(clave) is CornerRadius r ? r : new CornerRadius(porSi);

            var card = new Border
            {
                Style = (Style)Resources["ThemeCard"],
                Tag = tema.Id,
            };
            // El borde de "en uso" se pone con el pincel del propio tema para que la
            // tarjeta elegida se vea elegida también en Nocturna.
            if (enUso) card.BorderBrush = Tinta("B.Accent");

            var cuerpo = new StackPanel();

            // ---- la maqueta ----
            var mock = new Border
            {
                Height = 108,
                CornerRadius = Radio("R.Tile", 8),
                Background = Tinta("B.Bg"),
                Margin = new Thickness(1),
                ClipToBounds = true,
            };
            var mockGrid = new Grid();
            mockGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            mockGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            mockGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // La banda de arriba, con su tile de acento y el título.
            var banda = new Border
            {
                Height = 24,
                Background = Tinta("B.Banner"),
                Margin = new Thickness(7, 7, 7, 0),
                CornerRadius = new CornerRadius(6),
            };
            var bandaCont = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 6, 0) };
            bandaCont.Children.Add(new Border
            {
                Width = 13, Height = 13, CornerRadius = new CornerRadius(4),
                Background = Tinta("B.AuroraFill"), VerticalAlignment = VerticalAlignment.Center,
            });
            bandaCont.Children.Add(new Border
            {
                Width = 46, Height = 5, CornerRadius = new CornerRadius(3),
                Background = Tinta("B.BannerText"), Opacity = 0.85,
                Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            });
            bandaCont.Children.Add(new Border
            {
                Width = 22, Height = 9, CornerRadius = new CornerRadius(4),
                Background = Tinta("B.BannerChip"),
                Margin = new Thickness(0, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
            });
            banda.Child = bandaCont;
            Grid.SetRow(banda, 0);
            mockGrid.Children.Add(banda);

            // Tres filas: raíl de estado, icono, línea de texto y pastilla.
            var filas = new StackPanel { Margin = new Thickness(7, 6, 7, 6) };
            var railes = new[] { "B.Green", "B.Coral", "B.BarIdle" };
            var pastillas = new[] { "B.ChipOkBg", "B.ChipBadBg", "B.ChipNeutralBg" };
            for (int i = 0; i < 3; i++)
            {
                var fila = new Grid { Margin = new Thickness(0, 0, 0, i == 2 ? 0 : 5) };
                fila.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                fila.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                fila.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                fila.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var rail = new Border
                {
                    Width = 2.5, Height = 11, CornerRadius = new CornerRadius(2),
                    Background = Tinta(railes[i]), Margin = new Thickness(0, 0, 4, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(rail, 0); fila.Children.Add(rail);

                var icono = new Border
                {
                    Width = 11, Height = 11, CornerRadius = new CornerRadius(3),
                    Background = Tinta("B.Field"), BorderBrush = Tinta("B.Line"), BorderThickness = new Thickness(1),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(icono, 1); fila.Children.Add(icono);

                var linea = new Border
                {
                    Height = 5, CornerRadius = new CornerRadius(3), Background = Tinta("B.Text"),
                    Opacity = 0.30 + 0.12 * (2 - i), Margin = new Thickness(7, 0, 7, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(linea, 2); fila.Children.Add(linea);

                var pastilla = new Border
                {
                    Width = 26, Height = 10, CornerRadius = new CornerRadius(5),
                    Background = Tinta(pastillas[i]), VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(pastilla, 3); fila.Children.Add(pastilla);

                filas.Children.Add(fila);
            }
            Grid.SetRow(filas, 1);
            mockGrid.Children.Add(filas);

            // Las seis muestras: fondo, panel, texto y los tres acentos, de frente, para
            // que se puedan comparar los temas sin tener que adivinar de qué clave sale cada color.
            var muestras = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(7, 0, 7, 7),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            foreach (var clave in ThemeInfo.Muestras)
                muestras.Children.Add(new Border
                {
                    Width = 15, Height = 8, CornerRadius = new CornerRadius(2),
                    Background = Tinta(clave), BorderBrush = Tinta("B.Edge"),
                    BorderThickness = new Thickness(0.8), Margin = new Thickness(0, 0, 4, 0),
                });
            Grid.SetRow(muestras, 2);
            mockGrid.Children.Add(muestras);

            mock.Child = mockGrid;
            cuerpo.Children.Add(mock);

            // ---- el pie: nombre, lema y qué haría un clic ----
            var pie = new StackPanel { Margin = new Thickness(11, 10, 11, 11) };
            var nombre = new StackPanel { Orientation = Orientation.Horizontal };
            nombre.Children.Add(new TextBlock
            {
                Text = tema.Nombre,
                Style = (Style)Resources["ThemeName"],
                VerticalAlignment = VerticalAlignment.Center,
            });
            nombre.Children.Add(new TextBlock
            {
                Text = tema.Icono,
                FontFamily = (FontFamily)TintaFont(),
                FontSize = 13,
                Margin = new Thickness(8, 1, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Tinta("B.TextDim"),
            });
            pie.Children.Add(nombre);

            pie.Children.Add(new TextBlock
            {
                Text = tema.Lema,
                Style = (Style)Resources["ThemeLema"],
            });

            var pieChip = new Border
            {
                CornerRadius = Radio("R.Chip", 6),
                Padding = new Thickness(9, 3, 9, 3),
                Background = enUso ? Tinta("B.ChipOkBg") : Tinta("B.ChipNeutralBg"),
                Margin = new Thickness(0, 9, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            pieChip.Child = new TextBlock
            {
                Text = enUso ? "En uso ahora" : "Pulsar para ponerlo",
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = enUso ? Tinta("B.ChipOkFg") : Tinta("B.ChipNeutralFg"),
            };
            pie.Children.Add(pieChip);
            cuerpo.Children.Add(pie);

            card.Child = cuerpo;
            card.MouseLeftButtonUp += (snd, ev) => OnElegirTema(tema.Id);
            return card;
        }

        // La fuente de iconos del tema en uso: las tarjetas muestran el glifo con la
        // tipografía que se está viendo, no la del tema retratado, para que la letra no
        // cambie de familia en mitad de la pantalla.
        private static FontFamily TintaFont()
            => Application.Current.TryFindResource("F.Icon") as FontFamily ?? new FontFamily("Segoe Fluent Icons");

        private void OnElegirTema(string id)
        {
            // Se aplica y se remonta la rejilla: la tarjeta que queda marcada es la que
            // está puesta, y el pie del menú dice su nombre. No hay botón Aplicar aparte.
            ThemeService.Aplicar(id);
            MontarTemas();
        }

        private void OnTemaBase(object sender, RoutedEventArgs e) => OnElegirTema(ThemeService.Predeterminado);

        // Al cambiar el tema desde fuera de esta ventana (el menú de la bandeja, por
        // ejemplo), la sección de Temas tiene que remarcar otra tarjeta.
        private void OnTemaCambia(string id)
        {
            if (!IsLoaded) return;
            try { MontarTemas(); } catch { }
        }

        private void OnIrAProteccion(object sender, RoutedEventArgs e)
        {
            // Se cierra Ajustes y se salta a la pestaña: dejar las dos ventanas abiertas una
            // encima de otra no sirve para mirar nada.
            var m = this.Owner as MainWindow ?? Application.Current.MainWindow as MainWindow;
            Close();
            m?.IrAProteccion("");
        }


        // ---------- Sección «Excepciones del sistema» ----------
        //
        // El catálogo vive en AppExcepciones (nombres, explicaciones y reglas), así que aquí no
        // se escribe ni una frase de más: esta pantalla solo lee la ficha y monta un interruptor
        // por entrada. Se montan a mano en vez de con un ItemsControl porque cada fila lleva su
        // estado dicho con palabras y un botón que abre la explicación, y eso en una plantilla no
        // se expresa sin pelearse con el enlace de datos.
        //
        // El interruptor aplica al soltarlo, sin esperar a Aceptar. No sale UAC porque PCJ ya
        // arranca elevado, y esperar al botón de cerrar dejaría al usuario con Windows sin poder
        // actualizarse sin saberlo.

        private bool _excLista;          // los interruptores ya están puestos: un cambio es un clic real
        private bool _excOcupada;        // hay una escritura en marcha en Windows Firewall
        // La palabra del estado de cada ficha, por id, para poder reescribirla al mover la palanca:
        // la fila se monta una vez y el interruptor se mueve muchas.
        private readonly Dictionary<string, TextBlock> _excEstados = new();
        // Los botones de «ver la explicación», en el orden de la lista, para poder abrirlos desde
        // el modo de foto: una captura no puede pinchar con el ratón, y sin esto el estado
        // desplegado de una ficha no se ha visto nunca antes de instalar.
        private readonly List<Action> _excAbrir = new();

        private void MontarExcepciones()
        {
            var s = App.Settings.Current;
            var estado = s.ExcepcionesSistema;
            ExcRecomendadas.Children.Clear();
            ExcOpcionales.Children.Clear();
            _excEstados.Clear();
            _excAbrir.Clear();
            foreach (var e in AppExcepciones.PorGrupo("recomendada"))
                ExcRecomendadas.Children.Add(FilaExcepcion(e, Encendida(estado, e.Id)));
            foreach (var e in AppExcepciones.PorGrupo("opcional"))
                ExcOpcionales.Children.Add(FilaExcepcion(e, Encendida(estado, e.Id)));
            TxtExcPie.Text = "Ahora mismo: " + AppExcepciones.Resumen(estado) + ".";

            // Los dos interruptores sueltos se ponen aquí, antes de dar por montada la sección:
            // si se diera por montada antes, esta asignación dispararía su manejador y abrir Ajustes
            // escribiría reglas y el fichero hosts sin que nadie lo pidiera.
            ChkRedLocal.IsChecked = s.TraficoLocalLibre;
            ChkMalware.IsChecked = s.BloqueoMalwareActivo;
            EstadosSueltos();
            RefrescarMalware();
            _excLista = true;
        }

        // Las dos palancas sueltas llevan las mismas palabras que las fichas del catálogo. La de la
        // red local va igual que las excepciones (encendida = dejar pasar); la del malware al revés,
        // que encendida es la que está cortando y es lo que se quiere.
        void EstadosSueltos()
        {
            PonerEstado(TxtEstadoRedLocal, cortando: ChkRedLocal.IsChecked != true,
                        cortarEsLoDeseado: false);
            PonerEstado(TxtEstadoMalware, cortando: ChkMalware.IsChecked == true,
                        cortarEsLoDeseado: true);
        }

        static bool Encendida(IDictionary<string, bool> estado, string id)
            => estado.TryGetValue(id, out bool p) && p;

        private FrameworkElement FilaExcepcion(ExcepcionInfo e, bool puesta)
        {
            var fila = new StackPanel { Margin = new Thickness(0, 6, 0, 6) };

            // Primera línea: el nombre a la izquierda y, a la derecha, el interruptor con las
            // palabras del estado debajo.
            var cab = new Grid();
            cab.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            cab.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var nombre = new TextBlock
            {
                Text = e.Nombre,
                Style = (Style)Resources["CardTitle"],
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
            };
            Grid.SetColumn(nombre, 0);

            var derecha = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(18, 0, 0, 0),
            };

            // El interruptor se crea ya en su sitio y los manejadores se enganchan después: si no,
            // ponerlo a mano dispararía el cambio y PCJ escribiría en el firewall al abrir Ajustes.
            var sw = new CheckBox
            {
                IsChecked = puesta,
                Tag = e.Id,
                HorizontalAlignment = HorizontalAlignment.Right,
                ToolTip = QueEscribe(e),
            };
            // SwitchClaro está en el diccionario de la aplicación, no en el de esta ventana, así
            // que hay que buscarlo hacia arriba: Resources["SwitchClaro"] solo mira lo propio.
            if (TryFindResource("SwitchClaro") is Style estilo) sw.Style = estilo;
            sw.Checked += OnExcCambia;
            sw.Unchecked += OnExcCambia;

            var estado = new TextBlock
            {
                Style = (Style)Resources["CardHint"],
                Margin = new Thickness(0, 4, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
            };
            PonerEstado(estado, cortando: !puesta, cortarEsLoDeseado: false);
            _excEstados[e.Id] = estado;

            derecha.Children.Add(sw);
            derecha.Children.Add(estado);
            Grid.SetColumn(derecha, 1);

            cab.Children.Add(nombre);
            cab.Children.Add(derecha);
            fila.Children.Add(cab);

            // Segunda línea: el botón que abre la explicación, y la explicación escondida detrás.
            // Antes iba el texto entero debajo del nombre, y con las quince fichas la sección no
            // cabía en la ventana (había que bajar media pantalla para llegar a «Aceptar»).
            var boton = new Button
            {
                Style = (Style)FindResource("PlainButton"),
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(-4, 3, 0, 0),
                Padding = new Thickness(4, 2, 4, 2),
                ToolTip = "Ensancha la ficha y escribe para qué sirve y qué regla deja puesta.",
            };
            var flecha = new TextBlock
            {
                Text = "\uE70D",                       // chevron hacia abajo
                FontFamily = (FontFamily)FindResource("F.Icon"),
                FontSize = 11,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)FindResource("B.TextDim"),
            };
            var rotulo = new TextBlock
            {
                Text = "ver la explicación",
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)FindResource("B.TextDim"),
            };
            var pila = new StackPanel { Orientation = Orientation.Horizontal };
            pila.Children.Add(flecha);
            pila.Children.Add(rotulo);
            boton.Content = pila;

            var detalle = new TextBlock
            {
                Text = e.Detalle,
                Style = (Style)Resources["CardHint"],
                Margin = new Thickness(2, 5, 10, 2),
                TextWrapping = TextWrapping.Wrap,
                Visibility = Visibility.Collapsed,
            };
            // Y debajo, lo que escribe en el firewall: era solo el aviso flotante del interruptor,
            // y quien abre la ficha es justo quien lo quiere leer.
            var escribe = new TextBlock
            {
                Text = QueEscribe(e),
                Style = (Style)Resources["CardHint"],
                Margin = new Thickness(2, 4, 10, 2),
                TextWrapping = TextWrapping.Wrap,
                Visibility = Visibility.Collapsed,
            };

            bool abierta = false;
            Action abrir = () =>
            {
                abierta = !abierta;
                var ver = abierta ? Visibility.Visible : Visibility.Collapsed;
                detalle.Visibility = ver;
                escribe.Visibility = ver;
                flecha.Text = abierta ? "\uE70E" : "\uE70D";   // arriba / abajo
                rotulo.Text = abierta ? "ocultar la explicación" : "ver la explicación";
            };
            _excAbrir.Add(abrir);
            boton.Click += (s, ev) => abrir();

            fila.Children.Add(boton);
            fila.Children.Add(detalle);
            fila.Children.Add(escribe);
            return fila;
        }

        // Las palabras que van debajo de cada palanca. Se escriben porque el interruptor de esta
        // pantalla dice lo contrario de lo que parece: es una EXCEPCIÓN al bloqueo, o sea que
        // encendido es DEJAR PASAR. El autor lo preguntó mirando la lista, así que la duda es real.
        // El color sigue la misma lógica: ámbar en el estado que conviene notar, y para la lista
        // de malware eso es estar apagada (de ahí el último argumento).
        void PonerEstado(TextBlock el, bool cortando, bool cortarEsLoDeseado)
        {
            el.Text = cortando ? "bloqueo activo" : "bloqueo inactivo";
            // SetResourceReference en vez de asignar la brocha: es el equivalente a un
            // DynamicResource, y si se cambia de tema con Ajustes abierto el color cambia con él.
            el.SetResourceReference(TextBlock.ForegroundProperty,
                cortando == cortarEsLoDeseado ? "B.Green" : "B.Amber");
        }

        // Abre las N primeras fichas del catálogo. Solo lo usa el modo de foto.
        internal void PreviewAbrirExplicaciones(int cuantas)
        {
            for (int i = 0; i < Math.Min(cuantas, _excAbrir.Count); i++) _excAbrir[i]();
        }

        // Lo que va a escribir esa excepción, dicho en claro. Sirve de aviso al pasar el puntero
        // por el interruptor: el texto largo explica PARA QUÉ, y esto explica CÓMO lo abre.
        private static string QueEscribe(ExcepcionInfo e)
        {
            List<FirewallService.ReglaSpec> reglas;
            try { reglas = e.Reglas() ?? new List<FirewallService.ReglaSpec>(); }
            catch { return "PCJ no pudo calcular aquí las reglas de esta excepción."; }

            if (reglas.Count == 0)
                return "En este equipo no escribe ninguna regla: el programa que necesitaría no está.";

            var sb = new StringBuilder();
            sb.AppendLine(reglas.Count + (reglas.Count == 1 ? " regla que escribe Windows Firewall:"
                                                            : " reglas que escribe Windows Firewall:"));
            foreach (var r in reglas)
            {
                string d = r.d ?? "";
                // Todas empiezan por el mismo prefijo; repetirlo doce veces en un aviso no aporta.
                int i = d.LastIndexOf(" · ", StringComparison.Ordinal);
                if (i > 0) d = d.Substring(i + 3);
                sb.Append("• ").Append(d);
                sb.Append(r.dir == 1 ? " — entrada" : " — salida");
                if (r.rp.Length > 0) sb.Append(", puerto remoto ").Append(r.rp);
                if (r.lp.Length > 0) sb.Append(", puerto local ").Append(r.lp);
                sb.AppendLine();
            }
            return sb.ToString().TrimEnd();
        }

        private async void OnExcCambia(object sender, RoutedEventArgs e)
        {
            if (!_excLista || _excOcupada) return;
            if (sender is not CheckBox sw || sw.Tag is not string id) return;
            var info = AppExcepciones.PorId(id);
            if (info == null) return;

            var estado = App.Settings.Current.ExcepcionesSistema;
            bool puesta = sw.IsChecked == true;
            estado[id] = puesta;
            App.Settings.Save();
            if (_excEstados.TryGetValue(id, out var etiqueta))
                PonerEstado(etiqueta, cortando: !puesta, cortarEsLoDeseado: false);

            TxtExcPie.Text = "Escribiendo en Windows Firewall…";
            await AplicarExcepciones(info, puesta);
        }

        // Escríbelas todas otra vez, sin preguntar, desde el pie de la sección.
        private async void OnExcReescribir(object sender, RoutedEventArgs e)
        {
            if (_excOcupada) return;
            TxtExcPie.Text = "Escribiendo en Windows Firewall…";
            await AplicarExcepciones(null, false);
        }

        // info == null significa "reescribe las que estén encendidas", que es el mismo viaje.
        private async Task AplicarExcepciones(ExcepcionInfo? info, bool puesta)
        {
            _excOcupada = true;
            ExcRecomendadas.IsEnabled = false;
            ExcOpcionales.IsEnabled = false;

            var estado = App.Settings.Current.ExcepcionesSistema;
            string error = "";
            bool ok = false;
            // El firewall COM puede tardar un segundo largo con las doce posiciones de las
            // quince excepciones: en el hilo de la interfaz eso es la ventana congelada.
            await Task.Run(() => ok = AppExcepciones.Aplicar(estado, out error));

            ExcRecomendadas.IsEnabled = true;
            ExcOpcionales.IsEnabled = true;
            _excOcupada = false;

            string nombre = info == null ? "las excepciones" : "«" + info.Nombre + "»";
            if (!ok)
            {
                TxtExcPie.Text = "No se pudo escribir en Windows Firewall: " +
                                 (error.Length > 0 ? error : "el sistema lo rechazó.");
                App.Events.Add(MonitorRedPCJ.Models.EventKind.RuleChanged,
                    "Monitor de Red PCJ no pudo actualizar " + nombre +
                    " en Windows Firewall: " + error, "", important: true);
                return;
            }

            App.Events.Add(MonitorRedPCJ.Models.EventKind.RuleChanged,
                info == null
                    ? "Monitor de Red PCJ reescribió las excepciones del sistema: " +
                      AppExcepciones.Resumen(estado)
                    : "Monitor de Red PCJ " + (puesta ? "abrió" : "cerró") +
                      " la excepción del sistema «" + info.Nombre + "» · " +
                      AppExcepciones.Resumen(estado),
                "", important: false);

            TxtExcPie.Text = (error.Length > 0
                ? "Se aplicó, pero Windows rechazó algo: " + error + " · "
                : "") + "Ahora mismo: " + AppExcepciones.Resumen(estado) + ".";

            // La banda de Protección no se refresca desde aquí: su propio repaso del firewall va
            // cada doce segundos y se fuerza al cambiar de pestaña, así que al usuario siempre
            // le sale el número nuevo.
        }

        // ---------- Las dos sueltas: red local y lista de malware ----------

        // Abrir la red de casa son cuatro reglas del firewall; cerrar el malware es escribir el
        // fichero hosts. Las dos se aplican al soltar el interruptor, por el mismo motivo que las
        // excepciones del catálogo: esperar a Aceptar deja al usuario con la impresora muerta
        // creyendo que ya la había encendido.

        private async void OnRedLocal(object sender, RoutedEventArgs e)
        {
            if (!_excLista || _excOcupada) return;
            bool puesta = ChkRedLocal.IsChecked == true;
            var s = App.Settings.Current;
            s.TraficoLocalLibre = puesta;
            App.Settings.Save();
            EstadosSueltos();

            _excOcupada = true;
            ChkRedLocal.IsEnabled = false;
            TxtExcPie.Text = "Escribiendo en Windows Firewall…";

            string error = "";
            bool ok = false;
            await Task.Run(() => ok = AppExcepciones.AplicarRedLocal(puesta, out error));

            ChkRedLocal.IsEnabled = true;
            _excOcupada = false;

            string que = puesta ? "abrió el tráfico con los aparatos de la red local"
                                : "cerró el tráfico con los aparatos de la red local";
            if (!ok)
            {
                TxtExcPie.Text = "No se pudo escribir en Windows Firewall: " +
                                 (error.Length > 0 ? error : "el sistema lo rechazó.");
                App.Events.Add(MonitorRedPCJ.Models.EventKind.RuleChanged,
                    "Monitor de Red PCJ no pudo " + que + ": " + error, "", important: true);
                return;
            }
            App.Events.Add(MonitorRedPCJ.Models.EventKind.RuleChanged,
                "Monitor de Red PCJ " + que, "", important: false);
            TxtExcPie.Text = (error.Length > 0 ? "Se aplicó, pero Windows rechazó algo: " + error + " · " : "") +
                             "Red local: " + (puesta ? "abierta" : "cerrada") + ".";
        }

        private async void OnMalware(object sender, RoutedEventArgs e)
        {
            if (!_excLista || _excOcupada) return;
            var s = App.Settings.Current;
            bool puesta = ChkMalware.IsChecked == true;
            s.BloqueoMalwareActivo = puesta;
            App.Settings.Save();
            EstadosSueltos();

            // Al encenderla con la copia ya bajada no hace falta volver a internet: se escribe tal
            // cual. Solo se descarga cuando no hay copia de la que escribir, y al apagar nunca se
            // descarga, que si la descarga falla se cortaría también el quitar el bloque.
            await EscribirListaMalware(puesta && !MalwareService.HayCache(), puesta);
        }

        // actualizar = bajar la lista antes de escribir; escribir = tocar el hosts.
        // Apagar la opción siempre escribe: lo que escribe es el hosts sin el bloque de PCJ.
        private async Task EscribirListaMalware(bool actualizar, bool escribir)
        {
            var s = App.Settings.Current;
            _excOcupada = true;
            ChkMalware.IsEnabled = false;
            BtnMalwareActualizar.IsEnabled = false;
            TxtMalwareEstado.Text = actualizar && !MalwareService.HayCache()
                ? "Bajando la lista…"
                : (escribir ? "Escribiendo el fichero hosts…" : "Quitando el bloque de PCJ del hosts…");

            string aviso = "", error = "";
            var resultado = await Task.Run(() =>
            {
                if (actualizar)
                {
                    int n;
                    if (!MalwareService.Descargar(s.MalwareListaUrl, out aviso, out n))
                        return ("descarga", false);
                    s.MalwareActualizadoUtc = DateTime.UtcNow;
                    s.MalwareEntradas = n;
                    App.Settings.Save();
                }
                if (!escribir) return ("quitado", true);
                var dominios = MalwareService.LeerCache();
                if (dominios.Count == 0) return ("vacia", false);
                bool ok = MalwareService.EscribirHosts(dominios, out error);
                return (ok ? "escrito" : "error", ok);
            });

            ChkMalware.IsEnabled = true;
            BtnMalwareActualizar.IsEnabled = true;
            _excOcupada = false;

            string estado = MalwareService.Estado(s);
            switch (resultado.Item1)
            {
                case "descarga":
                    TxtMalwareEstado.Text = aviso + " " + estado;
                    App.Events.Add(MonitorRedPCJ.Models.EventKind.RuleChanged,
                        "Monitor de Red PCJ no pudo actualizar la lista de malware: " + aviso,
                        "", important: false);
                    break;
                case "vacia":
                    TxtMalwareEstado.Text = "No hay ninguna lista guardada de la que escribir. " +
                                            "Dale a «Actualizar la lista» con internet.";
                    break;
                case "error":
                    TxtMalwareEstado.Text = "No se pudo escribir el hosts: " + error;
                    App.Events.Add(MonitorRedPCJ.Models.EventKind.RuleChanged,
                        "Monitor de Red PCJ no pudo escribir la lista de malware en el fichero hosts: " +
                        error, MalwareService.RutaHosts, important: true);
                    break;
                case "quitado":
                    TxtMalwareEstado.Text = "Bloqueo de la lista quitado del hosts. " + estado;
                    App.Events.Add(MonitorRedPCJ.Models.EventKind.RuleChanged,
                        "Monitor de Red PCJ quitó la lista de malware del fichero hosts",
                        MalwareService.RutaHosts, important: false);
                    break;
                default:
                    TxtMalwareEstado.Text = (aviso.Length > 0 ? aviso + " · " : "") +
                        "Lista escrita en el hosts. " + estado;
                    App.Events.Add(MonitorRedPCJ.Models.EventKind.RuleChanged,
                        "Monitor de Red PCJ escribió la lista de malware en el fichero hosts: " +
                        s.MalwareEntradas + " dominios", MalwareService.RutaHosts, important: false);
                    break;
            }

            // El aviso de abajo solo se ve si lo que el interruptor pide y lo que el hosts tiene no
            // cuadran: después de escribir, casi siempre ya no cuadra nada que decir.
            RefrescarAviso();
        }

        private async void OnMalwareActualizar(object sender, RoutedEventArgs e)
        {
            if (!_excLista || _excOcupada) return;
            // El proceso se ve en su panel: ahí está la barra, los bytes que entran, las líneas
            // leídas y, si algo se cae, el porqué escrito en castellano. Dejarlo todo en una línea
            // de aquí abajo fue lo que hizo parecer que el botón no hacía nada.
            AbrirPanelMalware(true);
            await Task.CompletedTask;
        }

        // ---------- El panel del proceso de la lista ----------

        MalwareWindow? _panelMalware;

        // Una sola ventana: si ya está abierta, se trae a delante en vez de montar otra que esté
        // bajando la misma lista a la vez.
        void AbrirPanelMalware(bool descargar)
        {
            if (_panelMalware != null)
            {
                _panelMalware.Activate();
                return;
            }

            var w = new MalwareWindow(descargar, App.Settings.Current.BloqueoMalwareActivo)
            {
                Owner = this,
            };
            w.Closed += (_, _) => { _panelMalware = null; RefrescarMalware(); };
            _panelMalware = w;
            w.Show();
        }

        private void OnMalwarePanel(object sender, RoutedEventArgs e) => AbrirPanelMalware(false);

        // La caché y el hosts son dos cosas distintas: tener la lista bajada no es tenerla
        // escrita. Mientras solo se enseñaba «N dominios en la copia guardada», el interruptor
        // podía estar encendido con el hosts limpio y nadie se enteraba.
        void RefrescarMalware()
        {
            TxtMalwareEstado.Text = MalwareService.Estado(App.Settings.Current);
            RefrescarAviso();
        }

        void RefrescarAviso()
        {
            var s = App.Settings.Current;
            bool puesto = s.BloqueoMalwareActivo;
            bool escrito = MalwareService.BloqueadoEnHosts();
            int enHosts = MalwareService.EntradasEnHosts();

            string aviso = "";
            if (puesto && !escrito)
                aviso = "Ojo: el interruptor está encendido, pero el hosts no lleva el bloque de PCJ, " +
                        "así que esos dominios todavía no están cortados. " +
                        (MalwareService.HayCache()
                            ? "Abre «Ver el proceso» y dale a escribir, o vuelve a apagar y encender " +
                              "el interruptor."
                            : "Primero hace falta bajar la lista: «Ver el proceso» lo enseña.");
            else if (puesto && escrito && enHosts < 10)
                aviso = "El bloque está en el hosts, pero con " + enHosts +
                        " dominios: se escribió a medias o otro programa lo tocó. " +
                        "Dale a «Actualizar la lista» para reescribirlo entero.";
            else if (!puesto && escrito)
                aviso = "El interruptor está apagado y el hosts todavía tiene " + enHosts +
                        " dominios de PCJ escritos. Vuelve a encenderlo y apágalo, o bórralos desde " +
                        "el panel.";

            TxtMalwareAviso.Text = aviso;
            TxtMalwareAviso.Visibility = aviso.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // ---------- Pestaña «Herramientas» ----------

        private void MontarHerramientas()
        {
            // La ficha de "sin pestaña" ya no lleva un gris escrito a mano: pide los neutros
            // de la paleta, que son los que cambian cuando se cambia de tema.
            var neutros = new OpcionHerramienta
            {
                Id = "",
                Nombre = "Sin pestaña añadida",
                Lema = "Solo Tráfico, Protección y Recursos",
                Glifo = Simbolo(0xE711),
                Mosaico = ThemeService.Tinta(ThemeService.Actual, "B.ChipNeutralBg") ?? Brushes.Transparent,
                Tinta = ThemeService.Tinta(ThemeService.Actual, "B.ChipNeutralFg") ?? Brushes.Gray,
            };
            var opciones = new List<OpcionHerramienta> { neutros };
            foreach (var def in ToolCatalog.Nuevas)
                opciones.Add(new OpcionHerramienta
                {
                    Id = def.Id,
                    Nombre = def.Nombre,
                    Lema = def.Lema,
                    Glifo = Simbolo(def.Glifo),
                    Mosaico = Suave(def.Color),
                    // El color de identidad de cada herramienta, aclarado si el tema es oscuro
                    // (en Nocturna un petróleo apagado desaparece sobre el fondo).
                    Tinta = ThemeService.PincelDeIdentidad(def.Color),
                });

            _herramientaElegida = App.Settings.Current.HerramientaEnPrincipal ?? "";
            LstPrincipal.ItemsSource = opciones;
            int i = opciones.FindIndex(o => o.Id == _herramientaElegida);
            LstPrincipal.SelectedIndex = i < 0 ? 0 : i;
            // La selección de arriba dispara SelectionChanged: a partir de ahora sí es un clic real.
            _listaLista = true;
        }

        private void OnElegirPrincipal(object sender, SelectionChangedEventArgs e)
        {
            if (!_listaLista) return;
            if (LstPrincipal.SelectedItem is OpcionHerramienta o) _herramientaElegida = o.Id;
        }

        private void OnAbrirHerramientas(object sender, RoutedEventArgs e)
        {
            // Se abre desde la ventana principal para que la caja de herramientas herede el
            // dueño, el estado y los atajos que ya tiene montados ahí.
            var m = this.Owner as MainWindow ?? Application.Current.MainWindow as MainWindow;
            if (m == null)
            {
                MessageBox.Show("Abre antes la ventana de PCJ.", "Monitor de Red PCJ",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            m.AbrirHerramientas(_herramientaElegida);
        }

        // ---- pequeños ayudantes de color, para no repetir el conversor en cada ficha ----

        static Color Hex(string hex)
        {
            try { return (Color)ColorConverter.ConvertFromString(hex); }
            catch { return Color.FromRgb(15, 163, 163); }
        }

        static Brush P(string hex)
        {
            var b = new SolidColorBrush(Hex(hex));
            b.Freeze();
            return b;
        }

        // El mosaico del icono va en el color de la herramienta pero muy diluido, como en la
        // ventana de herramientas; si fuera pleno se comería el resto de la rejilla.
        static Brush Suave(string hex)
        {
            var c = Hex(hex);
            var b = new SolidColorBrush(Color.FromArgb(38, c.R, c.G, c.B));
            b.Freeze();
            return b;
        }

        static Brush Oscurecer(string hex)
        {
            var c = Hex(hex);
            var b = new SolidColorBrush(Color.FromArgb(255,
                (byte)(c.R * 0.72), (byte)(c.G * 0.72), (byte)(c.B * 0.72)));
            b.Freeze();
            return b;
        }

        static string Simbolo(int cp)
        {
            try { return char.ConvertFromUtf32(cp); } catch { return ""; }
        }

        private void OnMenu(object sender, SelectionChangedEventArgs e)
        {
            if (PageGeneral == null) return;
            int i = Menu.SelectedIndex;
            PageGeneral.Visibility = i == 0 ? Visibility.Visible : Visibility.Collapsed;
            PageProtect.Visibility = i == 1 ? Visibility.Visible : Visibility.Collapsed;
            PageExc.Visibility = i == 2 ? Visibility.Visible : Visibility.Collapsed;
            PageScanner.Visibility = i == 3 ? Visibility.Visible : Visibility.Collapsed;
            PageTools.Visibility = i == 4 ? Visibility.Visible : Visibility.Collapsed;
            PageThemes.Visibility = i == 5 ? Visibility.Visible : Visibility.Collapsed;
            PageAbout.Visibility = i == 6 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OnOk(object sender, RoutedEventArgs e)
        {
            var s = App.Settings.Current;
            StartupHelper.SetEnabled(ChkStartup.IsChecked == true);
            s.RunOnStartup = ChkStartup.IsChecked == true;
            s.AlertsEnabled = ChkAlerts.IsChecked == true;
            bool incognitoAhora = ChkIncognito.IsChecked == true;
            bool yaEstaba = Recording.Incognito;
            s.IncognitoMode = incognitoAhora;
            if (int.TryParse(TxtRetention.Text, out var days) && days > 0) s.HistoryRetentionDays = days;
            s.AutoScanNetwork = ChkAutoScan.IsChecked == true;
            if (int.TryParse(TxtScanInterval.Text, out var mins) && mins > 0) s.ScanIntervalMinutes = mins;
            s.HerramientaEnPrincipal = _herramientaElegida ?? "";

            // El bloqueo total no se cambia desde aquí: exige repasar app por app en la
            // pestaña «Protección», así que esta pantalla solo informa del estado.
            App.Settings.Save();
            // El incógnito no es una bandera decorativa: corta los escritorios a disco, y al
            // desactivarlo recupera el histórico que había antes de activarlo. Solo se llama
            // cuando cambia: si se llamara en cada Aceptar, la foto de guarda se pisaría y al
            // salir del modo quedaría recuperado el tramo que se suponía que no debía guardarse.
            if (incognitoAhora != yaEstaba) App.SetIncognito(incognitoAhora);
            // La pestaña nueva de la barra se monta y la vieja se tira al momento: si se
            // dejara para el próximo arranque, el cambio parecería no haber hecho nada.
            (this.Owner as MainWindow ?? Application.Current.MainWindow as MainWindow)
                ?.RefrescarHerramientaPrincipal();
            Close();
        }

        private void OnCancel(object sender, RoutedEventArgs e) => Close();

        private void OnClearHistory(object sender, RoutedEventArgs e)
        {
            // Antes este botón solo borraba dos ficheros y pedía reiniciar: dejaba intacto el
            // apps.json, que es justamente donde vive el histórico de tráfico por programa.
            var ok = MessageBox.Show(
                "Se borra todo lo que PCJ guardó hasta ahora: el registro de eventos, el tráfico " +
                "acumulado por programa con los destinos a los que salió, y la lista de dispositivos " +
                "de la red.\n\nLas reglas del firewall no se tocan: quién puede salir y quién no " +
                "seguirá igual.\n\n¿Borrar el histórico?",
                "Borrar histórico", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (ok != MessageBoxResult.Yes) return;

            try
            {
                App.Traffic.ClearHistory();
                App.Events.Clear();
                App.Scanner.ClearDevices();
                foreach (var f in new[] { Paths.HistoryFile })
                    if (File.Exists(f)) File.Delete(f);
                MessageBox.Show("Histórico borrado. La monitorización en vivo sigue funcionando.",
                    "Monitor de Red PCJ", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }
        }

        private void OnCleanup(object sender, RoutedEventArgs e)
        {
            int n = App.Firewall.CleanupStaleRules();
            string msg = n < 0
                ? "Hace falta permisos de administrador: usa «Reiniciar como administrador» en el icono de la bandeja."
                : (n == 0 ? "No hay reglas obsoletas." : $"Se eliminaron {n} reglas.");
            MessageBox.Show(msg, "Limpiar reglas", MessageBoxButton.OK,
                n < 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }

        private void OnScanNow(object sender, RoutedEventArgs e) => App.Scanner.ScanAsync();

        // Vuelco a PNG para poder revisar el diseño de esta ventana sin tener que abrirla de
        // verdad ni hacer clic en nada. Solo pinta: no guarda configuración ni toca el firewall.
        //   MonitorRedPCJ.exe --settings-preview archivo.png [pestaña]
        public static void RenderPreview(string archivo, int pestana)
        {
            var win = new SettingsWindow
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                ShowActivated = false,
                Left = -9000,
                Top = 120,
                Width = 880,
                Height = 600,
            };
            win.Menu.SelectedIndex = Math.Max(0, Math.Min(6, pestana));
            win.Show();

            // En «Excepciones del sistema» las fichas van cerradas por defecto, así que la foto
            // nunca enseñaría el estado abierto. Se abren las dos primeras a mano: es lo único que
            // no se puede comprobar pinchando desde un guion.
            if (pestana == 2) win.PreviewAbrirExplicaciones(2);

            var frame = new System.Windows.Threading.DispatcherFrame();
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
            timer.Tick += (s, e) => { timer.Stop(); frame.Continue = false; };
            timer.Start();
            System.Windows.Threading.Dispatcher.PushFrame(frame);

            int w = Math.Max(1, (int)Math.Ceiling(win.ActualWidth));
            int h = Math.Max(1, (int)Math.Ceiling(win.ActualHeight));
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, 96, 96,
                System.Windows.Media.PixelFormats.Pbgra32);
            rtb.Render(win);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            using (var fs = File.Create(archivo)) enc.Save(fs);

            // «Excepciones del sistema» es mucho más alta que la ventana, y una foto de la
            // ventana solo enseña las tres primeras filas. Se vuelca también el contenido medido
            // sin límite de alto, en una segunda imagen junto a la primera.
            if (win.Contenido.Content is FrameworkElement cuerpo && win.Contenido.ActualWidth > 40)
            {
                double cw = Math.Max(200, win.Contenido.ActualWidth -
                                          SystemParameters.VerticalScrollBarWidth);
                cuerpo.Measure(new Size(cw, double.PositiveInfinity));
                cuerpo.Arrange(new Rect(0, 0, cw, Math.Max(1, cuerpo.DesiredSize.Height)));
                cuerpo.UpdateLayout();
                // Sin fondo propio la imagen saldría con el texto sobre transparente, ilegible
                // en un visor claro: se le pone el de la ventana, que es el que hay detrás.
                if (cuerpo is Panel panel && panel.Background == null) panel.Background = win.Background;

                int ch = Math.Max(1, (int)Math.Ceiling(cuerpo.DesiredSize.Height));
                var rtb2 = new System.Windows.Media.Imaging.RenderTargetBitmap((int)cw, ch, 96, 96,
                    System.Windows.Media.PixelFormats.Pbgra32);
                rtb2.Render(cuerpo);
                var enc2 = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc2.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb2));
                using var fs2 = File.Create(
                    Path.ChangeExtension(archivo, null) + ".entera.png");
                enc2.Save(fs2);
            }

            win.Close();
        }
    }
}
