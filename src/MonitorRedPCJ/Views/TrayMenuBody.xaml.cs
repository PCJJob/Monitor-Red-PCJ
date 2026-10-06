using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using MonitorRedPCJ.Services;

namespace MonitorRedPCJ.Views
{
    // Menú de la bandeja dibujado con el tema de la aplicación, no con el menú gris de
    // Windows. Se pinta aquí (WPF) y se cuelga de un ContextMenu transparente: el icono de
    // la bandeja es de Windows Forms, que solo ofrece el menú viejo y cuadriculado.
    //
    // Todas las filas son ToggleButton: así las dos opciones con estado (Siesta e Incógnito)
    // y las acciones simples comparten diseño, y el estado se ve sin casillas ni bordes.
    public partial class TrayMenuBody : UserControl
    {
        public sealed class Actions
        {
            public Action? ShowHide;
            public Action<bool>? Siesta;        // true = en siesta (avisos pausados)
            public Action<bool>? Incognito;     // true = incógnito activado
            public Action? Settings;
            public Action? Admin;
            public Action? Exit;
        }

        private Actions _acts = new Actions();
        private Popup? _popup;

        public TrayMenuBody()
        {
            InitializeComponent();
        }

        public void Bind(Actions acts) => _acts = acts;

        // El menú se arma una vez y se abre muchas: cada apertura hay que volver a leerla,
        // si no la marca se queda contando lo de hace un rato.
        public void RefreshState(bool windowVisible, bool siesta, bool incognito, string subtitle)
        {
            RowShow.Content = windowVisible ? "Ocultar el monitor" : "Mostrar el monitor";
            // Ojo cuando se puede enseñar; pantalla tachada cuando está en pantalla.
            RowShow.Tag = Glyph(windowVisible ? 0xE8CD : 0xE890);   // pantalla tachada / ojo
            RowSiesta.IsChecked = siesta;
            RowIncognito.IsChecked = incognito;
            TxtSub.Text = subtitle;
            // Con el arranque elevado ya no hace falta: se deja ver, pero apagado, para que
            // no parezca que la opción desapareció.
            bool elevado = FirewallService.IsAdmin;
            RowAdmin.IsEnabled = !elevado;
            RowAdmin.ToolTip = elevado
                ? "Esta copia ya se ejecuta con permisos de administrador, así que el monitor " +
                  "puede aplicar reglas del firewall sin volver a preguntar."
                : "Reinicia el monitor con permisos de administrador para poder tocar el firewall.";
            RowShow.IsChecked = false;
            RowSettings.IsChecked = false;
            RowAdmin.IsChecked = false;
            RowExit.IsChecked = false;
        }

        // El soporte real del menú: una ventana emergente de WPF con transparencia, colgada de
        // la ventana principal y plantada donde está el puntero. Se usa en vez del menú de
        // Windows Forms (gris y cuadriculado) y en vez de un ContextMenu con plantilla propia:
        // el ContextMenu de fábrica pinta su marco y su raya blanca (la quejica de la 1.3.4),
        // y con plantilla propia se quedaba en 0x0, sin dibujar nada.
        public Popup HostPopup()
        {
            if (_popup != null) return _popup;
            _popup = new Popup
            {
                Child = this,
                AllowsTransparency = true,
                Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint,
                StaysOpen = false,
                Focusable = true,
            };
            _popup.Opened += (s, e) =>
            {
                if (!_autoCerrar) return;
                _fuera = 0;
                // Se arma un ciclo despues de que la ventana del popup exista y este colocada;
                // abriendo aqui mismo todavia no hay ventana que enganchar.
                Dispatcher.BeginInvoke(new Action(Armar),
                    System.Windows.Threading.DispatcherPriority.ContextIdle);
            };
            _popup.Closed += (s, e) => Desarmar();
            return _popup;
        }

        private void Close()
        {
            if (_popup != null) _popup.IsOpen = false;
        }

        // ---- Que el menu se retire solo ---------------------------------------------
        //
        // StaysOpen=false no bastaba: ese mecanismo de WPF cierra el popup cuando se hace clic
        // fuera, pero solo si el popup llego a ser la ventana activa. Como este menu se abre
        // desde la bandeja, con la ventana principal oculta, nunca recibia la activacion y se
        // quedaba plantado en pantalla estorbando aunque uno hiciera otra cosa. Se cubren las
        // dos formas de perder el interes:
        //   1) al abrirse se pone el popup en primer plano y se escucha WM_KILLFOCUS: un clic en
        //      cualquier otro sitio lo cierra de golpe (y la tecla Escape tambien);
        //   2) un vigia mira el puntero cada 100 ms: si sale del recuadro se retira. El recuadro
        //      lleva 36 px de holgura, porque el menu se planta justo debajo del puntero y sin
        //      margen el propio borde contaria como "fuera" y se cerraria al abrirse.
        private System.Windows.Threading.DispatcherTimer? _vigia;
        private System.Windows.Interop.HwndSource? _fuente;
        private int _fuera;
        private bool _autoCerrar = true;
        private string? _parte;          // solo en el modo de comprobacion: ruta del log
        private const int Holgura = 36;

        private void Armar()
        {
            try
            {
                if (_popup == null || !_popup.IsOpen) return;

                _fuente = PresentationSource.FromVisual(this) as System.Windows.Interop.HwndSource;
                if (_fuente != null)
                {
                    _fuente.AddHook(WndProc);
                    SetForegroundWindow(_fuente.Handle);
                }

                _fuera = 0;
                _vigia = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(100),
                };
                _vigia.Tick += OnVigia;
                _vigia.Start();
                Nota("armado  ventana=" + (_fuente != null ? "si" : "NO"));
            }
            catch (Exception ex) { Nota("armar roto: " + ex.Message); }
        }

        private void Desarmar()
        {
            try { if (_vigia != null) { _vigia.Stop(); _vigia.Tick -= OnVigia; } } catch { }
            _vigia = null;
            try { if (_fuente != null) _fuente.RemoveHook(WndProc); } catch { }
            _fuente = null;
            _fuera = 0;
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wparam, IntPtr lparam, ref bool handled)
        {
            const int WmKillFocus = 0x0008, WmKeyDown = 0x0100, VkEscape = 0x1B;
            if (_popup != null && _popup.IsOpen)
            {
                if (msg == WmKillFocus) { Nota("foco perdido -> cerrar"); Retirarse(); }
                else if (msg == WmKeyDown && (wparam.ToInt64() & 0xFFFF) == VkEscape)
                {
                    Nota("escape -> cerrar");
                    Close();
                }
            }
            return IntPtr.Zero;
        }

        private void OnVigia(object? sender, EventArgs e)
        {
            if (_popup == null || !_popup.IsOpen) { Desarmar(); return; }
            if (!RectPantalla(out Rect r)) return;
            if (!GetCursorPos(out POINT p)) return;

            bool dentro = p.X >= r.X - Holgura && p.X <= r.X + r.Width + Holgura &&
                          p.Y >= r.Y - Holgura && p.Y <= r.Y + r.Height + Holgura;
            if (dentro)
            {
                if (_fuera != 0) Nota("el puntero vuelve dentro, fuera=" + _fuera);
                _fuera = 0;
                return;
            }
            _fuera++;
            Nota("fuera=" + _fuera + "  puntero=" + p.X + "," + p.Y + "  recuadro=" +
                 (int)r.X + "," + (int)r.Y + " " + (int)r.Width + "x" + (int)r.Height);

            // Con un boton pulsado fuera no se espera a los tres ciclos: se retira al momento.
            bool boton = (GetAsyncKeyState(VkLButton) & 0x8000) != 0 ||
                         (GetAsyncKeyState(VkRButton) & 0x8000) != 0;
            if (_fuera >= 3 || (boton && _fuera >= 1)) Retirarse();
        }

        private void Retirarse()
        {
            Desarmar();
            Close();
        }

        // El recuadro del menu en pixeles reales de pantalla. La ventana del popup mide justo lo
        // que mide este control (con los 18 px de sombra dentro), asi que GetWindowRect da ya el
        // area que hay que vigilar, en las mismas unidades que GetCursorPos.
        private bool RectPantalla(out Rect r)
        {
            r = Rect.Empty;
            IntPtr h = _fuente?.Handle ?? IntPtr.Zero;
            if (h == IntPtr.Zero)
                h = (PresentationSource.FromVisual(this) as System.Windows.Interop.HwndSource)?.Handle
                    ?? IntPtr.Zero;
            if (h == IntPtr.Zero || !GetWindowRect(h, out RECT w)) return false;
            r = new Rect(w.L, w.T, w.R - w.L, w.B - w.T);
            return r.Width > 10 && r.Height > 10;
        }

        private void Nota(string s)
        {
            if (_parte == null) return;
            try { System.IO.File.AppendAllText(_parte, "\n  " + s); } catch { }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT p);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vk);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr h);

        private const int VkLButton = 0x01, VkRButton = 0x02;

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        // Las filas de acción no guardan estado: el ToggleButton se acaba de marcar él solo
        // al hacer clic, y hay que devolverlo.
        private static void Untoggle(object sender)
        {
            if (sender is ToggleButton tb) tb.IsChecked = false;
        }

        // Los glifos van por código de punto: el área privada no sobrevive a ciertos
        // editores y acaba saliendo como una interrogante.
        private static string Glyph(int code) => ((char)code).ToString();

        private void OnShow(object sender, RoutedEventArgs e)
        {
            Untoggle(sender);
            Close();
            _acts.ShowHide?.Invoke();
        }

        private void OnSiesta(object sender, RoutedEventArgs e)
        {
            bool paused = RowSiesta.IsChecked == true;
            Close();
            _acts.Siesta?.Invoke(paused);
        }

        private void OnIncognito(object sender, RoutedEventArgs e)
        {
            bool on = RowIncognito.IsChecked == true;
            Close();
            _acts.Incognito?.Invoke(on);
        }

        private void OnSettings(object sender, RoutedEventArgs e)
        {
            Untoggle(sender);
            Close();
            _acts.Settings?.Invoke();
        }

        private void OnAdmin(object sender, RoutedEventArgs e)
        {
            Untoggle(sender);
            Close();
            _acts.Admin?.Invoke();
        }

        // Segundo modo de comprobación: abre el menú de verdad (la ventana emergente con su
        // transparencia) durante unos milisegundos y deja un parte en %TEMP%. RenderPreview pinta
        // el control, pero solo el popup de Windows dice si sale marco, raya blanca o esquinas
        // recortadas.
        //
        // Con "auto" o "dentro" se comprueba además el retiro solo:
        //   auto   → el menú se planta lejos del puntero: el vigía debe cerrarlo (~300 ms).
        //   dentro → el menú se planta justo donde está el puntero: debe seguir abierto al final.
        public static void ShowPreview(int ms, string modo, int px, int py)
        {
            string log = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pcj-menu-show.log");
            AppDomain.CurrentDomain.UnhandledException += (s, ev) =>
            {
                try { System.IO.File.AppendAllText(log, "\n" + ((ev.ExceptionObject as Exception)?.ToString() ?? "?")); } catch { }
            };
            try
            {
                // Ventanita de anclaje: el popup necesita una ventana de la que colgarse, y en la
                // comprobación no hay ventana principal. Va abajo a la izquierda y el menú se
                // coloca arriba con un desplazamiento negativo.
                var ancla = new Window
                {
                    Width = 1,
                    Height = 1,
                    Left = 100,
                    Top = 900,
                    WindowStyle = WindowStyle.None,
                    ResizeMode = ResizeMode.NoResize,
                    ShowInTaskbar = false,
                    Topmost = true,
                    AllowsTransparency = true,
                    Background = Brushes.Transparent,
                };
                ancla.Show();
                ancla.Activate();

                var body = new TrayMenuBody();
                body.RefreshState(false, false, true, "Casa  ·  ↓1,2 GB  ↑340 MB");
                bool vigia = modo == "auto" || modo == "dentro";
                body._autoCerrar = vigia;
                if (vigia) body._parte = log;
                var popup = body.HostPopup();
                popup.PlacementTarget = ancla;
                if (modo == "dentro")
                {
                    // Mismo sitio real que vería el autor: el recuadro debajo del puntero. Las
                    // coordenadas vienen en pixeles de pantalla y los desplazamientos de WPF van
                    // en unidades independientes del DPI, así que se convierten con la escala.
                    var dpi = VisualTreeHelper.GetDpi(ancla);
                    popup.Placement = System.Windows.Controls.Primitives.PlacementMode.Absolute;
                    popup.HorizontalOffset = px / Math.Max(0.25, dpi.DpiScaleX);
                    popup.VerticalOffset = py / Math.Max(0.25, dpi.DpiScaleY);
                }
                else
                {
                    popup.HorizontalOffset = 1000;
                    popup.VerticalOffset = -760;
                }
                popup.StaysOpen = !vigia;   // sin vigía, que no lo cierre un clic de fuera durante la foto
                popup.IsOpen = true;
                System.IO.File.WriteAllText(log, "popup abierto  modo=" + (modo == "" ? "foto" : modo));

                var tMedir = new System.Windows.Threading.DispatcherTimer { Interval = System.TimeSpan.FromMilliseconds(1500) };
                tMedir.Tick += (s, e) =>
                {
                    tMedir.Stop();
                    try
                    {
                        var parte = new System.Text.StringBuilder();
                        parte.Append("\ntarjeta=" + body.ActualWidth + "x" + body.ActualHeight);
                        parte.Append("  estado=" + (popup.IsOpen ? "abierto" : "CERRADO"));
                        int pid = System.Diagnostics.Process.GetCurrentProcess().Id;
                        Rect? caja = null;
                        EnumWindows((h, lp) =>
                        {
                            GetWindowThreadProcessId(h, out int p);
                            if (p != pid || !IsWindowVisible(h)) return true;
                            if (!GetWindowRect(h, out RECT r)) return true;
                            int w = r.R - r.L, alto = r.B - r.T;
                            parte.Append("\n  " + Clase(h) + " " + w + "x" + alto + "@" + r.L + "," + r.T);
                            // La ventana del popup es la que mide lo que mide la tarjeta; la de
                            // anclaje es un punto y la del resto de la aplicación no existe aquí.
                            if (w > 120 && alto > 120) caja = new Rect(r.L, r.T, w, alto);
                            return true;
                        }, IntPtr.Zero);
                        System.IO.File.AppendAllText(log, parte.ToString());
                        if (caja != null)
                            GuardarZona(new Rect(caja.Value.X - 50, caja.Value.Y - 50,
                                                 caja.Value.Width + 100, caja.Value.Height + 100),
                                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pcj-menu-show.png"));
                    }
                    catch (Exception ex) { try { System.IO.File.AppendAllText(log, "\nparte roto: " + ex.Message); } catch { } }
                };
                tMedir.Start();

                var t = new System.Windows.Threading.DispatcherTimer { Interval = System.TimeSpan.FromMilliseconds(ms) };
                t.Tick += (s, e) =>
                {
                    t.Stop();
                    Application.Current.Shutdown();
                };
                t.Start();
            }
            catch (Exception ex)
            {
                try { System.IO.File.AppendAllText(log, "\n" + ex); } catch { }
                Environment.Exit(1);
            }
        }

        // Foto de un trozo de pantalla (para el parte de comprobación), recortada a lo que hay
        // de escritorio.
        private static void GuardarZona(Rect r, string ruta)
        {
            var va = System.Windows.SystemParameters.WorkArea;
            double x = Math.Max(0, r.X), y = Math.Max(0, r.Y);
            double x1 = Math.Min(va.Right, r.X + r.Width);
            double y1 = Math.Min(va.Bottom, r.Y + r.Height);
            int w = (int)(x1 - x), h = (int)(y1 - y);
            if (w < 20 || h < 20) return;
            var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
                g.CopyFromScreen((int)x, (int)y, 0, 0, new System.Drawing.Size(w, h));
            bmp.Save(ruta, System.Drawing.Imaging.ImageFormat.Png);
            bmp.Dispose();
        }

        // ---- Ventanales del parte de comprobación (solo los usa ShowPreview) ----
        private delegate bool EnumProc(IntPtr h, IntPtr lp);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumProc cb, IntPtr lp);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr h);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int GetWindowThreadProcessId(IntPtr h, out int pid);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr h, out RECT r);

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int max);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct RECT { public int L, T, R, B; }

        private static string Clase(IntPtr h)
        {
            var sb = new System.Text.StringBuilder(190);
            try { GetClassName(h, sb, 189); } catch { }
            string c = sb.ToString();
            int i = c.IndexOf(';');
            return i > 0 ? c.Substring(0, i) : c;
        }

        private void OnExit(object sender, RoutedEventArgs e)
        {
            Untoggle(sender);
            Close();
            _acts.Exit?.Invoke();
        }

        // Modo de comprobación: pinta el menú a un PNG sin abrir la bandeja. Sirve para ver el
        // diseño de verdad (colores, iconos, esquinas) antes de instalar nada.
        //   MonitorRedPCJ.exe --menu-preview C:\ruta\menu.png
        public static void RenderPreview(string path, bool windowVisible, bool siesta, bool incognito)
        {
            var body = new TrayMenuBody();
            body.RefreshState(windowVisible, siesta, incognito, "Casa  ·  ↓1,2 GB  ↑340 MB");

            // El fondo del recorte sigue la paleta pedida: sobre Nocturna un fondo claro
            // hacía ver el menú con un borde que en la bandeja real no existe.
            var fondo = System.Windows.Application.Current.TryFindResource("B.Bg")
                        as System.Windows.Media.Brush
                        ?? new SolidColorBrush(Color.FromRgb(0xED, 0xF3, 0xF8));
            var host = new Border { Background = fondo };
            host.Child = body;
            host.Measure(new Size(640, 640));
            var sz = body.DesiredSize;
            host.Width = sz.Width; host.Height = sz.Height;
            host.Arrange(new Rect(new Point(0, 0), sz));
            host.UpdateLayout();

            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                (int)System.Math.Ceiling(sz.Width), (int)System.Math.Ceiling(sz.Height),
                96, 96, PixelFormats.Pbgra32);
            rtb.Render(host);

            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            using var fs = System.IO.File.Create(path);
            enc.Save(fs);
        }
    }
}
