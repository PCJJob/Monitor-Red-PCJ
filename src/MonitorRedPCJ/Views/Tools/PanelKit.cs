using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace MonitorRedPCJ.Views.Tools
{
    // ============================================================================
    //  Piezas sueltas con las que se montan las pantallas de las herramientas.
    //
    //  Están hechas en código y no en XAML por un motivo práctico: son catorce
    //  pantallas distintas que comparten el mismo esqueleto (título, botones,
    //  tabla, pie). Un ControlTemplate por pantalla sería cuatrocientas líneas de
    //  XAML repetido.
    //
    //  Desde la 1.5.0 los pinceles ya no son colores escritos aquí: son las fichas de
    //  la paleta del tema en uso. Se piden CADA VEZ que se construye un elemento
    //  (por eso son propiedades y no campos: un campo se quedaría con el pincel del
    //  tema que había al arrancar). Como estas pantallas se montan en código, no se
    //  repintan solas al cambiar de tema —eso solo lo hace el XAML con su
    //  DynamicResource—, y por eso la ventana de herramientas reconstruye el panel
    //  que esté abierto cuando ThemeService avisa.
    // ============================================================================

    internal static class Kit
    {
        public static SolidColorBrush P(uint argb)
        {
            var b = new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16),
                                                      (byte)(argb >> 8), (byte)argb));
            b.Freeze();
            return b;
        }

        // La ficha de la paleta, con el color de siempre como red de seguridad por si se
        // llamara antes de que la aplicación tenga recursos montados.
        private static SolidColorBrush Tinta(string clave, uint respaldo)
            => Application.Current?.Resources[clave] as SolidColorBrush ?? P(respaldo);

        public static FontFamily Iconos
            => Application.Current?.Resources["F.Icon"] as FontFamily ?? new("Segoe Fluent Icons, Segoe MDL2 Assets");
        public static FontFamily Main
            => Application.Current?.Resources["F.Main"] as FontFamily ?? new("Segoe UI Variable Text, Segoe UI");

        public static SolidColorBrush Texto => Tinta("B.Text", 0xFF16242E);
        public static SolidColorBrush Dim => Tinta("B.TextDim", 0xFF677986);
        public static SolidColorBrush Muy => Tinta("B.TextFaint", 0xFF8A9BA8);
        public static SolidColorBrush Teal => Tinta("B.Teal", 0xFF0FA3A3);
        public static SolidColorBrush Indigo => Tinta("B.Indigo", 0xFF4361EE);
        public static SolidColorBrush Coral => Tinta("B.Coral", 0xFFD9534F);
        public static SolidColorBrush Ambar => Tinta("B.ChipWarnFg", 0xFFB4770E);
        public static SolidColorBrush Verde => Tinta("B.ChipOkFg", 0xFF1E8E63);
        public static SolidColorBrush Linea => Tinta("B.Line", 0xFFDCE6EE);
        public static SolidColorBrush FondoSuave => Tinta("B.Hover", 0xFFF4F8FC);
        public static SolidColorBrush FondoHover => Tinta("B.Pressed", 0xFFE9F2FA);

        public static TextBlock TextoDe(string s, double tam, Brush pincel, bool seminegra = false)
        {
            var t = new TextBlock
            {
                Text = s ?? "",
                FontSize = tam,
                Foreground = pincel,
                FontFamily = Main,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (seminegra) t.FontWeight = FontWeights.SemiBold;
            return t;
        }

        public static TextBlock Glifo(string codigo, double tam, Brush pincel)
        {
            var t = new TextBlock
            {
                Text = codigo,
                FontFamily = Iconos,
                FontSize = tam,
                Foreground = pincel,
                VerticalAlignment = VerticalAlignment.Center,
            };
            return t;
        }

        /// Botoncillo redondeado. Es un Border con ratón encima, no un Button con
        /// ControlTemplate: así el aspecto es idéntico en las catorce pantallas y no
        /// hay estilo de Windows asomando por ningún lado.
        public static Border Chip(string texto, Action? clic, string? glifo = null,
                                  Brush? fondo = null, Brush? tinta = null, double tam = 12,
                                  string? tip = null)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(11, 6, 11, 6) };
            if (glifo != null)
            {
                sp.Children.Add(new TextBlock
                {
                    Text = glifo,
                    FontFamily = Iconos,
                    FontSize = tam - 1,
                    Foreground = tinta ?? Kit.Texto,
                    VerticalAlignment = VerticalAlignment.Center,
                });
                sp.Children.Add(new Border { Width = 6 });
            }
            sp.Children.Add(new TextBlock
            {
                Text = texto,
                FontSize = tam,
                FontWeight = FontWeights.SemiBold,
                Foreground = tinta ?? Kit.Texto,
                VerticalAlignment = VerticalAlignment.Center,
            });

            var bd = new Border
            {
                Child = sp,
                CornerRadius = new CornerRadius(9),
                Background = fondo ?? FondoSuave,
                BorderBrush = Linea,
                BorderThickness = new Thickness(1),
                Cursor = clic == null ? Cursors.Arrow : Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
            };
            if (tip != null) bd.ToolTip = tip;
            var baseFondo = bd.Background;
            if (clic != null)
            {
                bd.MouseEnter += (s, e) => bd.Background = FondoHover;
                bd.MouseLeave += (s, e) => bd.Background = baseFondo;
                bd.MouseLeftButtonUp += (s, e) => { try { clic(); } catch { } };
            }
            return bd;
        }

        /// Campo de texto pequeño con su etiqueta encima, para cuota, día de reinicio…
        public static StackPanel Campo(string etiqueta, string valor, double ancho = 90,
                                       Action<string>? alCambiar = null)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
            sp.Children.Add(TextoDe(etiqueta, 11, Dim));
            var caja = new TextBox
            {
                Text = valor ?? "",
                Width = ancho,
                FontSize = 13,
                FontFamily = Main,
                Padding = new Thickness(8, 5, 8, 5),
                BorderBrush = Linea,
                BorderThickness = new Thickness(1),
                Background = P(0xFFFFFFFF),
                Foreground = Texto,
                Margin = new Thickness(0, 3, 0, 0),
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            caja.GotKeyboardFocus += (s, e) => caja.SelectAll();
            if (alCambiar != null)
            {
                caja.LostFocus += (s, e) => alCambiar(caja.Text);
                caja.KeyDown += (s, e) => { if (e.Key == Key.Enter) alCambiar(caja.Text); };
            }
            sp.Children.Add(caja);
            return sp;
        }

        public static CheckBox Casilla(string texto, bool marcado, Action<bool> alCambiar,
                                       string? tip = null)
        {
            var ch = new CheckBox
            {
                Content = texto,
                IsChecked = marcado,
                FontSize = 12.5,
                FontFamily = Main,
                Foreground = Texto,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0),
                ToolTip = tip,
            };
            ch.Checked += (s, e) => alCambiar(true);
            ch.Unchecked += (s, e) => alCambiar(false);
            return ch;
        }

        public static string Num(double v) => v.ToString("0.#", new CultureInfo("es-ES"));
        public static string Bytes(long b) => MonitorRedPCJ.Format.Bytes(b);

        public static string Hora(DateTime utc)
        {
            if (utc == DateTime.MinValue) return "nunca";
            var l = utc.ToLocalTime();
            return l.ToString("dd MMM · HH:mm", new CultureInfo("es-ES"));
        }

        public static string HaciaCuanto(DateTime utc)
        {
            if (utc == DateTime.MinValue) return "todavía no";
            var d = DateTime.Now - utc.ToLocalTime();
            if (d.TotalSeconds < 60) return "hace " + Math.Max(1, (int)d.TotalSeconds) + " s";
            if (d.TotalMinutes < 60) return "hace " + (int)d.TotalMinutes + " min";
            if (d.TotalHours < 24) return "hace " + (int)d.TotalHours + " h";
            return "hace " + (int)d.TotalDays + " días";
        }
    }

    // ----------------------------------------------------------------------------

    internal sealed class Columna
    {
        public string Titulo;
        public double Ancho;
        public bool Estrella;
        public bool Derecha;

        public Columna(string titulo, double ancho = 0, bool derecha = false, bool estrella = false)
        {
            Titulo = titulo; Ancho = ancho; Derecha = derecha; Estrella = estrella;
        }

        public static Columna Fija(string t, double w, bool der = false) => new(t, w, der);
        public static Columna Libre(string t) => new(t, 0, false, true);
    }

    // ----------------------------------------------------------------------------

    internal sealed class Fila
    {
        public string[] Celdas;
        public string? Sub;                    // segunda línea bajo la primera celda
        public string? Chip;
        public Brush ChipFondo = Kit.FondoSuave;
        public Brush ChipTinta = Kit.Dim;
        public Brush? Tinte;                   // color de la primera celda
        public bool EsGrupo;
        public string? Tip;
        public CheckBox? Casilla;
        public List<(string Texto, Action Accion, string? Tip)> Botones = new();

        public Fila(params string[] celdas) { Celdas = celdas ?? new[] { "" }; }
    }

    // ----------------------------------------------------------------------------

    /// Esqueleto de una pantalla de herramienta: título con sus botones, kpis, contenido
    /// libre, tabla y pie. Las catorce herramientas se pintan con esto.
    internal sealed class PanelVivo : StackPanel
    {
        private readonly TextBlock _titulo;
        private readonly TextBlock _ayuda;
        private readonly WrapPanel _barra;
        private readonly StackPanel _kpis;
        public readonly StackPanel Extra;
        private readonly Border _cajaTabla;
        private readonly Grid _cab;
        private readonly StackPanel _filas;
        private readonly TextBlock _pie;
        private Columna[] _cols = Array.Empty<Columna>();
        private Border? _banda;
        private readonly StackPanel _cabRaiz;
        private readonly Grid _cabFila;
        private ScrollViewer _lista = null!;
        private bool _barraDebajo;

        // Para la comprobación automática (--tools-check): cuántas filas salió pintar la
        // herramienta y qué dice su pie, sin exponer los controles al resto del programa.
        public int FilasPintadas => _filas.Children.Count;
        public int BotonesPintados => _barra.Children.Count;
        public int KpisPintadas => _kpis.Children.Count;
        public int ExtrasPintadas => Extra.Children.Count;
        public string PiePintado => _pie.Text ?? "";
        public string TituloPintado => _titulo.Text ?? "";

        public PanelVivo(string titulo, string ayuda = "")
        {
            Orientation = Orientation.Vertical;
            Margin = new Thickness(0, 0, 0, 4);

            _cabFila = new Grid();
            _cabFila.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _cabFila.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var textos = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            _titulo = Kit.TextoDe(titulo, 14, Kit.Texto, true);
            _titulo.FontWeight = FontWeights.SemiBold;
            textos.Children.Add(_titulo);
            _ayuda = new TextBlock
            {
                Text = ayuda,
                FontSize = 11.5,
                Foreground = Kit.Dim,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0),
                MaxWidth = 640,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            if (ayuda.Length > 0) textos.Children.Add(_ayuda);
            _cabFila.Children.Add(textos);
            _cabRaiz = new StackPanel { Orientation = Orientation.Vertical };
            _cabRaiz.Children.Add(_cabFila);
            var cab = _cabRaiz;

            // La barra va en un WrapPanel y en su propia columna: si caben, una línea; si no,
            // dos. Con un StackPanel horizontal el último botón se salía del borde de la ventana.
            _barra = new WrapPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 560,
            };
            Grid.SetColumn(_barra, 1);
            _cabFila.Children.Add(_barra);
            Children.Add(cab);

            _kpis = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
            Children.Add(_kpis);

            Extra = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
            Children.Add(Extra);

            _cab = new Grid { Margin = new Thickness(10, 7, 10, 7) };
            _filas = new StackPanel();
            _cajaTabla = new Border
            {
                Margin = new Thickness(0, 12, 0, 0),
                Background = Kit.P(0xFFFFFFFF),
                BorderBrush = Kit.Linea,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(11),
                Child = new StackPanel(),
            };
            var dentro = (StackPanel)_cajaTabla.Child;
            _banda = new Border
            {
                Background = Kit.FondoSuave,
                CornerRadius = new CornerRadius(10, 10, 0, 0),
                Child = _cab,
            };
            dentro.Children.Add(_banda);
            _lista = new ScrollViewer
            {
                Content = _filas,
                MaxHeight = 380,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(2, 2, 2, 4),
            };
            dentro.Children.Add(_lista);
            // La banda de cabeceras va fuera del ScrollViewer: cuando asoma la barra vertical,
            // las filas se estrechan y las columnas dejarían de cuadrar con sus títulos.
            _filas.SizeChanged += (s, e) => AlinearCabecera();
            _lista.SizeChanged += (s, e) => AlinearCabecera();
            Children.Add(_cajaTabla);

            _pie = new TextBlock
            {
                FontSize = 11.5,
                Foreground = Kit.Dim,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(2, 9, 2, 0),
            };
            Children.Add(_pie);
        }

        // Reserva el hueco de la barra vertical en la banda de títulos, así cada cabecera cae
        // sobre su columna aunque la lista sea más larga que el hueco.
        private void AlinearCabecera()
        {
            if (_banda == null || _lista == null) return;
            double reserva = _lista.ScrollableHeight > 0 ? 17 : 0;
            if (Math.Abs(_banda.Margin.Right - reserva) < 0.5) return;
            _banda.Margin = new Thickness(0, 0, reserva, 0);
        }

        public void Titulo(string t, string ayuda = "")
        {
            _titulo.Text = t;
            _ayuda.Text = ayuda;
            _ayuda.Visibility = string.IsNullOrEmpty(ayuda) ? Visibility.Collapsed : Visibility.Visible;
        }

        /// Botón de la barra de arriba. Devuelve el Border para poder cambiarle el texto.
        public Border Boton(string texto, Action clic, string? glifo = null, string? tip = null)
        {
            var c = Kit.Chip(texto, clic, glifo, tip: tip);
            c.Margin = new Thickness(0, 2, 8, 2);   // aire por si la barra parte fila
            _barra.Children.Add(c);
            return c;
        }

        /// Filtro tipo pastilla (los Ahora/Histórico, Este mes/Hoy…). El marcado se pinta
        /// en verde azulado para que se vea cuál está elegido sin leer.
        public Border Pastilla(string texto, Action clic, bool activa)
        {
            var c = Kit.Chip(texto, clic, null,
                             activa ? Kit.P(0xFFE4F4F4) : Kit.FondoSuave,
                             activa ? Kit.Teal : Kit.Dim, 11.5);
            c.Margin = new Thickness(0, 2, 8, 2);
            _barra.Children.Add(c);
            return c;
        }

        public void LimpiarBarra() => _barra.Children.Clear();

        /// True para que la fila de botones baje a su propia línea, bajo el título. Los paneles
        /// con seis u ocho botones dejaban el título estrechado y con los puntos suspensivos.
        public bool BarraDebajo
        {
            get => _barraDebajo;
            set
            {
                if (_barraDebajo == value) return;
                _barraDebajo = value;
                if (value)
                {
                    _cabFila.Children.Remove(_barra);
                    _barra.MaxWidth = double.PositiveInfinity;
                    _barra.Margin = new Thickness(0, 9, 0, 0);
                    _cabRaiz.Children.Add(_barra);
                }
                else
                {
                    _cabRaiz.Children.Remove(_barra);
                    _barra.MaxWidth = 560;
                    _barra.Margin = new Thickness(0);
                    Grid.SetColumn(_barra, 1);
                    _cabFila.Children.Add(_barra);
                }
            }
        }

        public void Cabecera(params Columna[] cols)
        {
            _cols = cols ?? Array.Empty<Columna>();
            _cab.ColumnDefinitions.Clear();
            _cab.Children.Clear();
            for (int i = 0; i < _cols.Length; i++)
            {
                _cab.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = _cols[i].Estrella ? new GridLength(1, GridUnitType.Star)
                                              : new GridLength(_cols[i].Ancho),
                });
                var t = Kit.TextoDe(_cols[i].Titulo.ToUpperInvariant(), 10.5, Kit.Dim, true);
                t.FontWeight = FontWeights.SemiBold;
                // Aire entre columnas: sin él, dos cabeceras seguidas se leían como una palabra.
                t.Margin = new Thickness(0, 0, 11, 0);
                if (_cols[i].Derecha) t.TextAlignment = TextAlignment.Right;
                Grid.SetColumn(t, i);
                _cab.Children.Add(t);
            }
            _banda!.Visibility = _cols.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            _cajaTabla.Visibility = _cols.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        public void Kpi(string etiqueta, string valor, string sub = "", Brush? tinte = null)
        {
            var pila = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };
            var bd = new Border
            {
                Background = Kit.P(0xFFFFFFFF),
                BorderBrush = Kit.Linea,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(11),
                Padding = new Thickness(14, 10, 14, 10),
                Child = pila,
            };
            pila.Children.Add(Kit.TextoDe(etiqueta.ToUpperInvariant(), 10, Kit.Dim));
            var v = Kit.TextoDe(valor, 21, tinte ?? Kit.Texto, true);
            v.FontWeight = FontWeights.SemiBold;
            v.FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI");
            v.Margin = new Thickness(0, 3, 0, 0);
            pila.Children.Add(v);
            if (sub.Length > 0) pila.Children.Add(Kit.TextoDe(sub, 11, Kit.Dim));
            _kpis.Children.Add(bd);
        }

        public void KpisLimpias() => _kpis.Children.Clear();

        public void Filas(IEnumerable<Fila>? filas, int max = 220)
        {
            _filas.Children.Clear();
            if (filas == null) return;
            int n = 0, toques = 0;
            foreach (var f in filas)
            {
                if (n >= max) { toques++; continue; }
                _filas.Children.Add(FilaUI(f, n));
                n++;
            }
            if (toques > 0)
                _filas.Children.Add(Kit.TextoDe("… y " + toques + " más en los ficheros de PCJ",
                                                11.5, Kit.Muy));
            if (n == 0)
                _filas.Children.Add(Kit.TextoDe("Todavía no hay nada que mostrar.", 12.5, Kit.Dim));
        }

        private UIElement FilaUI(Fila f, int indice)
        {
            var g = new Grid { MinHeight = f.EsGrupo ? 22 : 26 };

            if (f.EsGrupo)
            {
                var pila = new StackPanel { Orientation = Orientation.Horizontal };
                pila.Children.Add(Kit.TextoDe(f.Celdas.Length > 0 ? f.Celdas[0] : "", 12.5, Kit.Teal, true));
                if (f.Celdas.Length > 1 && f.Celdas[1].Length > 0)
                    pila.Children.Add(Kit.TextoDe("   " + f.Celdas[1], 11.5, Kit.Muy));
                var bg = new Border
                {
                    Child = pila,
                    Background = Kit.P(0xFFFAFDFE),
                    Padding = new Thickness(10, 5, 10, 5),
                };
                return bg;
            }

            for (int i = 0; i < _cols.Length; i++)
                g.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = _cols[i].Estrella ? new GridLength(1, GridUnitType.Star)
                                              : new GridLength(_cols[i].Ancho),
                });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });  // casilla/chip
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });  // botones

            for (int i = 0; i < _cols.Length && i < f.Celdas.Length; i++)
            {
                var celda = f.Celdas[i] ?? "";
                var tinte = i == 0 && f.Tinte != null ? f.Tinte : Kit.Texto;
                var t = Kit.TextoDe(celda, 12.5, tinte, i == 0 && f.Sub != null);
                t.Margin = new Thickness(0, 0, 11, 0);   // misma aireación que la cabecera
                if (_cols[i].Derecha) t.TextAlignment = TextAlignment.Right;
                if (f.Tip != null && i == 0) t.ToolTip = f.Tip;
                if (f.Sub != null && i == 0)
                {
                    var pila = new StackPanel();
                    pila.Children.Add(t);
                    var s = Kit.TextoDe(f.Sub, 10.5, Kit.Muy);
                    s.TextTrimming = TextTrimming.CharacterEllipsis;
                    pila.Children.Add(s);
                    Grid.SetColumn(pila, i);
                    g.Children.Add(pila);
                }
                else
                {
                    Grid.SetColumn(t, i);
                    g.Children.Add(t);
                }
            }

            int colExtras = _cols.Length;
            var derecha = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (f.Casilla != null)
            {
                f.Casilla.Margin = new Thickness(0, 0, 8, 0);
                derecha.Children.Add(f.Casilla);
            }
            if (f.Chip != null)
            {
                derecha.Children.Add(new Border
                {
                    Background = f.ChipFondo,
                    CornerRadius = new CornerRadius(7),
                    Padding = new Thickness(8, 2, 8, 2),
                    Margin = new Thickness(0, 0, 8, 0),
                    Child = Kit.TextoDe(f.Chip, 11, f.ChipTinta, true),
                });
            }
            foreach (var b in f.Botones)
                derecha.Children.Add(Kit.Chip(b.Texto, b.Accion, null, Kit.P(0xFFF6FAFD), Kit.Teal, 11.5, b.Tip));
            Grid.SetColumn(derecha, colExtras);
            if (derecha.Children.Count > 0) g.Children.Add(derecha);

            var bdFila = new Border
            {
                Child = g,
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(10, 3, 10, 3),
                Background = (indice % 2) == 1 ? Kit.P(0xFFFAFCFE) : Kit.P(0x00FFFFFF),
            };
            bdFila.MouseEnter += (s, e) => bdFila.Background = Kit.FondoHover;
            bdFila.MouseLeave += (s, e) => bdFila.Background = (indice % 2) == 1
                ? Kit.P(0xFFFAFCFE) : Kit.P(0x00FFFFFF);
            return bdFila;
        }

        public void Pie(string texto) => _pie.Text = texto ?? "";
    }

    // ----------------------------------------------------------------------------

    /// Barras de tráfico (descendente arriba, ascendente abajo) para la máquina del tiempo.
    internal sealed class GraficoBarras : FrameworkElement
    {
        public List<(long rx, long tx)> Datos = new();
        public int Marcado = -1;
        public string RotuloIzq = "", RotuloDer = "";
        public string Vacio = "Aún no hay muestras: se van guardando solas cada 30 segundos";

        public GraficoBarras()
        {
            Height = 132;
            SnapsToDevicePixels = true;
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            var fondo = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
            fondo.Freeze();
            dc.DrawRectangle(fondo, new Pen(Kit.Linea, 1),
                             new Rect(0.5, 0.5, Math.Max(0, w - 1), Math.Max(0, h - 1)));
            if (Datos.Count == 0)
            {
                // Un recuadro en blanco parece roto. Se dice lo que falta y qué lo llena.
                var ft = new FormattedText(Vacio, System.Globalization.CultureInfo.CurrentUICulture,
                    FlowDirection.LeftToRight,
                    new Typeface(Kit.Main, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                    12, Kit.Dim, VisualTreeHelper.GetDpi(this).PixelsPerDip);
                dc.DrawText(ft, new Point(Math.Max(6, (w - ft.Width) / 2), (h - ft.Height) / 2));
                return;
            }

            long tope = 1;
            foreach (var d in Datos) tope = Math.Max(tope, Math.Max(d.rx, d.tx));

            double slot = w / Datos.Count;
            double anchoBarra = Math.Max(1.5, Math.Min(14, slot * 0.62));
            double mitad = h * 0.5;

            // La línea del centro es la única marca: abajo sube, abajo baja.
            dc.DrawLine(new Pen(Kit.Linea, 1), new Point(4, mitad), new Point(w - 4, mitad));

            for (int i = 0; i < Datos.Count; i++)
            {
                double cx = slot * i + slot / 2;
                double hr = (Datos[i].rx / (double)tope) * (h * 0.44);
                double ht = (Datos[i].tx / (double)tope) * (h * 0.44);
                var pincel = i == Marcado ? Kit.Indigo : Kit.Teal;
                dc.DrawRectangle(pincel, null,
                    new Rect(cx - anchoBarra / 2, mitad - hr, anchoBarra, Math.Max(1, hr)));
                dc.DrawRectangle(i == Marcado ? Kit.P(0xFF8FA0E8) : Kit.P(0xFF9BD9D9), null,
                    new Rect(cx - anchoBarra / 2, mitad + 1, anchoBarra, Math.Max(1, ht)));
            }
        }
    }

    /// Barra de progreso finísima para la cuota.
    internal sealed class BarraProgreso : FrameworkElement
    {
        public double Porcentaje;
        public string TextoEncima = "";

        public BarraProgreso() { Height = 26; }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth;
            double p = Math.Max(0, Math.Min(1, Porcentaje / 100.0));
            var rect = new Rect(0, 14, w, 9);
            dc.DrawRoundedRectangle(Kit.P(0xFFEAF1F7), null, rect, 4.5, 4.5);
            if (p > 0)
            {
                var relleno = new Rect(0, 14, Math.Max(9, w * p), 9);
                Brush brocha = Porcentaje >= 100 ? Kit.Coral : Porcentaje >= 80 ? Kit.Ambar : Kit.Teal;
                dc.DrawRoundedRectangle(brocha, null, relleno, 4.5, 4.5);
            }
            if (TextoEncima.Length > 0)
            {
                var ft = new FormattedText(TextoEncima, CultureInfo.CurrentUICulture,
                    FlowDirection.LeftToRight, new Typeface(Kit.Main, FontStyles.Normal,
                    FontWeights.SemiBold, FontStretches.Normal), 12.5, Kit.Texto,
                    VisualTreeHelper.GetDpi(this).PixelsPerDip);
                dc.DrawText(ft, new Point(0, 0));
            }
        }
    }
}
