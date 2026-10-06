using System;
using System.Collections.Generic;
using System.Windows;
using MonitorRedPCJ.Models;
using MonitorRedPCJ.Services;

namespace MonitorRedPCJ.Views
{
    public partial class AlertWindow : Window
    {
        private readonly TrackedApp _app;
        private readonly string _host;

        // Posición compartida: la última alerta movida define dónde aparecen las siguientes
        private static System.Windows.Point? _lastPos;

        // Avisos abiertos ahora mismo, para no apilar dos ventanas del mismo programa
        // (un programa que se reabre en bucle llenaría la pantalla de copias).
        private static readonly HashSet<string> _open = new(StringComparer.OrdinalIgnoreCase);

        public static bool IsOpenFor(string exePath)
            => !string.IsNullOrEmpty(exePath) && _open.Contains(exePath);

        public AlertWindow(TrackedApp app, string host, bool manual = false)
        {
            InitializeComponent();
            _app = app;
            _host = host;
            TxtApp.Text = app.Name;
            // Sin host es un aviso por arranque: la app no pudo conectar porque el bloqueo
            // total le corta la salida, no porque haya alcanzado un servidor.
            if (manual)
            {
                // Programa añadido a mano desde Protección (los que corren elevados no se ven
                // solos, así que el usuario los elige con el explorador).
                TxtHost.Text = "Lo añadiste a mano: decide ahora si puede salir a internet.";
                TxtTitle.Text = "Aplicación añadida a la lista";
            }
            else if (string.IsNullOrEmpty(host))
            {
                // Aviso por apertura. Con el bloqueo total la app no pudo conectar porque
                // Windows le corta la salida; sin él es simplemente un programa nuevo.
                bool strict = App.Settings.Current.StrictMode;
                TxtHost.Text = strict
                    ? "Se abrió con el bloqueo total activo y no tiene permiso para salir"
                    : "Lo abriste por primera vez: decide si quieres que pueda salir a internet";
                TxtTitle.Text = strict ? "Aplicación nueva sin salida" : "Programa nuevo en la lista";

                // Cuántos siguen esperando detrás de este. Se dicen aquí, y no de veinte ventanas
                // a la vez, porque los avisos salen de uno en uno: hasta que no respondas (o
                // cierres) este, el siguiente no aparece.
                int quedan = App.Traffic.AvisosEnCola;
                if (quedan > 0)
                    TxtHost.Text += $" Quedan {quedan} {Plural(quedan)} más sin decidir; salen de uno " +
                        "en uno, a medida que vayas respondiendo.";
            }
            else
            {
                TxtHost.Text = "Se conecta a: " + host;
                TxtTitle.Text = "Nueva actividad de red";
            }
            TxtSub.Text = DateTime.Now.ToString("HH:mm  dd/MM/yyyy");

            if (!string.IsNullOrEmpty(app.ExePath)) _open.Add(app.ExePath);
            Closed += (s, e) =>
            {
                if (!string.IsNullOrEmpty(_app.ExePath)) _open.Remove(_app.ExePath);
            };

            // Cerrada sin pulsar Permitir/Bloquear/Solo esta vez: se recuerda, para que en el
            // modo normal no vuelva a salir la misma pregunta en cada arranque.
            Closing += (s, e) =>
            {
                if (!_answered) App.Traffic.NoteDismissed(_app);
            };

            // Permitir arrastrar la ventana con el mouse desde cualquier zona
            MouseLeftButtonDown += (s, e) =>
            {
                if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
                    DragMove();
            };

            LocationChanged += (s, e) =>
            {
                if (!IsLoaded) return;
                _lastPos = new System.Windows.Point(Left, Top);
                App.Settings.Current.AlertLeft = Left;
                App.Settings.Current.AlertTop = Top;
                SavePosThrottled();
            };

            Loaded += (s, e) =>
            {
                var wa = System.Windows.SystemParameters.WorkArea;
                var pos = _lastPos ??
                    (App.Settings.Current.AlertLeft.HasValue && App.Settings.Current.AlertTop.HasValue
                        ? new System.Windows.Point(App.Settings.Current.AlertLeft.Value,
                                                   App.Settings.Current.AlertTop.Value)
                        : (System.Windows.Point?)null);
                if (pos.HasValue)
                {
                    // Reubicar dentro de la pantalla por si cambió la resolución
                    Left = System.Math.Max(wa.Left, System.Math.Min(pos.Value.X, wa.Right - Width));
                    Top = System.Math.Max(wa.Top, System.Math.Min(pos.Value.Y, wa.Bottom - Height));
                }
                else
                {
                    Left = wa.Right - Width - 24;
                    Top = wa.Bottom - Height - 24;
                }
            };
        }

        private static DateTime _lastPosSave = DateTime.MinValue;
        private static void SavePosThrottled()
        {
            if ((DateTime.UtcNow - _lastPosSave).TotalSeconds < 1) return;
            _lastPosSave = DateTime.UtcNow;
            App.Settings.Save();
        }

        // ---------- Nombre y ruta completos ----------
        private bool _answered;
        private bool _detailsShown;
        private bool _detailsFilled;

        private void OnWhy(object sender, RoutedEventArgs e)
        {
            // Misma idea que los detalles: se despliega, la ventana crece y se recoloca.
            bool open = PanelWhy.Visibility != Visibility.Visible;
            PanelWhy.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            BtnWhy.Content = open ? "Ocultar la diferencia entre salida y entrada"
                                  : "¿Diferencia entre bloquear la salida y la entrada?";
            if (open)
            {
                Dispatcher.BeginInvoke(new Action(KeepOnScreen),
                    System.Windows.Threading.DispatcherPriority.Loaded);
            }
        }

        private void OnDetails(object sender, RoutedEventArgs e)
        {
            _detailsShown = !_detailsShown;
            PanelDetails.Visibility = _detailsShown ? Visibility.Visible : Visibility.Collapsed;
            BtnDetails.Content = _detailsShown ? "Ocultar detalles" : "Ver nombre y ruta completos";
            if (_detailsShown)
            {
                FillDetails();
                // La ventana crece hacia abajo: reacomodarla para que no se salga de la pantalla
                Dispatcher.BeginInvoke(new Action(KeepOnScreen), System.Windows.Threading.DispatcherPriority.Loaded);
            }
        }

        private void KeepOnScreen()
        {
            var wa = System.Windows.SystemParameters.WorkArea;
            UpdateLayout();
            double h = ActualHeight > 0 ? ActualHeight : Height;
            double top = System.Math.Min(Top, wa.Bottom - h);
            if (top < wa.Top) top = wa.Top;
            if (System.Math.Abs(top - Top) > 0.5) Top = top;
        }

        private void FillDetails()
        {
            if (_detailsFilled) return;
            _detailsFilled = true;

            string exe = _app.ExePath ?? "";
            TxtDetailName.Text = string.IsNullOrEmpty(_app.Name) ? "(sin nombre)" : _app.Name;
            TxtDetailPath.Text = string.IsNullOrEmpty(exe) ? "La aplicación ya no informa su ruta." : exe;

            var meta = new System.Text.StringBuilder();
            try
            {
                if (!string.IsNullOrEmpty(exe) && System.IO.File.Exists(exe))
                {
                    var vi = System.Diagnostics.FileVersionInfo.GetVersionInfo(exe);
                    var fi = new System.IO.FileInfo(exe);
                    void Line(string k, string v)
                    {
                        if (string.IsNullOrWhiteSpace(v)) return;
                        if (meta.Length > 0) meta.Append('\n');
                        meta.Append(k + "  " + v);
                    }
                    Line("Editor:   ", vi.CompanyName);
                    Line("Producto: ", vi.ProductName);
                    Line("Versión:  ", string.IsNullOrEmpty(vi.ProductVersion) ? vi.FileVersion : vi.ProductVersion);
                    Line("Tamaño:   ", HumanSize(fi.Length));
                    Line("Modificado: ", fi.LastWriteTime.ToString("dd/MM/yyyy HH:mm"));
                    BtnOpenFolder.IsEnabled = true;
                }
                else
                {
                    meta.Append("El archivo ya no existe en esa ruta (¿desinstalada o movida?).");
                    BtnOpenFolder.IsEnabled = false;
                }
            }
            catch (Exception ex)
            {
                meta.Append("No se pudieron leer los datos del archivo: " + ex.Message);
                BtnOpenFolder.IsEnabled = false;
            }
            TxtDetailMeta.Text = meta.ToString();
            TxtDetailMeta.Visibility = meta.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            FillRisk();
        }

        private static string HumanSize(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB" };
            double v = bytes;
            int i = 0;
            while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
            return (i == 0 ? v.ToString("0") : v.ToString("0.0")) + " " + units[i];
        }

        // Para decir «quedan 1 programa» / «quedan 9 programas» sin escribirlo dos veces.
        private static string Plural(int n) => n == 1 ? "programa" : "programas";

        // ---------- Análisis de riesgo ----------
        private RiskInfo? _risk;

        private void FillRisk()
        {
            try
            {
                _risk = AppRisk.Analyze(_app.ExePath);
                var r = _risk;

                TxtRiskLabel.Text = "Análisis: " + r.Label;
                TxtRiskWhat.Text = r.What;
                TxtRiskEffect.Text = "Si le quitas la salida: " + r.Effect;
                TxtRiskAdvice.Text = r.Advice;

                // Las dos tintas de la tarjeta de análisis salen de la paleta del tema en uso,
                // dichas por lo que significan (el fondo del chip y el acento del nivel). La
                // alerta es una ventana que nace y muere con cada aviso, así que no hace falta
                // que se suscriba al cambio de tema: la siguiente ya lo pide hecho.
                string bg = r.Level switch
                {
                    RiskLevel.EsencialRed => "B.ChipBadBg",
                    RiskLevel.SistemaWindows => "B.ChipWarnBg",
                    RiskLevel.Aplicacion => "B.ChipOkBg",
                    _ => "B.ChipNeutralBg"
                };
                string accent = r.Level switch
                {
                    RiskLevel.EsencialRed => "B.Coral",
                    RiskLevel.SistemaWindows => "B.Amber",
                    RiskLevel.Aplicacion => "B.Green",
                    _ => "B.Slate"
                };
                RiskCard.Background = (System.Windows.Media.Brush)FindResource(bg);
                TxtRiskLabel.Foreground = (System.Windows.Media.Brush)FindResource(accent);
            }
            catch (Exception ex)
            {
                RiskCard.Visibility = Visibility.Collapsed;
                TxtDetailMeta.Text = "El análisis no se pudo calcular: " + ex.Message;
                TxtDetailMeta.Visibility = Visibility.Visible;
            }
        }

        private void OnCopyPath(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!string.IsNullOrEmpty(_app.ExePath)) Clipboard.SetText(_app.ExePath);
                TxtDetailMeta.Text = "Ruta copiada al portapapeles.";
                TxtDetailMeta.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                TxtDetailMeta.Text = "No se pudo copiar: " + ex.Message;
                TxtDetailMeta.Visibility = Visibility.Visible;
            }
        }

        private void OnOpenFolder(object sender, RoutedEventArgs e)
        {
            string exe = _app.ExePath ?? "";
            try
            {
                if (string.IsNullOrEmpty(exe)) return;
                if (System.IO.File.Exists(exe))
                {
                    // explorer.exe con /select resalta el archivo concreto dentro de su carpeta
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = "/select,\"" + exe + "\"",
                        UseShellExecute = true
                    });
                }
                else
                {
                    string dir = System.IO.Path.GetDirectoryName(exe);
                    if (!string.IsNullOrEmpty(dir) && System.IO.Directory.Exists(dir))
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        { FileName = dir, UseShellExecute = true });
                }
            }
            catch { /* abrir el explorador no es crítico */ }
        }

        private async void OnAllow(object sender, RoutedEventArgs e)
        {
            _answered = true;
            BtnAllow.IsEnabled = BtnBlock.IsEnabled = false;
            App.Traffic.MarkKnown(_app);
            _app.OutAction = RuleAction.Allow;
            // SetDecision elige el mecanismo según el modo: regla de permiso con bloqueo
            // total, regla de permiso normal sin él.
            await System.Threading.Tasks.Task.Run(() =>
                App.Firewall.SetDecision(_app.ExePath, Direction.Out, Decision.Permitido));
            App.Events.Add(EventKind.RuleChanged, $"{_app.Name}: acceso permitido", _host, true);
            Close();
        }

        private async void OnBlock(object sender, RoutedEventArgs e)
        {
            // Antes de cortar la salida a un componente de Windows hay que avisar del riesgo:
            // bloquear el cliente DNS, por ejemplo, deja sin internet a todo el equipo.
            if (_risk == null) FillRisk();
            if (_risk != null && _risk.Level == RiskLevel.EsencialRed)
            {
                BtnAllow.IsEnabled = BtnBlock.IsEnabled = false;
                var answer = MessageBox.Show(
                    _app.Name + " es parte del sistema de red de Windows.\n\n" + _risk.Effect +
                    "\n\n" + _risk.Advice + "\n\n¿Seguro que quieres bloquear su salida?",
                    "Monitor de Red PCJ", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                    MessageBoxResult.No);
                BtnAllow.IsEnabled = BtnBlock.IsEnabled = true;
                if (answer != MessageBoxResult.Yes) return;
            }

            BtnAllow.IsEnabled = BtnBlock.IsEnabled = false;
            _answered = true;
            App.Traffic.MarkKnown(_app);

            // Crear la regla puede abrir la ventana de UAC: fuera del hilo de interfaz
            bool ok = await System.Threading.Tasks.Task.Run(() =>
                App.Firewall.SetDecision(_app.ExePath, Direction.Out, Decision.Bloqueado));

            if (ok)
            {
                _app.OutAction = RuleAction.Block;
            }
            App.Events.Add(EventKind.RuleChanged,
                ok ? $"Monitor de Red PCJ bloqueó la salida de {_app.Name}"
                   : $"Monitor de Red PCJ no pudo bloquear a {_app.Name} ({FirewallService.LastError})",
                _host, true);
            Close();
        }

        private void OnOnce(object sender, RoutedEventArgs e)
        {
            _answered = true;
            App.Traffic.MarkKnown(_app);
            App.Events.Add(EventKind.Info, $"{_app.Name}: permitida solo esta vez", _host, false);
            Close();
        }
    }
}
