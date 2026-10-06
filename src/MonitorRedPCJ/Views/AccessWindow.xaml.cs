using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using MonitorRedPCJ.Models;
using MonitorRedPCJ.Services;

namespace MonitorRedPCJ.Views
{
    // Repaso previo al bloqueo total: el usuario marca qué aplicaciones podrán salir
    // a internet. Todo lo que quede sin marcar se corta de verdad, así que la lista
    // lleva el análisis de riesgo al lado de cada casilla.
    public partial class AccessWindow : Window
    {
        public class Row : System.ComponentModel.INotifyPropertyChanged
        {
            public string ExePath { get; set; } = "";
            public string Name { get; set; } = "";
            public string Folder { get; set; } = "";
            public bool Essential { get; set; }
            public string RiskLabel { get; set; } = "";
            public string RiskTip { get; set; } = "";
            public Brush RiskBg { get; set; } = Brushes.Transparent;
            public Brush RiskColor { get; set; } = Brushes.Black;
            public string Traffic { get; set; } = "";
            public TrackedApp? App { get; set; }

            private bool _allowed;
            public bool Allowed
            {
                get => _allowed;
                set { if (_allowed == value) return; _allowed = value; PropertyChanged?.Invoke(this, _args); }
            }

            public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
            private static readonly System.ComponentModel.PropertyChangedEventArgs _args =
                new System.ComponentModel.PropertyChangedEventArgs(nameof(Allowed));
        }

        private readonly List<Row> _rows = new();
        private string _filter = "";
        private bool _busy;
        private readonly bool _alreadyStrict;

        public AccessWindow()
        {
            InitializeComponent();
            _alreadyStrict = App.Settings.Current.StrictMode;
            if (_alreadyStrict)
            {
                // Con el corte ya activo esto deja de ser un asistente y pasa a ser el panel
                // de permisos: desmarcar una app le quita su regla PERMISO.
                Title = "Aplicaciones con salida permitida";
                TxtIntro.Text = "El bloqueo total está activo: Windows corta toda la salida y solo estas " +
                                "aplicaciones tienen conexión. Desmarcar una le quita su permiso al instante " +
                                "de aplicar; marcarla se lo devuelve.";
                BtnApply.Content = "Aplicar cambios";
                BtnApply.Style = (Style)FindResource("PrimaryButton");
            }
            Build();
        }

        private void Build()
        {
            // Inventario compartido con la pestaña de Protección: histórico de tráfico más todo
            // lo que está en marcha, aunque nunca haya conectado por estar cortado.
            foreach (var e in AppInventory.Collect())
                _rows.Add(MakeRow(e.ExePath, e.Name, e.App, e.Bps, e.Running));

            // Lo esencial y lo que ya se movía por la red aparece primero: es lo que hay que
            // revisar sí o sí antes de activar el corte.
            _rows.Sort((a, b) =>
            {
                int r = (b.Essential ? 1 : 0).CompareTo(a.Essential ? 1 : 0);
                if (r != 0) return r;
                r = (b.App != null ? 1 : 0).CompareTo(a.App != null ? 1 : 0);
                if (r != 0) return r;
                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });

            ApplyFilter();
            UpdateCount();
        }

        // El buscador solo cambia lo que se ve: las casillas siguen modificando la lista real.
        private void ApplyFilter()
        {
            List.ItemsSource = string.IsNullOrEmpty(_filter)
                ? _rows
                : _rows.Where(r => r.Name.ToLowerInvariant().Contains(_filter) ||
                                   r.Folder.ToLowerInvariant().Contains(_filter)).ToList();
        }

        // Igual que en la pestaña de Protección: al cambiar la lista filtrada se montan otra
        // vez todas las filas, y hacerlo en cada pulsación dejaba el cursor por detrás de lo
        // que se teclea. Se escribe primero y se recomponen al dejar de teclear.
        private System.Windows.Threading.DispatcherTimer? _searchTimer;

        private void OnSearch(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            string raw = (Search.Text ?? "").Trim();
            SearchHint.Visibility = raw.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            _filter = raw.ToLowerInvariant();

            if (_searchTimer == null)
            {
                _searchTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(220)
                };
                _searchTimer.Tick += (s, ev) => { _searchTimer!.Stop(); ApplyFilter(); };
            }
            _searchTimer.Stop();
            _searchTimer.Start();
        }

        private Row MakeRow(string exe, string name, TrackedApp? app, double bps, bool running)
        {
            var risk = AppRisk.Analyze(exe);
            var decision = App.Firewall.GetDecision(exe, Direction.Out);

            bool essential = risk.Level == RiskLevel.EsencialRed;

            // Se marcan solas las que sin ellas no habría internet (esenciales), el navegador
            // y lo que ya tuviera permiso en este modo. Lo demás lo decide el usuario.
            // Misma regla que usa el activar el corte de un toque, para no contradecirse.
            bool allowed = AppInventory.AutoAllowed(exe, decision, risk);

            var (bg, fg) = Colors(risk.Level);
            return new Row
            {
                ExePath = exe,
                Name = string.IsNullOrEmpty(name) ? Path.GetFileNameWithoutExtension(exe) : name,
                Folder = FolderOf(exe),
                Allowed = allowed,
                Essential = essential,
                RiskLabel = risk.Label,
                RiskTip = risk.What + "\n\n" + risk.Effect + "\n\n" + risk.Advice,
                RiskBg = bg,
                RiskColor = fg,
                Traffic = running ? Format.Bps(bps) + " · en ejecución" : "en descanso",
                App = app,
            };
        }

        private (Brush, Brush) Colors(RiskLevel level)
        {
            var fg = (Brush)FindResource("B.TextDim");
            Brush bg;
            switch (level)
            {
                case RiskLevel.EsencialRed:
                    fg = (Brush)FindResource("B.Coral"); bg = (Brush)FindResource("B.ChipBadBg");
                    break;
                case RiskLevel.SistemaWindows:
                    fg = (Brush)FindResource("B.Amber"); bg = (Brush)FindResource("B.ChipWarnBg");
                    break;
                case RiskLevel.Aplicacion:
                    fg = (Brush)FindResource("B.Green"); bg = (Brush)FindResource("B.ChipOkBg");
                    break;
                default:
                    bg = (Brush)FindResource("B.ChipNeutralBg");
                    break;
            }
            return (bg, fg);
        }

        private static string FolderOf(string exePath)
        {
            try
            {
                var dir = Path.GetDirectoryName(exePath);
                return string.IsNullOrEmpty(dir) ? exePath : dir.TrimEnd('\\');
            }
            catch { return exePath; }
        }

        private void OnCheckChanged(object sender, RoutedEventArgs e) => UpdateCount();

        private void UpdateCount()
        {
            int on = _rows.Count(r => r.Allowed);
            TxtCount.Text = $"{on} de {_rows.Count} aplicaciones podrán salir a internet";
        }

        private void OnEssential(object sender, RoutedEventArgs e)
        {
            foreach (var r in _rows) r.Allowed = r.Essential;
            UpdateCount();
        }

        private void OnAll(object sender, RoutedEventArgs e)
        {
            foreach (var r in _rows) r.Allowed = true;
            UpdateCount();
        }

        private void OnNone(object sender, RoutedEventArgs e)
        {
            foreach (var r in _rows) r.Allowed = false;
            UpdateCount();
        }

        private void OnCancel(object sender, RoutedEventArgs e) => Close();

        private async void OnApply(object sender, RoutedEventArgs e)
        {
            if (_busy) return;

            var allowed = _rows.Where(r => r.Allowed).Select(r => r.ExePath).ToList();

            if (allowed.Count == 0)
            {
                var sure = MessageBox.Show(
                    "No has marcado ninguna aplicación: Windows se quedará sin salida a internet, " +
                    "incluido el propio sistema. Solo se podrá deshacer desde este programa.\n\n" +
                    "¿Activar el bloqueo así?",
                    "Monitor de Red PCJ", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
                if (sure != MessageBoxResult.Yes) return;
            }

            _busy = true;
            BtnApply.IsEnabled = BtnEssential.IsEnabled = BtnAll.IsEnabled = BtnNone.IsEnabled = false;
            TxtCount.Text = "Aplicando… acepta la ventana de permisos de administrador";

            // Crea los permisos y después cambia la política de salida: se hace fuera del
            // hilo de interfaz porque la ventana de UAC puede tardar lo que tardes tú.
            bool ok = await Task.Run(() => App.Firewall.SetStrictMode(true, allowed));

            _busy = false;
            BtnEssential.IsEnabled = BtnAll.IsEnabled = BtnNone.IsEnabled = true;

            if (!ok)
            {
                BtnApply.IsEnabled = true;
                string why = FirewallService.LastError;
                App.Events.Add(EventKind.Info, "No se pudo activar el bloqueo total: " + why, "");
                MessageBox.Show("No se pudo activar el bloqueo total.\n\n" + why +
                        "\n\nHace falta aceptar la ventana de permisos de administrador; " +
                        "si tu cuenta no es de administrador, abre el programa con «Ejecutar como administrador».",
                    "Monitor de Red PCJ", MessageBoxButton.OK, MessageBoxImage.Warning);
                UpdateCount();
                return;
            }

            foreach (var r in _rows)
                if (r.App != null) App.Traffic.MarkKnown(r.App);

            App.Events.Add(EventKind.ProtectionToggled,
                _alreadyStrict
                    ? $"Permisos de salida actualizados — {allowed.Count} aplicaciones con salida permitida"
                    : $"Bloqueo total activado — {allowed.Count} aplicaciones con salida permitida",
                "", important: true);

            if (!string.IsNullOrEmpty(FirewallService.LastWarning))
            {
                App.Events.Add(EventKind.Info,
                    "Sin permiso de salida para: " + FirewallService.LastWarning, "");
                MessageBox.Show("El bloqueo total quedó activo, pero estas aplicaciones no pudieron " +
                        "recibir el permiso y por tanto no tendrán salida:\n\n" + FirewallService.LastWarning +
                        "\n\nSi te interesa alguna, márcala de nuevo con «Elegir apps con salida».",
                    "Monitor de Red PCJ", MessageBoxButton.OK, MessageBoxImage.Information);
            }

            DialogResult = true;
            Close();
        }
    }
}
