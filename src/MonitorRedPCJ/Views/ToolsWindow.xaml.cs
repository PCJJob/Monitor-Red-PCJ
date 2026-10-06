using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MonitorRedPCJ.Services.Tools;
using MonitorRedPCJ.Views.Tools;

namespace MonitorRedPCJ.Views
{
    /// La caja de herramientas: a la izquierda las doce fichas, a la derecha la que esté
    /// elegida con su descripción, su guía de pasos, su interruptor y su pantalla viva.
    public partial class ToolsWindow : Window, IAnfitrion
    {
        // Una fila del listado de la izquierda.
        private sealed class Ficha : INotifyPropertyChanged
        {
            public event PropertyChangedEventHandler? PropertyChanged;

            public ToolDef Origen { get; }
            public string Nombre => Origen.Nombre;
            public string Lema => Origen.Lema;
            public string Glifo => Char(Origen.Glifo);

            // Los tres se calculan al pedirlos, no al construir la ficha: el color del mosaico
            // es el de la herramienta adaptado al tema, y el punto verde/gris sale de la
            // paleta. Al cambiar de tema se avisa con Revivir() y la lista se repinta sola,
            // porque van enlazados.
            public Brush Mosaico => Services.ThemeService.PincelDeIdentidad(Origen.Color);
            public Brush Punto => ToolCatalog.Activada(Origen.Id)
                ? (Brush)Application.Current.Resources["B.Green"]
                : (Brush)Application.Current.Resources["B.BarIdle"];

            public Ficha(ToolDef def) { Origen = def; }

            private static string Char(int codigo)
            {
                try { return char.ConvertFromUtf32(codigo); } catch { return ""; }
            }

            public void Avivar() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Punto)));

            // Aviso completo al cambiar de tema: también el mosaico, que el avivar de siempre
            // (encender/apagar una herramienta) no necesita tocar.
            public void Revivir()
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Punto)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Mosaico)));
            }
        }

        private readonly List<Ficha> _fichas = new();
        private readonly Dictionary<string, HerramientaPanel> _paneles = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<Paso> _pasos = new();
        private bool _cambiandoInterruptor;

        public static ToolsWindow? Abierta { get; private set; }

        private sealed class Paso
        {
            public string Num { get; set; } = "";
            public string Texto { get; set; } = "";
        }

        public ToolsWindow(string? iniciarEn = null)
        {
            InitializeComponent();

            foreach (var def in ToolCatalog.Todas) _fichas.Add(new Ficha(def));
            ToolList.ItemsSource = _fichas;

            Abierta = this;
            Closed += (s, e) => { if (Abierta == this) Abierta = null; };
            App.Tools.EstadoCambiado += () => Dispatcher.BeginInvoke(ActualizarContador);

            // El tema nuevo no llega solo a esta ventana. Las pantallas de las doce
            // herramientas se montan en código, y un elemento hecho en C# se queda con el
            // pincel que tenía al nacer: no tiene DynamicResource que lo avise. Aquí se tira
            // el panel en caché y se vuelve a montar con los colores del tema recién puesto.
            Services.ThemeService.Cambiado += OnTema;
            Closed += (s, e) => Services.ThemeService.Cambiado -= OnTema;

            var primera = _fichas.FirstOrDefault(f => string.Equals(f.Origen.Id, iniciarEn,
                                                                   StringComparison.OrdinalIgnoreCase))
                          ?? _fichas[0];
            ToolList.SelectedItem = primera;
            ActualizarContador();
        }

        // ---------- Listado de la izquierda ----------

        // Cambio de tema en caliente: se desmontan los paneles guardados (con su Soltar de
        // siempre, para que se desenganchen de los servicios), se reavivan los colores de las
        // fichas y se vuelve a montar la que se esté viendo.
        private void OnTema(string id)
        {
            foreach (var p in _paneles.Values)
            {
                try { p.SoltarPublico(); } catch { }
            }
            _paneles.Clear();
            foreach (var f in _fichas) f.Revivir();
            if (ToolList.SelectedItem is Ficha actual) Mostrar(actual);
        }

        private void OnSelect(object sender, SelectionChangedEventArgs e)
        {
            if (ToolList.SelectedItem is Ficha f) Mostrar(f);
        }

        private void ActualizarContador()
        {
            int nuevas = ToolCatalog.Nuevas.Count;
            int encendidas = ToolCatalog.Nuevas.Count(t => ToolCatalog.Activada(t.Id));
            TxtEncendidas.Text = encendidas + " de " + nuevas + " herramientas encendidas";
            foreach (var f in _fichas) f.Avivar();
        }

        // ---------- Ficha de la derecha ----------

        private void Mostrar(Ficha ficha)
        {
            var def = ficha.Origen;

            HeadIcon.Background = ficha.Mosaico;
            HeadGlyph.Text = ficha.Glifo;
            HeadName.Text = def.Nombre;
            HeadLema.Text = def.Lema;

            TxtQueHace.Text = def.QueHace;
            TxtConsumo.Text = def.Consumo;
            BoxConsumo.Visibility = string.IsNullOrEmpty(def.Consumo) ? Visibility.Collapsed : Visibility.Visible;

            _cambiandoInterruptor = true;
            HeadSwitch.IsChecked = ToolCatalog.Activada(def.Id);
            _cambiandoInterruptor = false;
            HeadSwitch.Visibility = def.EsExistente ? Visibility.Collapsed : Visibility.Visible;
            HeadSwitchText.Text = def.EsExistente ? "Siempre disponible"
                                         : HeadSwitch.IsChecked == true ? "Encendida" : "Apagada";
            HeadSwitchText.Foreground = HeadSwitch.IsChecked == true
                ? (Brush)FindResource("B.Green")
                : (Brush)FindResource("B.TextDim");

            _pasos.Clear();
            int n = 1;
            foreach (var p in def.Pasos) _pasos.Add(new Paso { Num = n++.ToString(), Texto = p });
            GuideList.ItemsSource = _pasos;
            TxtPasosN.Text = _pasos.Count == 0 ? "" : _pasos.Count + " pasos";

            if (!_paneles.TryGetValue(def.Id, out var panel))
            {
                panel = Herramientas.Crear(def.Id, this);
                _paneles[def.Id] = panel;
            }
            Live.Content = panel;
            try { panel.Refrescar(); } catch { }
        }

        private void OnSwitch(object sender, RoutedEventArgs e)
        {
            if (_cambiandoInterruptor) return;
            if (!(ToolList.SelectedItem is Ficha f)) return;
            bool encender = HeadSwitch.IsChecked == true;
            try
            {
                ToolCatalog.Cambiar(f.Origen.Id, encender);
            }
            catch { }
            HeadSwitchText.Text = encender ? "Encendida" : "Apagada";
            HeadSwitchText.Foreground = encender ? (Brush)FindResource("B.Green")
                                                 : (Brush)FindResource("B.TextDim");
            ActualizarContador();
            if (_paneles.TryGetValue(f.Origen.Id, out var p)) { try { p.Refrescar(); } catch { } }
        }

        private void OnGuideToggled(object sender, RoutedEventArgs e)
        {
            // La guía se guarda abierta o cerrada en la propia ventana: no hace falta
            // persistirla, pero sí dejar de ver el contador de pasos cuando está plegada.
            // El Expander lanza Expanded nada más aplicarse la plantilla, cuando los nombres
            // de la XAML todavía no están asignados: ahí no hay nada que ocultar.
            if (TxtPasosN == null) return;
            TxtPasosN.Visibility = GuideBox.IsExpanded ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OnClose(object sender, RoutedEventArgs e) => Close();

        // ---------- Lo que piden los paneles ----------

        public void IrAHerramienta(string id)
        {
            var f = _fichas.FirstOrDefault(x => string.Equals(x.Origen.Id, id, StringComparison.OrdinalIgnoreCase));
            if (f == null) return;
            ToolList.ScrollIntoView(f);
            ToolList.SelectedItem = f;
        }

        public void IrAProteccion(string buscar)
        {
            if (Application.Current.MainWindow is MainWindow w) w.IrAProteccion(buscar);
        }

        public void IrARecursos()
        {
            if (Application.Current.MainWindow is MainWindow w) w.IrARecursos();
        }

        // ---------- Comprobación de diseño ----------

        /// Monta la caja con las herramientas apagadas y la fotografía a PNG. Solo lee: no
        /// cambia el modo del firewall ni escribe reglas.
        ///   MonitorRedPCJ.exe --tools-preview archivo.png [id] [milisegundos]
        public static void RenderPreview(string archivo, string id, int ms)
        {
            var win = new ToolsWindow(string.IsNullOrEmpty(id) ? null : id)
            {
                // Sin dueño, CenterOwner lanza al mostrar: en la foto la ventana va fuera de pantalla.
                WindowStartupLocation = WindowStartupLocation.Manual,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                ShowActivated = false,
                Left = -9000,
                Top = 120,
                Width = 1120,
                Height = 760,
            };
            win.Show();

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
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(win);

            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            using (var fs = System.IO.File.Create(archivo)) enc.Save(fs);
            win.Close();
        }
    }
}
