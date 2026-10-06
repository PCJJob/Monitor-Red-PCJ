using System;
using System.Drawing;
using System.Windows;
using WinForms = System.Windows.Forms;

namespace MonitorRedPCJ.Services
{
    // Bandeja del sistema con menú en español e icono dibujado en tiempo de ejecución
    public class TrayService : IDisposable
    {
        private readonly Window _main;
        private readonly Application _app;
        private readonly WinForms.NotifyIcon _icon;
        private Views.TrayMenuBody? _body;
        private System.Windows.Controls.Primitives.Popup? _menu;
        private int _disposed;

        public TrayService(Window main, Application app)
        {
            _main = main;
            _app = app;
            _icon = new WinForms.NotifyIcon
            {
                Icon = BuildIcon(),
                Text = "Monitor de Red PCJ",
                Visible = true,
            };
            _icon.DoubleClick += (s, e) => Show();
            BuildMenu();
            // El incógnito también se puede mover desde la ventana o desde Configuración:
            // el texto de la bandeja tiene que seguirles el ritmo.
            Recording.Changed += UpdateText;
        }

        // El menú lo pinta WPF (Views/TrayMenuBody) y lo sostiene un Popup con transparencia,
        // colocado junto al puntero. Antes se usaba ContextMenuStrip, que es el menú gris de
        // Windows: sin esquinas suaves, sin iconos y con las líneas que lo cuadriculan. Y antes
        // de eso un ContextMenu, que además de lo mismo añadía el suyo: un marco y una raya
        // blanca por el borde, porque ese popup de Windows no admite transparencia.
        private void BuildMenu()
        {
            _body = new Views.TrayMenuBody();
            _menu = _body.HostPopup();
            _menu.PlacementTarget = _main;

            var acts = new Views.TrayMenuBody.Actions
            {
                ShowHide = () => Toggle(),
                // "Siesta" es lo contrario de tener los avisos puestos; se traduce aquí, que
                // en el resto del programa se habla de AlertsEnabled.
                Siesta = paused =>
                {
                    App.Settings.Current.AlertsEnabled = !paused;
                    App.Settings.Save();
                },
                Incognito = on =>
                {
                    // Si el estado ya coincide, no se vuelve a llamar: cada entrada en incógnito
                    // toma una foto del histórico, y repetirla pisaría la foto buena.
                    if (on == Recording.Incognito) return;
                    App.Settings.Current.IncognitoMode = on;
                    App.Settings.Save();
                    // Lo que hace de verdad: corta la escritura a disco en todos los históricos y,
                    // al volver, recupera lo que había antes de activarlo.
                    App.SetIncognito(on);
                    UpdateText();
                },
                Settings = () =>
                {
                    var w = new Views.SettingsWindow { Owner = _main };
                    w.Show();
                },
                Admin = () => RelaunchElevated(),
                Exit = () =>
                {
                    // Es la única salida voluntaria: avisa a la ventana para que la X no la
                    // intercepte mandándola de nuevo a la bandeja.
                    (_main as MainWindow)?.RequestExit();
                    if (_app is App app)
                        app.ShutdownCompletely();
                    else
                        _app.Shutdown();
                },
            };
            _body.Bind(acts);

            // Clic derecho = abrir el menú con el estado leído en ese momento.
            _icon.MouseUp += (s, e) =>
            {
                if (e.Button != WinForms.MouseButtons.Right) return;
                OpenMenu();
            };
        }

        private void OpenMenu()
        {
            if (_body == null || _menu == null) return;
            try
            {
                _body.RefreshState(_main.IsVisible && _main.WindowState != WindowState.Minimized,
                                   App.Settings.Current.AlertsEnabled == false,
                                   Recording.Incognito,
                                   Subtitle());
                _menu.IsOpen = true;
            }
            catch { }
        }

        // La línea fina bajo el nombre: qué red se está mirando y cuánto lleva contado la
        // sesión. En incógnito se antepone el aviso, que es el dato que importa ahí.
        private string Subtitle()
        {
            try
            {
                var (rx, tx, _, _) = App.Traffic.GetSessionTotals();
                string s = App.Scanner.CurrentNetworkName() + "  ·  ↓" + Format.Bytes(rx) +
                           "  ↑" + Format.Bytes(tx);
                return Recording.Incognito ? "incógnito  ·  " + s : s;
            }
            catch { return "Monitor de Red PCJ"; }
        }

        // El aviso de la primera vez que se cierra la ventana: sin él, uno cree que el programa
        // se ha cerrado y luego no entiende por qué sigue consumiendo. Solo sale una vez por
        // sesión; el resto se sobreentiende.
        private bool _hintShown;

        public void NotifyHidden()
        {
            if (_hintShown || App.IsShuttingDown) return;
            _hintShown = true;
            try
            {
                _icon.ShowBalloonTip(3500, "Sigo en la bandeja",
                    "Monitor de Red PCJ sigue mirando la red con la ventana cerrada. " +
                    "Doble clic en el icono para volver, o clic derecho y «Salir» para apagarlo.",
                    WinForms.ToolTipIcon.Info);
            }
            catch { }
        }

        /// Aviso flotante de las herramientas (cuota, guardián de la LAN, puertos nuevos…).
        /// No depende de que la ventana esté abierta: sale por la bandeja, que es donde PCJ
        /// vive cuando uno no le está mirando la pantalla.
        public void Toast(string titulo, string texto)
        {
            if (App.IsShuttingDown) return;
            try
            {
                _icon.ShowBalloonTip(5000, titulo, texto, WinForms.ToolTipIcon.Info);
            }
            catch { }
        }

        // El icono de la bandeja tiene que decir lo que está pasando: si no se registra, se nota.
        private void UpdateText()        {
            try
            {
                bool on = App.Settings.Current.IncognitoMode;
                _icon.Text = on ? "Monitor de Red PCJ — incógnito" : "Monitor de Red PCJ";
            }
            catch { }
        }

        // Acceso directo a la única forma fiable de tocar el firewall sin pelearse con UAC.
        private static void RelaunchElevated()
        {
            if (FirewallService.IsAdmin)
            {
                MessageBox.Show("Esta copia de Monitor de Red PCJ ya se ejecuta con permisos de administrador.",
                    "Monitor de Red PCJ", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            try
            {
                string self = Environment.ProcessPath ?? "";
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = self,
                    UseShellExecute = true,
                    Verb = "runas",
                });
            }
            catch { /* el usuario canceló la ventana de UAC */ }
        }

        public void Show()
        {
            if (App.IsShuttingDown) return;
            _main.Show();
            _main.WindowState = WindowState.Normal;
            _main.Activate();
        }

        public void Toggle()
        {
            if (App.IsShuttingDown) return;
            if (_main.IsVisible && _main.WindowState != WindowState.Minimized)
                _main.Hide();
            else Show();
        }

        // Icono propio: círculo con degradado teal→índigo y ondas de señal
        private static Icon BuildIcon()
        {
            using var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using var br = new System.Drawing.Drawing2D.LinearGradientBrush(
                    new Rectangle(0, 0, 32, 32), Color.FromArgb(15, 163, 163), Color.FromArgb(67, 97, 238), 45f);
                g.FillEllipse(br, 2, 2, 28, 28);
                using var w = new Pen(Color.White, 2.2f);
                g.DrawArc(w, 8, 10, 16, 16, 200, 140);
                g.DrawArc(w, 11, 13, 10, 10, 200, 140);
                g.FillEllipse(Brushes.White, 14, 15, 4, 4);
            }
            IntPtr h = bmp.GetHicon();
            return Icon.FromHandle(h);
        }

        public void Dispose()
        {
            if (System.Threading.Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _icon.Visible = false;
            Recording.Changed -= UpdateText;
            try { if (_menu != null) _menu.IsOpen = false; } catch { }
            try { if (_menu != null) _menu.Child = null; } catch { }
            _icon.ContextMenuStrip?.Dispose();
            _icon.Dispose();
        }
    }
}
