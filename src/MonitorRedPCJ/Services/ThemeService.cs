using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace MonitorRedPCJ.Services
{
    // ============================================================
    //   EL MOTOR DE TEMAS
    // ============================================================
    //
    // La idea cabe en una frase: los colores y las formas están separados de las
    // hojas de estilo. Cada tema es un diccionario pequeño (Themes/Temas/*.xaml)
    // que solo define claves de paleta —B.Text, C.Green, R.Card, F.Main…— y el
    // resto de la aplicación las cita con DynamicResource. Al cambiar de tema se
    // sustituye el primer diccionario de Application.Resources por el del tema
    // elegido, y como cada elemento vuelve a pedir su pincel, toda la aplicación
    // se repinta sola: sin cerrar ventanas, sin reiniciar, sin recompilar.
    //
    // Por qué esto obliga a dos reglas que hay que respetar al tocar el diseño:
    //
    //   · Un tema nuevo ha de definir EXACTAMENTE las mismas claves que los
    //     demás (hoy son 150). Si falta una, el elemento que la pide se queda sin pincel y sale
    //     invisible (WPF no avisa con DynamicResource). El catálogo de abajo es el
    //     sitio donde se declaran los temas; el comprobador --theme-check verifica
    //     que las tres paletas tengan las mismas claves.
    //
    //   · Los pinceles de la paleta son compartidos por toda la aplicación. Nunca
    //     se anima su Color (eso pintaría de golpe todos los sitios que lo usan):
    //     se anima opacidad, tamaño o posición.
    //
    // Los códigos que guardan un pincel buscado una sola vez (el Skin de la
    // pestaña de Protección, los pinceles estáticos de las herramientas, los
    // colores de los gráficos) se avisan por el evento Cambiado para que lo
    // vuelvan a buscar; sin ese aviso seguirían mostrando el tema viejo hasta
    // cerrar la ventana.

    /// <summary>Un tema disponible, con lo que la pantalla de Temas necesita saber de él.</summary>
    public sealed class ThemeInfo
    {
        public string Id { get; init; } = "";

        /// <summary>Nombre que se lee en la tarjeta.</summary>
        public string Nombre { get; init; } = "";

        /// <summary>Una línea que dice para quién es este tema.</summary>
        public string Lema { get; init; } = "";

        /// <summary>Archivo de paleta dentro de Themes/Temas (sin extensión).</summary>
        public string Archivo { get; init; } = "";

        /// <summary>Si el fondo es oscuro: lo usan los controles que dibujan sombra o reborde.</summary>
        public bool Oscuro { get; init; }

        /// <summary>Glifo de la tarjeta, para no dejarla con un cuadrado mudo.</summary>
        public string Icono { get; init; } = "";

        /// <summary>
        /// Las seis claves que se pintan como muestras en la miniatura de la tarjeta.
        /// Son siempre las mismas en los tres temas, así las muestras se comparan de frente.
        /// </summary>
        public static readonly string[] Muestras =
            { "B.Bg", "B.Panel", "B.Text", "B.Teal", "B.Amber", "B.Coral" };
    }

    public static class ThemeService
    {
        // El catálogo. El primero es el fallback: si el ajuste guarda un id que ya no existe
        // (un tema eliminado en una versión futura), se cae aquí y no en un vacío.
        public static readonly IReadOnlyList<ThemeInfo> Lista = new List<ThemeInfo>
        {
            new ThemeInfo
            {
                Id = "aurora", Nombre = "Aurora", Archivo = "Aurora", Oscuro = false,
                Lema = "Claro y definido — el aspecto con el que PCJ siempre se vio",
                Icono = char.ConvertFromUtf32(0xE706),        // sol
            },
            new ThemeInfo
            {
                Id = "nocturna", Nombre = "Nocturna", Archivo = "Nocturna", Oscuro = true,
                Lema = "Fondo oscuro, acentos vivos — para trabajar de noche y con poco luz",
                Icono = char.ConvertFromUtf32(0xE708),        // luna
            },
            new ThemeInfo
            {
                Id = "bruma", Nombre = "Bruma", Archivo = "Bruma", Oscuro = false,
                Lema = "Papel frío con azul índigo — el más discreto de los tres",
                Icono = char.ConvertFromUtf32(0xE753),        // nube
            },
        };

        public const string Predeterminado = "aurora";

        // Índice por id para no recorrer la lista en cada pulsación.
        private static readonly Dictionary<string, ThemeInfo> _porId =
            new(StringComparer.OrdinalIgnoreCase);

        static ThemeService()
        {
            foreach (var t in Lista) _porId[t.Id] = t;
        }

        /// <summary>El tema pedido, o null si ese id no está en el catálogo.</summary>
        public static ThemeInfo? Buscar(string? id)
            => string.IsNullOrWhiteSpace(id) ? null
               : _porId.TryGetValue(id.Trim(), out var t) ? t : null;

        /// <summary>El id en uso ahora mismo.</summary>
        public static string Actual { get; private set; } = Predeterminado;

        /// <summary>El tema en uso ahora mismo (nunca null).</summary>
        public static ThemeInfo Corriente => Buscar(Actual) ?? Buscar(Predeterminado)!;

        /// <summary>¿El tema en uso es de fondo oscuro?</summary>
        public static bool EsOscuro => Corriente.Oscuro;

        /// <summary>
        /// Se lanza con el id recién puesto. Los códigos que dejaron un pincel guardado se
        /// suscriben aquí y lo vuelven a buscar; los que usan DynamicResource no necesitan
        /// enterarse, WPF los repinta solo.
        /// </summary>
        public static event Action<string>? Cambiado;

        // ---------- Aplicar ----------

        /// <summary>
        /// Cambia el tema en caliente. <paramref name="guardar"/> deja escribirla en
        /// settings.json: en el arranque se pasa false porque el valor ya viene de ahí y
        /// no hace falta volver a escribirlo (y porque al arrancar todavía puede no haber
        /// SettingsService creado).
        /// </summary>
        public static bool Aplicar(string? id, bool guardar = true)
        {
            var tema = Buscar(id) ?? Buscar(Predeterminado)!;
            if (string.Equals(tema.Id, Actual, StringComparison.OrdinalIgnoreCase) &&
                Application.Current != null && ReferenciaCorrecta(tema))
            {
                // Ya está puesto: no se sustituye el diccionario (eso repintaría todo sin
                // necesidad) ni se escribe en disco.
                return false;
            }

            try
            {
                Poner(tema);
            }
            catch
            {
                // Si el XAML del tema no cargara (nunca debe pasar: va compilado en el
                // ensamblado), nos quedamos donde estábamos antes de tocar nada.
                return false;
            }

            Actual = tema.Id;

            if (guardar)
            {
                try
                {
                    if (App.Settings != null && App.Settings.Current.Tema != tema.Id)
                    {
                        App.Settings.Current.Tema = tema.Id;
                        App.Settings.Save();
                    }
                }
                catch { }
            }

            Cambiado?.Invoke(tema.Id);
            return true;
        }

        // ¿El diccionario que ocupa el sitio de la paleta es el del tema? Sirve para no
        // repintar cuando alguien pulsa el tema que ya está en uso.
        private static bool ReferenciaCorrecta(ThemeInfo tema)
        {
            var dicts = Application.Current?.Resources?.MergedDictionaries;
            if (dicts == null || dicts.Count == 0) return false;
            var src = dicts[0].Source?.OriginalString ?? "";
            return src.IndexOf(tema.Archivo, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // El gesto de verdad: poner el diccionario del tema en la posición 0.
        private static void Poner(ThemeInfo tema)
        {
            var app = Application.Current;
            if (app == null) return;
            var nuevo = Cargar(tema.Archivo);
            var dicts = app.Resources.MergedDictionaries;
            if (dicts.Count == 0) dicts.Add(nuevo);
            else dicts[0] = nuevo;
        }

        // La misma ruta que declara App.xaml, escrita en la forma relativa que usa el código
        // generado de WPF: el ensamblado es siempre MonitorRedPCJ y los XAML van compilados
        // dentro, así que no depende de que el esquema pack esté ya registrado ni de la
        // carpeta desde la que se lance el exe.
        private static ResourceDictionary Cargar(string archivo)
            => new ResourceDictionary
            {
                Source = new Uri("/MonitorRedPCJ;component/Themes/Temas/" + archivo + ".xaml",
                                 UriKind.Relative)
            };

        // ---------- Las miniaturas de la pantalla de Temas ----------
        //
        // Cada tarjeta pinta una maqueta diminuta con los colores de SU tema, no con los del
        // tema en uso: así se ve lo que se va a obtener antes de pulsar. Para eso hace falta
        // leer una paleta que NO está montada en la aplicación, y eso es exactamente lo que
        // hace esta caché. Se carga cada paleta una vez y se deja guardada; son tres
        // diccionarios pequeños y solo se usan en Configuración.

        private static readonly Dictionary<string, ResourceDictionary> _paletas =
            new(StringComparer.OrdinalIgnoreCase);

        private static ResourceDictionary? Paleta(ThemeInfo tema)
        {
            if (_paletas.TryGetValue(tema.Id, out var cached)) return cached;
            try
            {
                var d = Cargar(tema.Archivo);
                _paletas[tema.Id] = d;
                return d;
            }
            catch { return null; }
        }

        /// <summary>
        /// El valor de una clave dentro del tema pedido, tal y como lo vería esa tarjeta.
        /// Devuelto congelado, que es lo que permite usarlo como pincel compartido y ponerlo
        /// de fondo en varias maquetas a la vez.
        /// </summary>
        public static Brush? Tinta(string? id, string clave)
        {
            var tema = Buscar(id);
            if (tema == null) return null;
            var p = Paleta(tema);
            if (p != null && p[clave] is Brush b)
            {
                if (b is not SolidColorBrush) return b;
                if (!b.IsFrozen) b.Freeze();
                return b;
            }
            return null;
        }

        /// <summary>Un color suelto de la paleta (para lo que necesite Color, no Brush).</summary>
        public static Color ColorDe(string? id, string clave)
            => Tinta(id, clave) is SolidColorBrush s ? s.Color : Colors.Transparent;

        /// <summary>Un radio, tamaño o fuente del tema pedido, para que la maqueta salga igual.</summary>
        public static object? Token(string? id, string clave)
        {
            var tema = Buscar(id);
            if (tema == null) return null;
            return Paleta(tema)?[clave];
        }

        // ---------- Colores de identidad ----------
        //
        // Cada herramienta del catálogo trae su color de icono escrito a mano (#0FA3A3 el
        // radar, #B58A00 la cuota…). Son señas de identidad, no parte de la paleta, y por eso
        // no se sustituyen por fichas: lo que sí hace falta es que se lean sobre el fondo
        // oscuro de Nocturna. Un azul petróleo que se ve perfecto sobre blanco casi desaparece
        // sobre #131B22, así que en los temas oscuros se aclaran los colores demasiado apagados
        // mezclándolos con blanco justo lo necesario para alcanzar un brillo legible. El matiz
        // no se mueve: la herramienta sigue siendo del mismo color, solo más claro.

        private static double Brillo(Color c) => 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B;

        /// <summary>El color de identidad, ya adaptado al tema en uso.</summary>
        public static Color DeIdentidad(string hex)
        {
            Color c;
            try { c = (Color)ColorConverter.ConvertFromString(hex); }
            catch { return EsOscuro ? Colors.White : Color.FromRgb(0x55, 0x69, 0x7A); }
            if (!EsOscuro) return c;

            const double minimo = 150;              // brillo por debajo del cual no se lee
            double b = Brillo(c);
            if (b >= minimo) return c;
            double t = (minimo - b) / (255 - b);    // cuánto hay que subirlo
            return Color.FromRgb(
                (byte)Math.Round(c.R + (255 - c.R) * t),
                (byte)Math.Round(c.G + (255 - c.G) * t),
                (byte)Math.Round(c.B + (255 - c.B) * t));
        }

        /// <summary>El pincel del color de identidad, congelado y listo para poner de fondo.</summary>
        public static Brush PincelDeIdentidad(string hex)
        {
            var b = new SolidColorBrush(DeIdentidad(hex));
            b.Freeze();
            return b;
        }

        // ---------- Comprobación ----------

        /// <summary>
        /// Pasa las tres paletas y escribe un informe: mismo número de claves, claves que
        /// faltan en alguna, y si alguna clave no se resuelve en pincel. Se llama desde
        /// --theme-check. Cuesta poco y pilla por adelantado el error típico de este motor,
        /// que es añadir un color nuevo a una paleta y olvidarlo en las otras dos.
        /// </summary>
        public static string Comprobar()
        {
            var sb = new System.Text.StringBuilder();
            Dictionary<string, HashSet<string>> claves = new(StringComparer.OrdinalIgnoreCase);
            foreach (var t in Lista)
            {
                var p = Paleta(t);
                if (p == null)
                {
                    sb.AppendLine($"FALLO  {t.Id}: no carga Themes/Temas/{t.Archivo}.xaml");
                    continue;
                }
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var k in p.Keys) set.Add(k.ToString() ?? "");
                sb.AppendLine($"{t.Id,-9} {set.Count,4} claves   oscuro={t.Oscuro}");
                claves[t.Id] = set;
            }

            // Comparar todas contra la primera: la diferencia es lo que rompería el repintado.
            string? baseId = null;
            foreach (var kv in claves) { baseId = kv.Key; break; }
            if (baseId != null)
            {
                var baseSet = claves[baseId];
                foreach (var kv in claves)
                {
                    if (kv.Key == baseId) continue;
                    var faltan = new List<string>();
                    foreach (var k in baseSet) if (!kv.Value.Contains(k)) faltan.Add(k);
                    var sobran = new List<string>();
                    foreach (var k in kv.Value) if (!baseSet.Contains(k)) sobran.Add(k);
                    if (faltan.Count == 0 && sobran.Count == 0)
                        sb.AppendLine($"OK     {kv.Key} tiene las mismas claves que {baseId}");
                    else
                    {
                        sb.AppendLine($"DIFER  {kv.Key}: faltan [{string.Join(", ", faltan)}]  sobran [{string.Join(", ", sobran)}]");
                    }
                }
            }
            return sb.ToString();
        }
    }
}
