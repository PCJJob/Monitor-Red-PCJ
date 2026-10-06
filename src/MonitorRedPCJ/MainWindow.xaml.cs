using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using MonitorRedPCJ.Services;
using MonitorRedPCJ.Services.Tools;
using MonitorRedPCJ.Views;
using MonitorRedPCJ.Views.Tools;

namespace MonitorRedPCJ
{
    public partial class MainWindow : Window, IAnfitrion
    {
        private readonly TrafficView _traffic = new();
        private readonly ProtectView _protect = new();
        private readonly HardwareView _hardware = new();
        private HerramientaPanel? _extra;

        public MainWindow()
        {
            InitializeComponent();
            ContentHost.Children.Add(_traffic);
            ContentHost.Children.Add(_protect);
            ContentHost.Children.Add(_hardware);
            // Registros y escáner ya no van aquí: viven en «Más herramientas». Si el usuario
            // eligió una herramienta para quedarse con su hueco, se monta ahora.
            MontarHerramientaPrincipal();
            ShowTab(_traffic);

            App.Traffic.DataUpdated += OnDataUpdated;
            App.Traffic.FirstConnectionDetected += OnFirstConnection;
            App.Traffic.BlockedLaunchDetected += OnBlockedLaunch;
            App.Settings.Current.RunOnStartup = StartupHelper.IsEnabled();

            // La pasilla del incógnito refleja el estado real (Recording), no el ajuste guardado,
            // así que se actualiza igual se cambie desde aquí, desde la bandeja o desde Configuración.
            Recording.Changed += UpdateIncognitoChip;
            UpdateIncognitoChip();
        }

        private void UpdateIncognitoChip()
        {
            if (App.IsShuttingDown) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    IncognitoChip.Visibility = Recording.Incognito ? Visibility.Visible : Visibility.Collapsed;
                }
                catch { }
            }), System.Windows.Threading.DispatcherPriority.Normal);
        }

        private void OnToggleIncognito(object sender, RoutedEventArgs e)
        {
            bool on = !Recording.Incognito;
            App.Settings.Current.IncognitoMode = on;
            App.Settings.Save();
            App.SetIncognito(on);
        }

        // Programa recién abierto sin permiso de salida, con el bloqueo total activo.
        private void OnBlockedLaunch(Models.TrackedApp app)
        {
            if (App.IsShuttingDown) return;
            Dispatcher.BeginInvoke(() =>
            {
                if (App.IsShuttingDown) return;
                // Si ya había una ventana de este programa no se monta otra, pero se avisa del
                // cierre igual: si no, la cola se quedaría esperando un aviso que nunca se cierra.
                if (AlertWindow.IsOpenFor(app.ExePath)) { App.Traffic.AvisoCerrado(app.ExePath); return; }
                var alert = new AlertWindow(app, "") { Owner = this };
                // Al cerrarse —respondida o descartada— se libera la cola para el siguiente.
                alert.Closed += (s, e) => App.Traffic.AvisoCerrado(app.ExePath);
                alert.Show();
            });
        }

        private void ShowTab(UIElement view)
        {
            foreach (UIElement c in ContentHost.Children)
                c.Visibility = c == view ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OnTab(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            if (sender == TabTraffic) ShowTab(_traffic);
            else if (sender == TabProtect) ShowTab(_protect);
            else if (sender == TabHardware) ShowTab(_hardware);
            else if (sender == TabExtra && _extra != null) ShowTab(_extra);
        }

        // «Más herramientas»: la ventana con las doce utilidades. Si ya está abierta, se trae
        // al frente en vez de abrir una segunda copia.
        private void OnTools(object sender, RoutedEventArgs e) => AbrirHerramientas(null);

        public void AbrirHerramientas(string? irA)
        {
            Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            if (ToolsWindow.Abierta is { } ya)
            {
                ya.Activate();
                if (!string.IsNullOrEmpty(irA)) ya.IrAHerramienta(irA!);
                return;
            }
            var w = new ToolsWindow(irA) { Owner = this };
            w.Show();
        }

        // ---------- Lo que piden los paneles de las herramientas ----------

        void IAnfitrion.IrAHerramienta(string id) => AbrirHerramientas(id);

        public void IrAProteccion(string buscar)
        {
            Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            TabProtect.IsChecked = true;
            Activate();
            _protect.Buscar(buscar ?? "");
        }

        public void IrARecursos()
        {
            Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            TabHardware.IsChecked = true;
            Activate();
        }

        // ---------- La herramienta que ocupa el hueco libre de la barra ----------

        private void MontarHerramientaPrincipal()
        {
            string id = App.Settings.Current.HerramientaEnPrincipal ?? "";
            var def = ToolCatalog.PorId(id);
            if (def == null)
            {
                TabExtra.Visibility = Visibility.Collapsed;
                return;
            }
            TabExtra.Content = def.Nombre;
            try { TabExtra.Tag = char.ConvertFromUtf32(def.Glifo); } catch { TabExtra.Tag = ""; }
            _extra = Herramientas.Crear(def.Id, this);
            ContentHost.Children.Add(_extra);
            TabExtra.Visibility = Visibility.Visible;
        }

        /// Configuracion cambió la herramienta de la barra: se quita la vieja y se monta la nueva.
        public void RefrescarHerramientaPrincipal()
        {
            if (_extra != null)
            {
                ContentHost.Children.Remove(_extra);
                _extra = null;
            }
            bool estabaEnExtra = TabExtra.IsChecked == true;
            TabExtra.IsChecked = false;
            TabExtra.Visibility = Visibility.Collapsed;
            MontarHerramientaPrincipal();
            if (estabaEnExtra && _extra != null) ShowTab(_extra);
            else ShowTab(_traffic);
        }

        private void OnSettings(object sender, RoutedEventArgs e)
        {
            var w = new SettingsWindow { Owner = this };
            w.Show();
        }

        private void OnDataUpdated()
        {
            if (App.IsShuttingDown) return;
            Dispatcher.BeginInvoke(() =>
            {
                if (App.IsShuttingDown) return;
                var (rx, tx, rxBps, txBps) = App.Traffic.GetSessionTotals();
                TxtRx.Text = Format.Bps(rxBps);
                TxtTx.Text = Format.Bps(txBps);
                TxtSession.Text = "Sesión: ↓" + Format.Bytes(rx) + "  ↑" + Format.Bytes(tx);
                TxtNetName.Text = App.Scanner.CurrentNetworkName();
            });
        }

        private void OnFirstConnection(Models.TrackedApp app, string host)
        {
            if (App.IsShuttingDown) return;
            Dispatcher.BeginInvoke(() =>
            {
                if (App.IsShuttingDown) return;
                var alert = new AlertWindow(app, host) { Owner = this };
                alert.Show();
            });
        }

        // La X no cierra el programa: lo manda a la bandeja, que es lo que uno espera de un
        // monitor. Salir de verdad se pide en el menú de la bandeja (o al apagar Windows, que
        // pide OnSessionEnding). Sin esta bandera, cerrar la ventana mataba el proceso entero.
        private bool _salirDeVerdad;

        internal void RequestExit() => _salirDeVerdad = true;

        protected override void OnClosing(CancelEventArgs e)
        {
            if (!_salirDeVerdad)
            {
                e.Cancel = true;
                Hide();
                App.Tray?.NotifyHidden();
                return;
            }

            App.Traffic.DataUpdated -= OnDataUpdated;
            App.Traffic.FirstConnectionDetected -= OnFirstConnection;
            App.Traffic.BlockedLaunchDetected -= OnBlockedLaunch;
            if (Application.Current is App app)
                app.PrepareForShutdown();
            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            if (Application.Current is App app)
                app.ShutdownCompletely();
            base.OnClosed(e);
        }

        internal void SelectProtectTab() => TabProtect.IsChecked = true;

        // Registros ya no es una pestaña: se abre la caja y salta a su ficha.
        internal void SelectLogTab() => AbrirHerramientas("registros");

        // Foto de la ventana principal sin abrirla de verdad: es la única manera de revisar la
        // barra de pestañas y la herramienta que ocupe su hueco sin instalar nada. Solo lee.
        //   MonitorRedPCJ.exe --main-preview archivo.png [traffic|protect|hardware|extra] [herramienta] [ms]
        public static void RenderPreview(string archivo, string pestana, string herramienta, int ms)
        {
            try { App.Settings.Current.HerramientaEnPrincipal = herramienta ?? ""; } catch { }
            var win = new MainWindow
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                ShowActivated = false,
                Left = -9000,
                Top = 100,
                Width = 1180,
                Height = 780,
            };
            win.Show();
            switch (pestana)
            {
                case "protect": win.TabProtect.IsChecked = true; break;
                case "hardware": win.TabHardware.IsChecked = true; break;
                case "extra":
                    if (win.TabExtra.Visibility == Visibility.Visible) win.TabExtra.IsChecked = true;
                    break;
            }

            var frame = new System.Windows.Threading.DispatcherFrame();
            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(Math.Max(1200, ms))
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
            // Sin Close: cerrar esta ventana apaga el programa entero, y el modo foto se cierra
            // solo justo después.
        }
    }

    public static class Format
    {
        public static string Bytes(long b)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double v = b; int u = 0;
            while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
            return v.ToString(u == 0 ? "0" : "0.#") + " " + units[u];
        }

        public static string Bps(double bps) => Bytes((long)bps) + "/s";
    }
}
