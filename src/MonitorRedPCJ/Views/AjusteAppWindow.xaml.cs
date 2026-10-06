using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using MonitorRedPCJ.Models;
using MonitorRedPCJ.Services;

namespace MonitorRedPCJ.Views
{
    /// <summary>
    /// La ventana de «Configuración adicional» de una aplicación: los cinco modos de salida,
    /// la caja de puertos y los dos refuerzos. Se abre desde la fila desplegada de Protección.
    ///
    /// Todo lo que se ve aquí sale de AppAjustes.Modos, que es también el que escribe las reglas.
    /// Así el texto que lee el usuario y lo que Windows Firewall recibe no se pueden contradecir.
    /// </summary>
    public partial class AjusteAppWindow : Window
    {
        private readonly TrackedApp _cfg;

        // Tarjeta y marca por id de modo, para repintar cuál está elegido sin rehacer la lista.
        private readonly Dictionary<string, Border> _tarjetas = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Ellipse> _marcas = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Border> _anillos = new(StringComparer.Ordinal);
        private CheckBox? _local, _hijos;

        // Si se aplicó de verdad; ProtectView lo usa para saber si tiene que refrescar.
        public bool Aplicado { get; private set; }

        private static readonly string PieNormal =
            "Se cambia al instante, sin cerrar el programa. Pide aprobación de administrador una " +
            "sola vez, aunque el modo necesite varios permisos y varios cortes.";

        public AjusteAppWindow(string exePath, string nombre, ImageSource? icono)
        {
            InitializeComponent();

            // Copia de trabajo: si se cancela, lo apuntado aquí se tira y apps.json no se entera.
            var vivo = App.Traffic.FindApp(exePath);
            _cfg = new TrackedApp
            {
                ExePath = exePath,
                Name = nombre,
                ModoAdicional = vivo?.ModoAdicional ?? "",
                PuertosAdicionales = vivo?.PuertosAdicionales ?? "",
                SoloRedLocal = vivo?.SoloRedLocal ?? false,
                HeredarAHijos = vivo?.HeredarAHijos ?? false,
            };

            TxtApp.Text = string.IsNullOrEmpty(nombre)
                ? System.IO.Path.GetFileName(exePath) : nombre;
            TxtRuta.Text = exePath;
            TxtRuta.ToolTip = exePath;
            TxtPuertos.Text = _cfg.PuertosAdicionales;
            if (icono != null)
            {
                Icono.Source = icono;
                IconoHost.Visibility = Visibility.Visible;
            }

            PonerEstado();
            MontarModos();
            MontarRefuerzos();
            RefrescarEleccion();
        }

        // La pastilla de Salida dicha en la cabecera, para no aplicar un modo a ciegas.
        private void PonerEstado()
        {
            var d = App.Firewall.GetDecision(_cfg.ExePath, Direction.Out);
            TxtEstado.Text = d switch
            {
                Decision.Permitido => "salida permitida",
                Decision.Bloqueado => "salida cortada",
                _ => "salida sin decidir"
            };
            TxtEstado.Foreground = Brush(
                d == Decision.Permitido ? "B.Green" : d == Decision.Bloqueado ? "B.Coral" : "B.TextDim");
        }

        // ---------- Las seis tarjetas del modo ----------

        private void MontarModos()
        {
            foreach (var m in AppAjustes.Modos)
            {
                var card = new Border { Style = (Style)Resources["OpcionCard"], Focusable = true };
                var cuerpo = new Grid();
                cuerpo.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
                cuerpo.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                // La marca: un anillo que se rellena cuando la tarjeta está elegida. Se lee cuál
                // manda de un vistazo, sin la casilla cuadrada gris de Windows.
                var anillo = new Border
                {
                    Width = 16,
                    Height = 16,
                    CornerRadius = new CornerRadius(8),
                    BorderThickness = new Thickness(1.6),
                    BorderBrush = Brush("B.Line"),
                    Background = Brush("B.Raised"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 2, 0, 0),
                };
                var punto = new Ellipse
                {
                    Width = 8,
                    Height = 8,
                    Fill = Brushes.Transparent,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                anillo.Child = punto;
                Grid.SetColumn(anillo, 0);
                cuerpo.Children.Add(anillo);

                var texto = new StackPanel();
                texto.Children.Add(new TextBlock { Text = m.Nombre, Style = (Style)Resources["OpcionNombre"] });
                texto.Children.Add(new TextBlock { Text = m.Detalle, Style = (Style)Resources["OpcionTexto"] });
                Grid.SetColumn(texto, 1);
                cuerpo.Children.Add(texto);

                card.Child = cuerpo;
                string id = m.Id;
                card.MouseLeftButtonUp += (_, _) => Elegir(id);
                card.KeyDown += (_, ev) =>
                {
                    if (ev.Key == Key.Enter || ev.Key == Key.Space) { Elegir(id); ev.Handled = true; }
                };

                _tarjetas[id] = card;
                _marcas[id] = punto;
                _anillos[id] = anillo;
                ModosHost.Children.Add(card);
            }
        }

        private void Elegir(string id)
        {
            _cfg.ModoAdicional = id;
            RefrescarEleccion();
        }

        // Repinta cuál está elegido, enseña o esconde la caja de puertos y avisa de lo que no
        // se puede combinar.
        private void RefrescarEleccion()
        {
            var elegido = AppAjustes.PorId(_cfg.ModoAdicional);
            foreach (var (id, card) in _tarjetas)
            {
                bool on = id == elegido.Id;
                card.BorderBrush = on ? Brush("B.Accent") : Brush("B.Line");
                card.Background = on ? Brush("B.PanelAlt") : Brush("B.Panel");
                _marcas[id].Fill = on ? Brush("B.Accent") : Brushes.Transparent;
                _anillos[id].BorderBrush = on ? Brush("B.Accent") : Brush("B.Line");
            }

            if (elegido.Id == "soloPuertos")
            {
                PuertosCard.Visibility = Visibility.Visible;
                TxtPuertos.Focus();
                TxtPuertos.CaretIndex = TxtPuertos.Text.Length;
            }
            else PuertosCard.Visibility = Visibility.Collapsed;

            // Los refuerzos que el modo elegido no puede acompañar se apagan y se dice por qué,
            // en vez de dejarlos activos y que nadie entienda por qué no surten efecto.
            bool puedeLocal = elegido.Id != "bloquearTodo" && !elegido.QuitaTodo;
            bool puedeHijos = !elegido.QuitaTodo;
            if (_local != null) ApagarRefuerzo(_local, puedeLocal);
            if (_hijos != null) ApagarRefuerzo(_hijos, puedeHijos);

            TxtPie.Text = !puedeLocal
                ? "El modo «" + elegido.Nombre + "» no admite refuerzos: o no hay salida que permitir " +
                  "(corte total), o PCJ no escribe ninguna regla en esta aplicación (sin restricciones)."
                : PieNormal;
        }

        // ---------- Los dos refuerzos ----------

        private void MontarRefuerzos()
        {
            _local = Refuerzo(
                "Restringir a la red local",
                "La aplicación solo puede hablar con los aparatos de tu casa o de tu oficina: el mismo " +
                "segmento de red y las direcciones privadas de siempre (10.x, 172.16-31.x, 192.168.x, " +
                "169.254.x y la propia 127.x). Todo lo que salga a internet se corta. Es el modo de un " +
                "programa de domótica, de una impresora o de un servidor local que no tiene por qué ver " +
                "la calle. Se puede sumar a cualquier permiso de arriba: lo que limita es el destino, " +
                "no el puerto.",
                _cfg.SoloRedLocal, v => _cfg.SoloRedLocal = v);

            _hijos = Refuerzo(
                "Aplicar lo mismo a los procesos hijo",
                "Muchos programas no trabajan solos: abren otros procesos que son los que de verdad " +
                "mueven la red (un instalador que lanza su descargador, un navegador que abre un proceso " +
                "por pestaña). Con esto marcado, los procesos que esta aplicación engendra heredan su " +
                "configuración, así que no se te escapa tráfico por un programa que no aparecía en la " +
                "lista. PCJ los mira en su repaso de siempre y solo escribe reglas cuando nace o muere " +
                "alguna. La cadena se para en el hijo: los nietos no heredan del abuelo.",
                _cfg.HeredarAHijos, v => _cfg.HeredarAHijos = v);
        }

        private CheckBox Refuerzo(string titulo, string detalle, bool puesto, Action<bool> alCambiar)
        {
            var card = new Border { Style = (Style)Resources["OpcionCard"] };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var texto = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            texto.Children.Add(new TextBlock { Text = titulo, Style = (Style)Resources["OpcionNombre"] });
            texto.Children.Add(new TextBlock { Text = detalle, Style = (Style)Resources["OpcionTexto"] });
            Grid.SetColumn(texto, 0);
            grid.Children.Add(texto);

            var c = new CheckBox
            {
                // SwitchClaro está en el diccionario de la aplicación, no en el de esta ventana,
                // así que se busca con TryFindResource (Resources["..."] solo mira aquí dentro).
                Style = (Style)TryFindResource("SwitchClaro"),
                IsChecked = puesto,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 2, 0),
            };
            c.Click += (_, _) => alCambiar(c.IsChecked == true);
            // Pulsar la tarjeta entera mueve el mando: acertar el interruptor pequeño es difícil.
            card.MouseLeftButtonUp += (_, _) =>
            {
                if (!c.IsEnabled) return;
                c.IsChecked = !(c.IsChecked == true);
                alCambiar(c.IsChecked == true);
            };
            Grid.SetColumn(c, 1);
            grid.Children.Add(c);

            card.Child = grid;
            c.Tag = card;   // para poder apagar la tarjeta entera, no solo el interruptor
            RefuerzosHost.Children.Add(card);
            return c;
        }

        // Un refuerzo que el modo no admite se ve apagado: si solo se desactiva la casilla, la
        // tarjeta sigue invitando a pulsarla y nadie entiende que ahí ya no hay nada que hacer.
        private static void ApagarRefuerzo(CheckBox c, bool activo)
        {
            c.IsEnabled = activo;
            if (c.Tag is not Border card) return;
            card.Opacity = activo ? 1.0 : 0.42;
            card.Cursor = activo ? Cursors.Hand : Cursors.Arrow;
        }

        // ---------- Botones ----------

        private void OnEjemploPuertos(object sender, RoutedEventArgs e)
        {
            TxtPuertos.Text = "443, 80, 8000:8100, 53/udp, 123/udp";
            TxtPuertos.Focus();
            TxtPuertos.CaretIndex = TxtPuertos.Text.Length;
        }

        private void OnCancelar(object sender, RoutedEventArgs e) => Close();

        private async void OnAplicar(object sender, RoutedEventArgs e)
        {
            _cfg.PuertosAdicionales = TxtPuertos.Text.Trim();
            _cfg.SoloRedLocal = _local?.IsChecked == true;
            _cfg.HeredarAHijos = _hijos?.IsChecked == true;

            var m = AppAjustes.PorId(_cfg.ModoAdicional);

            // Antes de abrir la ventana de UAC se comprueba la lista de puertos, que es lo único
            // que suele venir mal escrito.
            var sondeoB = new List<string>();
            var sondeoC = new List<FirewallService.ReglaSpec>();
            if (!AppAjustes.Construir(_cfg.ExePath, _cfg, sondeoB, sondeoC, out string mal))
            {
                Aviso(mal);
                TxtPuertos.Focus();
                return;
            }
            Aviso("");

            BtnAplicar.IsEnabled = false;
            bool ok = false;
            string error = "";
            await Task.Run(() => ok = AppAjustes.Aplicar(_cfg, out error));

            if (!ok)
            {
                BtnAplicar.IsEnabled = true;
                Aviso(error);
                MessageBox.Show(this,
                    "No se pudo aplicar la configuración de " + TxtApp.Text + ".\n\n" + error +
                    "\n\nSi la ventana de aprobación se canceló, vuelve a intentarlo aceptándola.",
                    "Monitor de Red PCJ", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            App.Events.Add(EventKind.RuleChanged,
                "Monitor de Red PCJ dejó a " + TxtApp.Text + " con la configuración adicional: " +
                m.Nombre + (AppAjustes.Resumen(_cfg).Length > 0 ? " · " + AppAjustes.Resumen(_cfg) : ""),
                _cfg.ExePath, important: false);

            // Si esta aplicación hereda a sus hijos, lo que ya estaba copiado en ellos se quedó
            // viejo ahora. Se avisa al sondeo, que es el único que escribe esas reglas.
            App.Traffic.RevalidarHeredades();

            Aplicado = true;
            Close();

            if (error.Length > 0)
                MessageBox.Show(this,
                    "Se aplicó, pero Windows Firewall no aceptó parte del trabajo:\n\n" + error,
                    "Monitor de Red PCJ", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // El aviso rojo debajo de la caja de puertos. Con texto vacío se esconde.
        private void Aviso(string texto)
        {
            if (texto.Length == 0)
            {
                TxtPuertosAviso.Visibility = Visibility.Collapsed;
                TxtPuertosAviso.Text = "";
                return;
            }
            TxtPuertosAviso.Text = texto;
            TxtPuertosAviso.Visibility = Visibility.Visible;
        }

        private Brush Brush(string clave)
            => Application.Current?.TryFindResource(clave) as Brush ?? Brushes.Gray;

        // Vuelco a PNG para revisar el diseño sin abrir la ventana ni tocar el firewall.
        //   MonitorRedPCJ.exe --ajuste-preview archivo.png [idDeModo]
        // Se pinta con un programa real del equipo para que la cabecera tenga nombre y ruta, y con
        // la caja de puertos escrita, para que el modo «solo los puertos» salga completo.
        public static void RenderPreview(string archivo, string modoId)
        {
            string exe = "C:\\Windows\\System32\\notepad.exe";
            string nombre = "cuadernos";
            foreach (var a in App.Traffic.GetApps())
            {
                if (string.IsNullOrEmpty(a.ExePath) || !System.IO.File.Exists(a.ExePath)) continue;
                exe = a.ExePath;
                nombre = string.IsNullOrEmpty(a.Name) ? System.IO.Path.GetFileName(exe) : a.Name;
                break;
            }

            var win = new AjusteAppWindow(exe, nombre, null)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                ShowActivated = false,
                Left = -9000,
                Top = 60,
                Width = 660,
                Height = 680,
            };
            if (!string.IsNullOrEmpty(modoId)) win.ElegirPublico(modoId);

            win.Show();
            var frame = new System.Windows.Threading.DispatcherFrame();
            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = System.TimeSpan.FromMilliseconds(900)
            };
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
            using (var fs = System.IO.File.Create(archivo)) enc.Save(fs);

            // Segunda foto: el cuerpo entero, estirado. La pantalla pone un techo a la altura
            // de la ventana, y los refuerzos de abajo se quedaban fuera de la primera imagen;
            // aquí se mide y se ordena el panel sin límite para pintarlo al completo.
            if (win.Cuerpo.Content is System.Windows.FrameworkElement cuerpo
                && cuerpo.ActualWidth > 1)
            {
                double cw = cuerpo.ActualWidth;
                cuerpo.Measure(new System.Windows.Size(cw, double.PositiveInfinity));
                cuerpo.Arrange(new System.Windows.Rect(0, 0, cw, cuerpo.DesiredSize.Height));
                int bh = Math.Max(1, (int)Math.Ceiling(cuerpo.DesiredSize.Height));
                var rb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    (int)Math.Ceiling(cw), bh, 96, 96,
                    System.Windows.Media.PixelFormats.Pbgra32);
                rb.Render(cuerpo);
                var enc2 = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc2.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rb));
                using var fs2 = System.IO.File.Create(
                    System.IO.Path.ChangeExtension(archivo, null) + ".cuerpo.png");
                enc2.Save(fs2);
            }

            win.Close();
        }

        // El mismo Elegir de la interfaz, para que el modo de preview lo pueda llamar.
        public void ElegirPublico(string id)
        {
            _cfg.PuertosAdicionales = "443, 80, 8000:8100, 53/udp";
            TxtPuertos.Text = _cfg.PuertosAdicionales;
            Elegir(id);
        }
    }
}
