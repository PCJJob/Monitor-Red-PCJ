using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace MonitorRedPCJ.Controls
{
    // Utilidades de dibujo compartidas por los gráficos de tráfico y de recursos.
    internal static class GraphKit
    {
        // ---------- El color, ahora del tema ----------
        //
        // Los gráficos no son XAML: dibujan a mano en OnRender, así que no pueden colgarse de
        // un DynamicResource. Lo que hacen es pedir la ficha de la paleta en cada trazo, que
        // es justo cuando se repinta la línea. Por eso son propiedades y no campos: un campo
        // se congelaría con el tema del primer arranque.
        internal static Color Token(string clave, Color respaldo)
            => Application.Current?.Resources[clave] is SolidColorBrush b ? b.Color : respaldo;

        private static Color ConAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

        // Letra de los ejes: el texto tenue del tema.
        public static Color Ink => Token("B.TextDim", Color.FromRgb(0x5A, 0x6B, 0x7B));

        // Sobre fondo oscuro una rejilla casi transparente desaparece, así que la opa
        // depende del tema: en claro se busca apenas notar la línea, en oscuro que se vea.
        private static byte TramaAlpha => Services.ThemeService.EsOscuro ? (byte)0x59 : (byte)0x14;
        private static byte EjeAlpha => Services.ThemeService.EsOscuro ? (byte)0x99 : (byte)0x28;

        public static Color Grid => ConAlpha(Token("B.Line", Color.FromRgb(0x33, 0x44, 0x55)), TramaAlpha);
        public static Color Axis => ConAlpha(Token("B.Edge", Color.FromRgb(0x33, 0x44, 0x55)), EjeAlpha);

        // El color del cartón: es lo que hay detrás del gráfico, y sirve para el anillo del
        // punto vivo y para la caja del aviso flotante. Antes era blanco a secas.
        public static Color Papel => Token("B.Panel", Colors.White);

        // Escalones binarios cuya cuarta parte sigue siendo redonda (128 MB → 32 MB por línea)
        // y que cubren toda la década, para no inflar el eje 4x con valores pequeños.
        public static double NiceMax(double v, double floor)
        {
            if (v <= floor) return floor;
            double[] steps = { 1, 2, 4, 8, 16, 32, 64, 128, 256, 512, 1024 };
            double pow = 1;
            while (v / pow > 1024) pow *= 1024;
            foreach (var s in steps)
                if (v / pow <= s) return s * pow;
            return 1024 * pow * 1024;
        }

        public static FormattedText Text(Visual v, string s, double size, Brush brush, double opacity = 1.0)
        {
            if (opacity < 1 && brush is SolidColorBrush sb)
            {
                var dim = new SolidColorBrush(sb.Color) { Opacity = opacity };
                dim.Freeze();
                brush = dim;
            }
            double dpi = VisualTreeHelper.GetDpi(v).PixelsPerDip;
            return new FormattedText(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), size, brush, dpi);
        }

        public static void Series(DrawingContext dc, Point[] pts, Color color, double baseY, double width)
        {
            var stroke = new SolidColorBrush(color);
            stroke.Freeze();

            var area = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1),
                GradientStops = new GradientStopCollection {
                    new GradientStop(Color.FromArgb(0x59, color.R, color.G, color.B), 0),
                    new GradientStop(Color.FromArgb(0x14, color.R, color.G, color.B), 0.75),
                    new GradientStop(Color.FromArgb(0x05, color.R, color.G, color.B), 1) }
            };
            area.Freeze();

            dc.DrawGeometry(area, null, Geometry(pts, baseY, true));

            var pen = new Pen(stroke, width)
            {
                LineJoin = PenLineJoin.Round,
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            pen.Freeze();
            dc.DrawGeometry(null, pen, Geometry(pts, baseY, false));
        }

        // Curva suave sin sobretiro (las tangentes se anulan en los cambios de signo),
        // para que un pico se vea como pico y no como triángulo.
        public static StreamGeometry Geometry(Point[] p, double baseY, bool closedArea)
        {
            int n = p.Length;
            var m = Tangents(p);
            var geo = new StreamGeometry();
            using var c = geo.Open();

            if (closedArea) c.BeginFigure(p[0], true, true);
            else c.BeginFigure(p[0], false, false);

            for (int i = 0; i < n - 1; i++)
            {
                double dx = Math.Max(1e-6, p[i + 1].X - p[i].X);
                c.BezierTo(
                    new Point(p[i].X + dx / 3, p[i].Y + m[i] * dx / 3),
                    new Point(p[i + 1].X - dx / 3, p[i + 1].Y - m[i + 1] * dx / 3),
                    p[i + 1], true, true);
            }

            if (closedArea)
            {
                c.LineTo(new Point(p[n - 1].X, baseY), true, false);
                c.LineTo(new Point(p[0].X, baseY), true, false);
            }
            geo.Freeze();
            return geo;
        }

        private static double[] Tangents(Point[] p)
        {
            int n = p.Length;
            var slope = new double[n - 1];
            var m = new double[n];
            for (int i = 0; i < n - 1; i++)
                slope[i] = (p[i + 1].Y - p[i].Y) / Math.Max(1e-6, p[i + 1].X - p[i].X);

            m[0] = slope[0];
            m[n - 1] = slope[n - 2];
            for (int i = 1; i < n - 1; i++)
                m[i] = slope[i - 1] * slope[i] <= 0 ? 0 : (slope[i - 1] + slope[i]) / 2;

            for (int i = 0; i < n - 1; i++)
            {
                if (slope[i] == 0) { m[i] = 0; m[i + 1] = 0; continue; }
                double a = m[i] / slope[i], b = m[i + 1] / slope[i];
                double s = a * a + b * b;
                if (s > 9)
                {
                    double t = 3 / Math.Sqrt(s);
                    m[i] = t * a * slope[i];
                    m[i + 1] = t * b * slope[i];
                }
            }
            return m;
        }

        public static void LiveDot(DrawingContext dc, Point p, Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            var ring = new SolidColorBrush(Papel);
            ring.Freeze();

            dc.DrawEllipse(brush, new Pen(ring, 2), p, 4, 4);
            var halo = new SolidColorBrush(color) { Opacity = 0.18 };
            halo.Freeze();
            dc.DrawEllipse(halo, null, p, 8, 8);
        }

        public static void HoverMark(DrawingContext dc, Point p, Color color)
        {
            var pen = new Pen(new SolidColorBrush(color), 2);
            pen.Freeze();
            dc.DrawEllipse(new SolidColorBrush(Papel), pen, p, 3.5, 3.5);
        }

        // Caja del tooltip sobre el eje que marca el ratón.
        public static void Tooltip(DrawingContext dc, double anchorX, double anchorY, double width,
                                  double height, IList<string> lines, Func<string, double, FormattedText> make)
        {
            double boxW = 0, boxH = 6 + lines.Count * 15;
            var texts = new List<FormattedText>();
            foreach (var s in lines)
            {
                var t = make(s, s == lines[0] ? 10 : 11);
                boxW = Math.Max(boxW, t.Width);
                texts.Add(t);
            }

            double bx = anchorX + 12;
            if (bx + boxW + 16 > width - 8) bx = anchorX - boxW - 28;
            double by = Math.Max(4, Math.Min(anchorY - boxH / 2, height - boxH - 4));

            var bg = new SolidColorBrush(Papel) { Opacity = 0.97 };
            bg.Freeze();
            var border = new SolidColorBrush(Axis);
            border.Freeze();
            dc.DrawRoundedRectangle(bg, new Pen(border, 1), new Rect(bx, by, boxW + 16, boxH), 6, 6);

            double ty = by + 5;
            foreach (var t in texts) { dc.DrawText(t, new Point(bx + 8, ty)); ty += 15; }
        }
    }

    // Gráfico de área con eje temporal fijo (ventana deslizante de WindowSeconds),
    // curvas monótonas tipo Fritsch–Carlson, rejilla con etiquetas y lectura al pasar el ratón.
    public class TrafficGraph : FrameworkElement
    {
        private const int WindowSeconds = 120;
        private const double PadLeft = 58, PadRight = 16, PadTop = 18, PadBottom = 24;

        private static Color RxColor => GraphKit.Token("B.Green", Color.FromRgb(0x2F, 0xB3, 0x80));   // descarga
        private static Color TxColor => GraphKit.Token("B.Amber", Color.FromRgb(0xF4, 0xA6, 0x2A));   // subida

        private readonly List<(double rx, double tx)> _samples = new();
        private readonly List<(double rx, double tx)> _display = new();
        private readonly DispatcherTimer _anim;
        private double _scale = 1024;
        private Point? _hover;

        public TrafficGraph()
        {
            _anim = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
            _anim.Tick += (s, e) => Interpolate();
            _anim.Start();
            Unloaded += (s, e) => _anim.Stop();
        }

        public void Push(double rx, double tx)
        {
            _samples.Add((rx, tx));
            if (_display.Count == _samples.Count - 1)
                _display.Add(_display.Count > 0 ? _display[_display.Count - 1] : (rx, tx));

            while (_samples.Count > WindowSeconds) _samples.RemoveAt(0);
            while (_display.Count > _samples.Count) _display.RemoveAt(0);
            if (_display.Count != _samples.Count)
            {
                _display.Clear();
                _display.AddRange(_samples);
            }
        }

        private void Interpolate()
        {
            if (_samples.Count == 0) return;
            for (int i = 0; i < _samples.Count; i++)
            {
                var (sr, st) = _samples[i];
                var (dr, dt) = _display[i];
                _display[i] = (dr + (sr - dr) * 0.3, dt + (st - dt) * 0.3);
            }

            double peak = 0;
            foreach (var (r, t) in _display) peak = Math.Max(peak, Math.Max(r, t));
            double target = GraphKit.NiceMax(peak, 1024);
            // Sube rápido para que un pico no se recorte, baja despacio para no parpadear.
            _scale += (target - _scale) * (target > _scale ? 0.5 : 0.06);

            InvalidateVisual();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            _hover = e.GetPosition(this);
            InvalidateVisual();
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            _hover = null;
            InvalidateVisual();
            base.OnMouseLeave(e);
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w < 80 || h < 60) return;

            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));

            double plotW = w - PadLeft - PadRight;
            double plotH = h - PadTop - PadBottom;
            double baseY = PadTop + plotH;

            DrawGrid(dc, plotW, plotH, baseY);

            if (_display.Count < 2)
            {
                var t = GraphKit.Text(this, "Esperando tráfico…", 12, new SolidColorBrush(GraphKit.Ink), 0.75);
                dc.DrawText(t, new Point(PadLeft + plotW / 2 - t.Width / 2, PadTop + plotH / 2 - t.Height / 2));
                return;
            }

            double stepX = plotW / (WindowSeconds - 1);
            int n = _display.Count;
            double X(int i) => PadLeft + plotW - (n - 1 - i) * stepX;
            double Y(double v) => baseY - Math.Min(plotH, v / _scale * plotH);

            var ptsRx = new Point[n];
            var ptsTx = new Point[n];
            for (int i = 0; i < n; i++)
            {
                ptsRx[i] = new Point(X(i), Y(_display[i].rx));
                ptsTx[i] = new Point(X(i), Y(_display[i].tx));
            }

            // Primero la serie de menor peso visual para que la descarga quede por delante.
            GraphKit.Series(dc, ptsTx, TxColor, baseY, 1.8);
            GraphKit.Series(dc, ptsRx, RxColor, baseY, 1.8);

            GraphKit.LiveDot(dc, ptsRx[n - 1], RxColor);
            GraphKit.LiveDot(dc, ptsTx[n - 1], TxColor);

            if (_hover is Point hp && hp.X >= PadLeft && hp.X <= PadLeft + plotW)
                DrawHover(dc, hp, ptsRx, ptsTx, n, stepX, baseY);
        }

        private void DrawGrid(DrawingContext dc, double plotW, double plotH, double baseY)
        {
            var gridPen = new Pen(new SolidColorBrush(GraphKit.Grid), 1);
            gridPen.Freeze();
            var axisPen = new Pen(new SolidColorBrush(GraphKit.Axis), 1);
            axisPen.Freeze();
            var label = new SolidColorBrush(GraphKit.Ink);
            label.Opacity = 0.85;
            label.Freeze();

            const int divisions = 4;
            for (int i = 0; i <= divisions; i++)
            {
                double y = baseY - plotH * i / divisions;
                dc.DrawLine(i == 0 ? axisPen : gridPen, new Point(PadLeft, y), new Point(PadLeft + plotW, y));

                var t = GraphKit.Text(this, Format.Bps(_scale * i / divisions), 10, label);
                dc.DrawText(t, new Point(PadLeft - 8 - t.Width, y - t.Height / 2));
            }

            // Marcas de tiempo cada 30 s ancladas en el borde derecho (ahora).
            double stepX = plotW / (WindowSeconds - 1);
            for (int ago = 0; ago < WindowSeconds; ago += 30)
            {
                double x = PadLeft + plotW - ago * stepX;
                if (ago != 0)
                    dc.DrawLine(gridPen, new Point(x, PadTop), new Point(x, baseY));

                string text = ago == 0 ? "ahora" : ago == 60 ? "1 min" : ago + " s";
                var t = GraphKit.Text(this, text, 10, label);
                dc.DrawText(t, new Point(x - t.Width / 2, baseY + 6));
            }
        }

        private void DrawHover(DrawingContext dc, Point hp, Point[] rx, Point[] tx, int n,
                               double stepX, double baseY)
        {
            double plotW = ActualWidth - PadLeft - PadRight;
            int idx = n - 1 - (int)Math.Round((PadLeft + plotW - hp.X) / stepX);
            idx = Math.Max(0, Math.Min(n - 1, idx));
            double x = rx[idx].X;

            var pen = new Pen(new SolidColorBrush(GraphKit.Axis), 1) { DashStyle = DashStyles.Dot };
            pen.Freeze();
            dc.DrawLine(pen, new Point(x, PadTop), new Point(x, baseY));

            GraphKit.HoverMark(dc, rx[idx], RxColor);
            GraphKit.HoverMark(dc, tx[idx], TxColor);

            int ago = n - 1 - idx;
            var lines = new[]
            {
                ago == 0 ? "ahora" : "hace " + (ago >= 60 ? (ago / 60) + " min" : ago + " s"),
                "↓ " + Format.Bps(_display[idx].rx),
                "↑ " + Format.Bps(_display[idx].tx),
            };

            var fg = new SolidColorBrush(GraphKit.Ink);
            fg.Freeze();
            GraphKit.Tooltip(dc, x, hp.Y, ActualWidth, ActualHeight, lines,
                (s, size) => GraphKit.Text(this, s, size, fg, s == lines[0] ? 0.7 : 1.0));
        }
    }

    public enum GraphUnit { Percent, Data, Memory }

    // Serie única de recursos (CPU, memoria, disco, red) con la misma calidad de dibujo
    // que el monitor de tráfico: rejilla, eje en pasos redondos, curva suave y lectura al hover.
    public class ResourceGraph : FrameworkElement
    {
        private const int WindowSeconds = 120;
        private const double PadLeft = 52, PadRight = 14, PadTop = 12, PadBottom = 20;
        private const int Divisions = 4;

        private readonly List<double> _samples = new();
        private readonly List<double> _display = new();
        private readonly DispatcherTimer _anim;
        private double _scale = 1;
        private Point? _hover;
        // El color del trazo, por si el XAML no pone ninguno: el acento del tema, leído al
        // dibujar (ver GraphKit.Token). _color se rellena desde la propiedad Stroke.
        private Color _color = Color.FromRgb(0x0F, 0xA3, 0xA3);

        public GraphUnit Unit { get; set; } = GraphUnit.Data;
        public string EmptyText { get; set; } = "Esperando datos…";

        // El trazo se expone como DependencyProperty, no como propiedad normal, por dos
        // motivos: el XAML de Recursos lo enlaza a la paleta con DynamicResource (y un
        // DynamicResource solo puede caer sobre una dependencia), y así al cambiar de tema
        // WPF avisa solo y la curva se repinta con el acento nuevo sin esperar a la muestra
        // siguiente.
        public static readonly DependencyProperty StrokeProperty =
            DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(ResourceGraph),
                new FrameworkPropertyMetadata(null,
                    FrameworkPropertyMetadataOptions.AffectsRender,
                    (d, e) =>
                    {
                        var g = (ResourceGraph)d;
                        if (e.NewValue is SolidColorBrush b) g._color = b.Color;
                    }));

        public Brush Stroke
        {
            get => (Brush)GetValue(StrokeProperty);
            set => SetValue(StrokeProperty, value);
        }

        // Valor más alto de la ventana visible, para mostrarlo como "máx" en la tarjeta.
        public double WindowPeak { get; private set; }

        public ResourceGraph()
        {
            _anim = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
            _anim.Tick += (s, e) => Interpolate();
            _anim.Start();
            Unloaded += (s, e) => _anim.Stop();
        }

        public void Push(double v)
        {
            _samples.Add(Math.Max(0, v));
            if (_display.Count == _samples.Count - 1)
                _display.Add(_display.Count > 0 ? _display[_display.Count - 1] : Math.Max(0, v));

            while (_samples.Count > WindowSeconds) _samples.RemoveAt(0);
            while (_display.Count > _samples.Count) _display.RemoveAt(0);
            if (_display.Count != _samples.Count)
            {
                _display.Clear();
                _display.AddRange(_samples);
            }
        }

        private void Interpolate()
        {
            if (_samples.Count == 0) return;
            for (int i = 0; i < _samples.Count; i++)
            {
                double target = _samples[i];
                _display[i] += (target - _display[i]) * 0.3;
            }

            double peak = 0;
            foreach (var v in _display) peak = Math.Max(peak, v);
            WindowPeak = peak;

            double goal = Unit switch
            {
                GraphUnit.Percent => 100,
                GraphUnit.Memory => Math.Max(4, Math.Ceiling(peak / 4) * 4), // ejes de 4 GB
                _ => GraphKit.NiceMax(peak, 1024),
            };
            _scale += (goal - _scale) * (goal > _scale ? 0.5 : 0.06);

            InvalidateVisual();
        }

        private string Label(double v) => Unit switch
        {
            GraphUnit.Percent => v.ToString("0") + " %",
            GraphUnit.Memory => v.ToString("0.#") + " GB",
            _ => Format.Bps(v),
        };

        protected override void OnMouseMove(MouseEventArgs e)
        {
            _hover = e.GetPosition(this);
            InvalidateVisual();
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            _hover = null;
            InvalidateVisual();
            base.OnMouseLeave(e);
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w < 90 || h < 50) return;

            // Relleno transparente: sin él, el gráfico solo capta el ratón donde hay dibujo.
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));

            double plotW = w - PadLeft - PadRight;
            double plotH = h - PadTop - PadBottom;
            double baseY = PadTop + plotH;

            var gridPen = new Pen(new SolidColorBrush(GraphKit.Grid), 1);
            gridPen.Freeze();
            var axisPen = new Pen(new SolidColorBrush(GraphKit.Axis), 1);
            axisPen.Freeze();
            var inkBrush = new SolidColorBrush(GraphKit.Ink);
            inkBrush.Opacity = 0.85;
            inkBrush.Freeze();

            for (int i = 0; i <= Divisions; i++)
            {
                double y = baseY - plotH * i / Divisions;
                dc.DrawLine(i == 0 ? axisPen : gridPen, new Point(PadLeft, y), new Point(PadLeft + plotW, y));

                var t = GraphKit.Text(this, Label(_scale * i / Divisions), 9, inkBrush);
                dc.DrawText(t, new Point(PadLeft - 6 - t.Width, y - t.Height / 2));
            }

            double stepX = plotW / (WindowSeconds - 1);
            for (int ago = 0; ago < WindowSeconds; ago += 60)
            {
                double x = PadLeft + plotW - ago * stepX;
                if (ago != 0) dc.DrawLine(gridPen, new Point(x, PadTop), new Point(x, baseY));

                var t = GraphKit.Text(this, ago == 0 ? "ahora" : ago / 60 + " min", 9, inkBrush);
                dc.DrawText(t, new Point(x - t.Width / 2, baseY + 4));
            }

            if (_display.Count < 2)
            {
                var t = GraphKit.Text(this, EmptyText, 11, new SolidColorBrush(GraphKit.Ink), 0.7);
                dc.DrawText(t, new Point(PadLeft + plotW / 2 - t.Width / 2, PadTop + plotH / 2 - t.Height / 2));
                return;
            }

            int n = _display.Count;
            var pts = new Point[n];
            for (int i = 0; i < n; i++)
            {
                double x = PadLeft + plotW - (n - 1 - i) * stepX;
                double y = baseY - Math.Min(plotH, _display[i] / _scale * plotH);
                pts[i] = new Point(x, y);
            }

            GraphKit.Series(dc, pts, _color, baseY, 1.7);
            GraphKit.LiveDot(dc, pts[n - 1], _color);

            if (_hover is Point hp && hp.X >= PadLeft && hp.X <= PadLeft + plotW)
            {
                int idx = n - 1 - (int)Math.Round((PadLeft + plotW - hp.X) / stepX);
                idx = Math.Max(0, Math.Min(n - 1, idx));
                double x = pts[idx].X;

                var dot = new Pen(new SolidColorBrush(GraphKit.Axis), 1) { DashStyle = DashStyles.Dot };
                dot.Freeze();
                dc.DrawLine(dot, new Point(x, PadTop), new Point(x, baseY));
                GraphKit.HoverMark(dc, pts[idx], _color);

                int ago = n - 1 - idx;
                var lines = new[]
                {
                    ago == 0 ? "ahora" : "hace " + (ago >= 60 ? (ago / 60) + " min" : ago + " s"),
                    Label(_display[idx])
                };
                GraphKit.Tooltip(dc, x, hp.Y, w, h, lines,
                    (s, size) => GraphKit.Text(this, s, size, inkBrush, s == lines[0] ? 0.7 : 1.0));
            }
        }
    }
}
