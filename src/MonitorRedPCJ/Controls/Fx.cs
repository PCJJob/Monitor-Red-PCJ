using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace MonitorRedPCJ.Controls
{
    // ============================================================================
    //  Fx — el movimiento, en tres piezas sueltas
    // ============================================================================
    //
    // Las vistas no llevan animaciones escritas en el XAML de cada fila: son ciento
    // y tantas filas que se rehacen cada segundo y medio, y una Storyboard declarada
    // en la plantilla costaría cara en cada repaso. Aquí van las tres cosas que de
    // verdad se notan, elegidas para que ninguna obligue a WPF a recolocar nada:
    //
    //   · Fraction   — las barritas de tráfico y de PESO crecen de lado en vez de
    //     saltar. Se anima el ScaleX de un RenderTransform, que es lo único que se
    //     puede mover sin pasar por el layout: el dibujo cambia de tamaño en la
    //     tarjeta gráfica y la fila no se mueve ni un píxel.
    //
    //   · Pulse      — un número que cambia de valor da un breve aviso de opacidad,
    //     para que se vea qué cifra se movió sin tener que leerla toda.
    //
    //   · Turn       — la flechita que abre el detalle gira en vez de cambiarse por
    //     el glifo de arriba.
    //
    // Y una regla que no es de este archivo pero manda aquí: nunca se anima el Color
    // de un pincel de la paleta. Esos pinceles son compartidos por toda la
    // aplicación y animarlos cambiaría el color en todos los sitios a la vez. Se
    // anima opacidad, tamaño o giro.
    //
    // Fx.Animar se puede apagar del todo (el modo de comprobación de diseño lo hace
    // antes de fotografiar, para que el PNG salga con las barras ya en su sitio).
    // ============================================================================
    public static class Fx
    {
        /// <summary>Apagado general: las propiedades adjuntas ponen el valor directo.</summary>
        public static bool Animar = true;

        private static readonly Duration Medio = new TimeSpan(0, 0, 0, 0, 260);
        private static readonly Duration Corto = new TimeSpan(0, 0, 0, 0, 170);
        private static readonly IEasingFunction Suave = new CubicEase { EasingMode = EasingMode.EaseOut };

        private static DoubleAnimation Anim(double to, Duration d)
            => new DoubleAnimation(to, d) { EasingFunction = Suave };

        private static double CeroUno(double v) => v < 0 ? 0 : (v > 1 ? 1 : v);

        // ---------------------------------------------------------------- Fraction

        public static readonly DependencyProperty FractionProperty =
            DependencyProperty.RegisterAttached("Fraction", typeof(double), typeof(Fx),
                new FrameworkPropertyMetadata(0.0, OnFractionChanged));

        public static void SetFraction(DependencyObject o, double v) => o.SetValue(FractionProperty, v);
        public static double GetFraction(DependencyObject o) => (double)o.GetValue(FractionProperty);

        // El último valor pedido, guardado aparte: durante la animación Width/ScaleX
        // tienen el valor interpolado y no sirven para saber de dónde partir.
        private static readonly DependencyProperty FromProperty =
            DependencyProperty.RegisterAttached("From", typeof(double), typeof(Fx),
                new PropertyMetadata(double.NaN));

        private static void OnFractionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not FrameworkElement el) return;
            double to = CeroUno((double)e.NewValue);
            var st = Escala(el);
            if (st == null) return;

            double from = (double)el.GetValue(FromProperty);
            el.SetValue(FromProperty, to);

            // Primera vez (o animación apagada): se pone y ya. Al nacer la fila no hay
            // de dónde arrancar, y animar desde cero haría que cada fila reciclada
            // volviera a crecer al pasar por ella con la rueda.
            if (!Animar || double.IsNaN(from) || Math.Abs(from - to) < 0.004)
            {
                st.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                st.ScaleX = to;
                return;
            }
            st.BeginAnimation(ScaleTransform.ScaleXProperty, Anim(to, Medio));
        }

        // El RenderTransform de la barra ha de ser un ScaleTransform suyo, declarado en
        // la plantilla con ScaleX=0 para que la barra nazca cerrada.
        private static ScaleTransform? Escala(FrameworkElement el)
        {
            if (el.RenderTransform is ScaleTransform st) return Libre(el, st, st.ScaleX, st.ScaleY);
            if (el.RenderTransform != null) return null;   // otro transform: no se toca
            var nuevo = new ScaleTransform(GetFraction(el), 1);
            el.RenderTransform = nuevo;
            return nuevo;
        }

        // --------------------------------------- el transform inmovilizado, en claro
        //
        // WPF inmoviliza ("freezes") los Freezables que se declaran dentro de una
        // plantilla o de un Setter cuando no llevan enlaces ni recursos dinámicos, y los
        // comparte entre todas las filas para ahorrar memoria. Un objeto inmovilizado no
        // se puede animar, y además sería compartido: animarlo movería las demás filas.
        // Por eso, si la escritura de prueba falla, se sustituye por una copia libre con
        // el mismo valor, que ya es solo de esta fila. Sin este paso, la primera barra que
        // intentara crecer lanzaría «el objeto está protegido o inmovilizado» y el
        // apartado entero de Protección se quedaría en blanco.
        private static bool SePuedeEscribir(Freezable f)
        {
            // Se comprueba escribiendo de verdad en una copia de prueba: la propiedad
            // IsFrozen viene implementada por la interfaz y no está a la vista desde
            // el tipo. Un inmovilizado lanza al asignar; la copia libre permite leer el
            // estado sin tocar el objeto compartido.
            try
            {
                if (f is ScaleTransform s) s.ScaleX = s.ScaleX;
                else if (f is RotateTransform r) r.Angle = r.Angle;
                return true;
            }
            catch (InvalidOperationException) { return false; }
        }

        private static ScaleTransform Libre(FrameworkElement el, ScaleTransform st, double x, double y)
        {
            if (SePuedeEscribir(st)) return st;
            var copia = new ScaleTransform(x, y);
            el.RenderTransform = copia;
            return copia;
        }

        private static RotateTransform Libre(FrameworkElement el, RotateTransform rt, double ang)
        {
            if (SePuedeEscribir(rt)) return rt;
            var copia = new RotateTransform(ang);
            el.RenderTransform = copia;
            return copia;
        }

        // ------------------------------------------------------------------- Pulse

        public static readonly DependencyProperty PulseProperty =
            DependencyProperty.RegisterAttached("Pulse", typeof(object), typeof(Fx),
                new PropertyMetadata(null, OnPulseChanged));

        public static void SetPulse(DependencyObject o, object v) => o.SetValue(PulseProperty, v);
        public static object GetPulse(DependencyObject o) => o.GetValue(PulseProperty);

        private static readonly DependencyProperty FirstPulseProperty =
            DependencyProperty.RegisterAttached("FirstPulse", typeof(bool), typeof(Fx),
                new PropertyMetadata(true));

        private static void OnPulseChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not UIElement el || !Animar) return;
            if (Equals(e.OldValue, e.NewValue)) return;

            // El primer valor no pulsa: al montar la lista todo "cambia" de la nada a lo
            // que hay, y hacer parpadear cuarenta filas a la vez parece un fallo.
            if ((bool)el.GetValue(FirstPulseProperty))
            {
                el.SetValue(FirstPulseProperty, false);
                return;
            }
            var a = new DoubleAnimationUsingKeyFrames
            {
                Duration = new Duration(Corto.TimeSpan * 2)
            };
            a.KeyFrames.Add(new LinearDoubleKeyFrame(0.42, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            a.KeyFrames.Add(new LinearDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(Corto.TimeSpan * 2)));
            el.BeginAnimation(UIElement.OpacityProperty, a);
        }

        // -------------------------------------------------------------------- Turn

        public static readonly DependencyProperty TurnedProperty =
            DependencyProperty.RegisterAttached("Turned", typeof(bool), typeof(Fx),
                new PropertyMetadata(false, OnTurnedChanged));

        public static void SetTurned(DependencyObject o, bool v) => o.SetValue(TurnedProperty, v);
        public static bool GetTurned(DependencyObject o) => (bool)o.GetValue(TurnedProperty);

        private static void OnTurnedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not FrameworkElement el) return;
            var rt = el.RenderTransform as RotateTransform
                     ?? (el.RenderTransform == null ? new RotateTransform(0) : null);
            if (rt == null) return;
            if (el.RenderTransform == null) el.RenderTransform = rt;
            else rt = Libre(el, rt, rt.Angle);
            double to = (bool)e.NewValue ? 180 : 0;
            if (!Animar) { rt.Angle = to; return; }
            rt.BeginAnimation(RotateTransform.AngleProperty, Anim(to, Medio));
        }

        // ---------------------------------------------------------------- Aparición

        /// <summary>
        /// Aparecer con un fundido corto: se usa al cambiar de grupo o al buscar, que es
        /// cuando la lista entera cambia de contenido y un cambio brusco se lee como un
        /// parpadeo. Solo opacidad, que no cuesta layout.
        /// </summary>
        public static void Aparecer(FrameworkElement el)
        {
            if (!Animar || el == null) return;
            el.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0.25, 1.0, new Duration(TimeSpan.FromMilliseconds(200)))
                { EasingFunction = Suave });
        }
    }
}
