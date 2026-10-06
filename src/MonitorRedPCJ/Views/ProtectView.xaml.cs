using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using MonitorRedPCJ.Models;
using MonitorRedPCJ.Native;
using MonitorRedPCJ.Services;

namespace MonitorRedPCJ.Views
{
    // Anchos compartidos de las columnas que el usuario puede ensanchar/estrechar arrastrando
    // su barra divisora: Tráfico en vivo, Peso y las pastillas de permiso. La cabecera y cada
    // fila se enlazan a estos valores, así que al arrastrar una barra se mueve toda la lista a
    // la vez (no solo la fila bajo el cursor).
    // Desde el rediseño de la 1.5.0 la columna de la APLICACIÓN ya no tiene ancho propio: es la
    // flexible de la rejilla, y es la que absorbe lo que ganen o pierdan estas tres. Por eso el
    // arrastre se puede independizar (cada tirador mueve UNA sola columna) sin que nada se
    // descuadre: al nombre le sobra con recortarse con puntos.
    public sealed class ListLayout : INotifyPropertyChanged
    {
        public static ListLayout Current { get; } = new ListLayout();
        public event PropertyChangedEventHandler? PropertyChanged;

        // Helper: aplica el valor recortado y avisa solo si cambió de verdad.
        private void Set(ref GridLength field, double value, double min, double max, string name)
        {
            double v = Math.Round(value);
            if (v < min) v = min;
            if (v > max) v = max;
            if (Math.Abs(v - field.Value) < 0.5) return;
            field = new GridLength(v);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        // Límites de cada columna, una sola vez para el arrastre y para el recorte.
        public const double TrafficMin = 96, TrafficMax = 320;
        public const double PesoMin = 40, PesoMax = 170;
        public const double PillMin = 92, PillMax = 190;

        // Ancho del par de cifras por segundo y su línea de acumulado. Los 152 de antes
        // cortaban el "GB" de las aplicaciones que llevan días moviendo tráfico, que es
        // justo el dato que se quiere leer sin ensanchar la columna a mano.
        private GridLength _trafficCol = new GridLength(168);
        public GridLength TrafficCol
        {
            get => _trafficCol;
            set => Set(ref _trafficCol, value.Value, TrafficMin, TrafficMax, nameof(TrafficCol));
        }

        private GridLength _pesoCol = new GridLength(60);
        public GridLength PesoCol
        {
            get => _pesoCol;
            set => Set(ref _pesoCol, value.Value, PesoMin, PesoMax, nameof(PesoCol));
        }

        // Ancho de CADA pastilla (Salida y Entrada comparten valor para que queden iguales).
        private GridLength _pillCol = new GridLength(118);
        public GridLength PillCol
        {
            get => _pillCol;
            set => Set(ref _pillCol, value.Value, PillMin, PillMax, nameof(PillCol));
        }
    }

    public partial class ProtectView : UserControl
    {
        // ------- Fila estable ---------------------------------------------------
        // La lista tiene más de cien filas y se repasa cada segundo y medio. Si cada repaso
        // construyera filas nuevas, WPF tiraría abajo todo el árbol visual y el desplazamiento
        // se volvería pesado. Aquí las filas son siempre las mismas objetos y solo cambian sus
        // valores, que se avisan uno a uno por INotifyPropertyChanged.
        public class Row : INotifyPropertyChanged
        {
            public event PropertyChangedEventHandler? PropertyChanged;

            public string ExePath { get; init; } = "";
            public TrackedApp? App;              // no se enlaza: solo hace falta para decidir
            public InventoryEntry? Entry;        // último dato leído, para el detalle

            private string _name = "";
            private string _folder = "";
            private ImageSource? _icon;
            private bool _hasIcon;
            private bool _running;
            private string _rx = "", _tx = "", _totalRx = "", _totalTx = "";
            private string _outText = "", _inText = "", _outTip = "", _inTip = "";
            private string _outGlyph = "", _inGlyph = "";
            private Style _outPill = null!, _inPill = null!;
            private Brush _dot = Brushes.Gray, _bar = Brushes.Gray, _risk = Brushes.Gray;
            private Brush _rail = Brushes.Gray;
            private double _frac;
            private bool _expanded;
            private string _barTip = "";
            private Visibility _detailVis = Visibility.Collapsed, _systemTag = Visibility.Collapsed;
            private string _detailTraffic = "", _detailPath = "", _detailRisk = "", _ajusteLine = "";
            private string _riskText = "", _riskTip = "";
            private Visibility _riskVis = Visibility.Collapsed;
            private Brush _riskFg = Brushes.Gray, _riskBg = Brushes.Transparent;

            public string Name { get => _name; set { if (Set(ref _name, value)) RaiseAuto(); } }
            public string Folder { get => _folder; set => Set(ref _folder, value); }
            public ImageSource? Icon { get => _icon; set => Set(ref _icon, value); }
            public bool HasIcon { get => _hasIcon; set => Set(ref _hasIcon, value); }
            public bool Running { get => _running; set => Set(ref _running, value); }

            public string Rx { get => _rx; set => Set(ref _rx, value); }
            public string Tx { get => _tx; set => Set(ref _tx, value); }
            public string TotalRx { get => _totalRx; set => Set(ref _totalRx, value); }
            public string TotalTx { get => _totalTx; set => Set(ref _totalTx, value); }

            public string OutText { get => _outText; set { if (Set(ref _outText, value)) RaiseAuto(); } }
            public string InText { get => _inText; set { if (Set(ref _inText, value)) RaiseAuto(); } }
            public string OutTip { get => _outTip; set => Set(ref _outTip, value); }
            public string InTip { get => _inTip; set => Set(ref _inTip, value); }
            public string OutGlyph { get => _outGlyph; set => Set(ref _outGlyph, value); }
            public string InGlyph { get => _inGlyph; set => Set(ref _inGlyph, value); }

            public Style OutPill { get => _outPill; set => Set(ref _outPill, value); }
            public Style InPill { get => _inPill; set => Set(ref _inPill, value); }
            public Brush Dot { get => _dot; set => Set(ref _dot, value); }
            public Brush BarBrush { get => _bar; set => Set(ref _bar, value); }
            public Brush RiskBrush { get => _risk; set => Set(ref _risk, value); }

            // Color del rail de la izquierda de la fila: el permiso de SALIDA dicho sin letras,
            // para que se pueda recorrer la lista con la vista sin leer las pastillas.
            public Brush Rail { get => _rail; set => Set(ref _rail, value); }

            // Lo que mueve esta aplicación comparado con la que más mueve, en tantos por uno.
            // La barra lo estira con Fx.Fraction, así que ya no se calcula en píxeles: al
            // ensanchar la columna de PESO la barra se alarga sola. El corte de 0,004 es el
            // equivalente a los 0,4 px que se usaban antes, para no avisar de movimientos que
            // no se ven y no tener la lista animándose sin parar.
            public double Frac
            {
                get => _frac;
                set { if (Math.Abs(_frac - value) > 0.004) Set(ref _frac, Math.Round(value, 3)); }
            }

            // Qué significa la longitud de la barra de PESO, dicho en el aviso al pasar el ratón.
            public string BarTip { get => _barTip; set => Set(ref _barTip, value); }

            // Si el detalle de la fila está desplegado: manda el giro de la flechita (Fx.Turned)
            // y, junto con DetailVisibility, que salga el recuadro.
            public bool Expanded { get => _expanded; set => Set(ref _expanded, value); }

            public Visibility DetailVisibility { get => _detailVis; set => Set(ref _detailVis, value); }
            public Visibility SystemTag { get => _systemTag; set => Set(ref _systemTag, value); }
            public string DetailTraffic { get => _detailTraffic; set => Set(ref _detailTraffic, value); }
            public string DetailPath { get => _detailPath; set => Set(ref _detailPath, value); }
            public string DetailRisk { get => _detailRisk; set => Set(ref _detailRisk, value); }

            // La configuración adicional de esta aplicación, en una línea ámbar dentro del detalle.
            // Vacío = no tiene ninguna, y el renglón no ocupa sitio.
            public string AjusteLine { get => _ajusteLine; set => Set(ref _ajusteLine, value); }

            // Etiqueta de analisis de seguridad que va justo detras del nombre: es la clasificacion
            // que antes solo se veia en la ventana de «Elegir apps». El aviso al pasar el raton
            // lleva el analisis completo (que es, y que pasa si la cortas).
            public string RiskText { get => _riskText; set => Set(ref _riskText, value); }
            public string RiskTip { get => _riskTip; set => Set(ref _riskTip, value); }
            public Visibility RiskVisibility { get => _riskVis; set => Set(ref _riskVis, value); }
            public Brush RiskFg { get => _riskFg; set => Set(ref _riskFg, value); }
            public Brush RiskBg { get => _riskBg; set => Set(ref _riskBg, value); }

            // Nombre para el lector de pantalla y para las pruebas de automatización: la
            // pastilla lleva dentro una plantilla, y sin esto su nombre sería ilegible.
            public string OutAuto => $"Salida de {Name}: {OutText}";
            public string InAuto => $"Entrada de {Name}: {InText}";

            // Glifos: mejor por código de punto que por literal, para que el archivo fuente
            // no dependa de la codificación con la que se compile.
            public static readonly string Down = char.ConvertFromUtf32(0xE70D);   // fila cerrada
            public static readonly string Up = char.ConvertFromUtf32(0xE70E);     // fila abierta

            private void RaiseAuto()
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OutAuto)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(InAuto)));
            }

            private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
            {
                if (EqualityComparer<T>.Default.Equals(field, value)) return false;
                field = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
                return true;
            }
        }

        // Los estilos y pinceles del tema se buscan una sola vez: hacer FindResource por cada
        // fila y cada repaso también cuesta. A cambio, cuando cambia el tema hay que tirarlos:
        // son pinceles concretos de la paleta vieja y las filas los siguen usando hasta que se
        // les pone otro (ver OnTemaCambia).
        private struct Skin
        {
            public Style Allow, Block, Pending;
            public Brush Green, Coral, Dim, Amber, Teal, Idle;
        }
        private Skin _skin;
        private bool _skinned;

        // Al cambiar de tema los pinceles guardados en Skin y en RiskTints son los viejos, y las
        // filas seguirían pintadas con la paleta anterior aunque todo lo demás haya cambiado.
        // Este manejador tira el cache, suelta el punto inactivo congelado y vuelve a repasar:
        // FindResource ya devuelve los pinceles de la paleta nueva.
        private void OnTemaCambia(string id)
        {
            _skinned = false;
            _idle = null;
            _riskTints = null;
            // El chip «en vivo» lleva colores pedidos a mano, no DynamicResource: hay que
            // volver a pedirlos con el tema nuevo o se quedaría con el color del anterior.
            if (IsLoaded) PintaVentana();
            if (IsLoaded) Refresh(forced: true);
        }

        // 0 = todas, 1 = las del usuario, 2 = las de Windows
        private int _group;
        private string _filter = "";

        // Orden de las cabeceras SALIDA y ENTRADA. Cada toque hace un ciclo de tres pasos:
        //   0 = el orden normal · 1 = las permitidas arriba · 2 = las bloqueadas arriba.
        // Son excluyentes entre sí: al ordenar por una dirección se suelta la otra, porque dos
        // filtros a la vez no tienen un orden que se pueda explicar.
        private int _sortOut, _sortIn;
        private bool _busy;          // interruptor general en marcha
        private bool _busyApp;       // cambio de regla por app en marcha
        private DateTime _lastBuild = DateTime.MinValue;
        private System.Windows.Threading.DispatcherTimer? _warmTimer;

        // Filas abiertas (por ruta) y menú desplegado: mientras hay un menú abierto no se
        // toca la lista, o el menú se caería en medio del clic.
        private readonly HashSet<string> _expanded = new(StringComparer.OrdinalIgnoreCase);
        private bool _menuOpen;

        // La lista se recompone solo cuando cambia el conjunto de programas (o al buscar,
        // cambiar de grupo o tocar una regla). Los repasos de tráfico no reordenan: si no,
        // las filas saltarían de sitio cada segundo y el desplazamiento se notaría.
        private List<Row> _rows = new();
        private Dictionary<string, Row> _index = new(StringComparer.OrdinalIgnoreCase);
        private bool _structureDirty = true;
        private bool _deferred;      // la pestaña estaba cerrada cuando había que repasar

        // Los iconos y el análisis de riesgo se cargan fuera del hilo de interfaz.
        private bool _iconsLoading;
        private int _iconBatches;
        private readonly Dictionary<string, RiskInfo> _riskCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _riskGate = new();

        // Lectura corta de una clasificación ya calculada; null si el repaso de fondo aún no
        // llegó a esa ruta (la fila sale entonces sin la etiqueta y se rellena sola después).
        private RiskInfo? PeekRisk(string exe)
        {
            lock (_riskGate) return _riskCache.TryGetValue(exe, out var r) ? r : null;
        }

        // Lectura con cálculo en el hilo de interfaz: solo para la fila desplegada, que es una
        // cada vez y ya está pidiendo el análisis completo.
        private RiskInfo RiskFor(string exe)
        {
            var cached = PeekRisk(exe);
            if (cached != null) return cached;
            RiskInfo info;
            try { info = AppRisk.Analyze(exe); }
            catch { info = new RiskInfo { Level = RiskLevel.Desconocido }; }
            lock (_riskGate) _riskCache[exe] = info;
            return info;
        }

        // Recuentos del repaso actual
        private int _nRunning, _nAllowed, _nCut, _nUndecided, _nOpenIn;
        // Cuántas guardadas hay fuera de la ventana de tiempo: se dice en el pie para que
        // «menos filas» no se confunda con «se ha borrado permiso alguno».
        private int _nFueraDeVentana;

        // ---------- La ventana de tiempo y el filtro de estado (1.6.2) ----------
        //
        // Los tramos de la barrita, en minutos; el último (0) es «todas». Son tramos fijos a
        // propósito: una barra continua de 1 a 999 no se puede leer de un vistazo ni se puede
        // nombrar en el dígito de al lado. El primero no es un tiempo, es un modo: «en vivo» pide
        // el repaso de procesos en cada sondeo y pinta la lista sin aplazamientos, para ver lo que
        // está pasando mientras se mira.
        const int Vivo = -1;
        static readonly int[] Ventanas = { Vivo, 1, 5, 10, 15, 30, 60, 120, 240, 0 };

        int _ventanaMin = 15;
        // 0 = sin filtro · 1 = en ejecución · 2 = permitidas · 3 = bloqueadas · 4 = sin decidir
        int _filtroEstado;
        // Mientras se monta la ventana, la barra dispara su ValueChanged con los campos aún a
        // medio poner; sin esto se llamaría a Refresh() antes de que exista la lista.
        bool _mandosListos;
        bool _cambiandoFiltro;
        System.Windows.Threading.DispatcherTimer? _refrescoTimer;

        int VentanaIndex()
        {
            int i = System.Array.IndexOf(Ventanas, _ventanaMin);
            return i < 0 ? 4 : i;   // sin valor reconocido, la barra se pone en 15 min
        }

        // Cuántos minutos de actividad cuentan. «En vivo» no es un tiempo: mira a un minuto, que
        // es lo que tarda en notarse que una app ya no está.
        int MinutosReales() => _ventanaMin == Vivo ? 1 : _ventanaMin;
        bool EnVivo => _ventanaMin == Vivo;

        string NombreVentana(int minutos) => minutos switch
        {
            Vivo => "en vivo",
            0 => "todas",
            1 => "último minuto",
            60 => "última hora",
            120 => "últimas 2 h",
            240 => "últimas 4 h",
            _ => "últimos " + minutos + " min"
        };

        // Solo el trecho, para meterlo en medio de una frase: "5 min", "2 h", "4 h".
        string DuracionVentana() => MinutosReales() switch
        {
            1 => "1 min",
            60 => "1 h",
            120 => "2 h",
            240 => "4 h",
            _ => MinutosReales() + " min"
        };

        string NombreFiltro(int estado) => estado switch
        {
            1 => "en ejecución",
            2 => "permitidas",
            3 => "bloqueadas",
            _ => "sin decidir"
        };

        // Está la app dentro de la ventana de tiempo: abierta ahora, o vista/conectando desde
        // el corte. Con corte a DateTime.MinValue (la última posición, «todas») pasa todo.
        static bool Viva(InventoryEntry e, DateTime corteUtc)
            => e.Running || (e.App != null &&
                             (e.App.LastAliveUtc >= corteUtc || e.App.LastEgressUtc >= corteUtc));

        // A qué chip de estado pertenece una fila. Son cuatro categorías sueltas, no una
        // partición: una app en marcha suele estar además permitida, y por eso los cuatro
        // números no suman el total de la lista.
        bool PasaFiltro(InventoryEntry e, int estado, HashSet<string> snapshot)
        {
            if (estado == 0) return true;
            if (estado == 1) return e.Running;
            var d = App.Firewall.GetDecision(e.ExePath, Direction.Out, snapshot);
            return estado switch
            {
                2 => d == Decision.Permitido,
                3 => d == Decision.Bloqueado,
                _ => d != Decision.Permitido && d != Decision.Bloqueado,
            };
        }

        // Los cuatro recuentos de la franja de arriba, sobre el grupo que se está enseñando y
        // antes del chip elegido: pinchar un filtro no debe cambiar las cifras que filtra.
        private void CuentaEstados(List<InventoryEntry> entries, HashSet<string> snapshot)
        {
            foreach (var e in entries)
            {
                if (e.Running) _nRunning++;
                var outD = App.Firewall.GetDecision(e.ExePath, Direction.Out, snapshot);
                if (outD == Decision.Permitido) _nAllowed++;
                else if (outD == Decision.Bloqueado) _nCut++;
                else _nUndecided++;
                if (App.Firewall.GetDecision(e.ExePath, Direction.In, snapshot) == Decision.Permitido)
                    _nOpenIn++;
            }
        }

        // El chip de la derecha de la barra. «En vivo» se pinta con el color del acento, para que
        // se vea de reojo que la lista se está refrescando sola sin tener que leer el texto.
        void PintaVentana()
        {
            TxtVentana.Text = NombreVentana(_ventanaMin);
            try
            {
                if (EnVivo)
                {
                    ChipVentana.BorderBrush = (Brush)FindResource("B.Accent");
                    ChipVentana.Background = (Brush)FindResource("B.Selected");
                    TxtVentana.Foreground = (Brush)FindResource("B.Accent");
                }
                else
                {
                    // Sin valor propio vuelven a mandar los DynamicResource del XAML, así que el chip
                    // se repinta solo con el tema si se cambia mientras está puesto.
                    ChipVentana.ClearValue(Border.BorderBrushProperty);
                    ChipVentana.ClearValue(Border.BackgroundProperty);
                    TxtVentana.ClearValue(TextBlock.ForegroundProperty);
                }
            }
            catch { }   // tema aún sin cargar: el texto vale, el adorno del color se pierde un repaso
        }

        // ── Que la cabecera no se monte sobre sí misma ──────────────────────────────
        // A la izquierda van el título, Refrescar, la barra de minutos y su chip; a la derecha
        // el selector de grupo y el buscador. La rejilla ya hace que el único que se apriete sea
        // el título, pero apretar tiene un suelo: su MinWidth. Pasado ese suelo la mitad
        // izquierda desborda su columna —WPF no recorta los Grid por defecto— y el chip vuelve a
        // plantarse encima de «Todas / Mis apps», que es justo lo que vio el autor. Como el ancho
        // mínimo de la aplicación (900) queda por debajo de lo que piden las piezas enteras, aquí
        // se suelta lastre por pasos: primero la palabra «Refrescar» (el icono se queda, que es
        // lo que se reconoce), y después la barra de minutos y el buscador, que admiten medirse.
        // Se calcula con los anchos reales de cada elemento, no con umbrales de ventana a ojo:
        // las etiquetas de la barra cambian de tamaño («en vivo» frente a «últimos 15 min») y el
        // selector de grupo se ensancha con el recuento entre paréntesis.
        const double AnchoBarra = 132, AnchoBarraEstrecha = 104;
        const double AnchoBuscador = 228, AnchoBuscadorEstrecho = 168;
        int _lastre = -1;
        double _wRefrescar = 0;   // el botón con la palabra puesta, medido una sola vez

        private void OnCabeceraAjuste(object sender, SizeChangedEventArgs e) => PedirAjuste();

        bool _ajustePedido;

        // El arreglo se pide, no se hace en el mismo medi: dentro de SizeChanged los anchos que
        // interesan (el chip con el texto nuevo, la pestaña con el recuento nuevo) todavía son los
        // del paso anterior. Esperando al fin del repaso se lee lo que ya está medido.
        void PedirAjuste()
        {
            if (_ajustePedido) return;
            _ajustePedido = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _ajustePedido = false;
                AflojaCabecera();
                AflojaBanda();
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        // Ancho que ocupa un elemento con sus márgenes, tal y como lo pidió en el último medi.
        static double Peso(FrameworkElement el) =>
            el == null ? 0 : el.DesiredSize.Width + el.Margin.Left + el.Margin.Right;

        void AflojaCabecera()
        {
            if (CabeceraLista == null || CabeceraDer == null || TxtCount == null) return;
            // Antes de tocar nada, con la franja entera a la vista: así el botón se mide con su
            // palabra. Es el único momento bueno, porque en cuanto se guarda ya no se mide.
            if (TxtRefrescar.Visibility == Visibility.Visible && BtnRefrescar.DesiredSize.Width > 0)
                _wRefrescar = Peso(BtnRefrescar);
            if (SegTile.DesiredSize.Width < 20 || _wRefrescar < 20) return;

            double libre = CabeceraLista.ActualWidth;
            int nivel = 0;
            while (nivel < 2 && Izquierda(nivel) > libre - Derecha(nivel)) nivel++;
            AplicaLastre(nivel);
        }

        // Lo que pediría la mitad izquierda en el paso «nivel» de lastre, contando el título en su
        // MinWidth: el peor caso, porque un título ya recortado es el que empuja a lo demás.
        double Izquierda(int nivel)
        {
            double refrescar = nivel >= 1
                ? BtnRefrescar.Padding.Left + BtnRefrescar.Padding.Right + IconoRefrescar.DesiredSize.Width
                : _wRefrescar;
            double barra = (nivel >= 2 ? AnchoBarraEstrecha : AnchoBarra) + BarraVentana.Margin.Left;
            return Peso(IzqTile) + CabeceraIzq.ColumnDefinitions[1].MinWidth + TxtCount.Margin.Left
                   + refrescar + barra + Peso(ChipVentana) + Peso(Colchon);
        }

        // La mitad derecha: las pestañas van contando apps («Todas (105)»), así que su ancho se
        // lee en vivo; del buscador basta la cifra que se le va a poner en cada paso.
        double Derecha(int nivel) =>
            Peso(SegTile) + (nivel >= 2 ? AnchoBuscadorEstrecho : AnchoBuscador);

        void AplicaLastre(int nivel)
        {
            if (nivel == _lastre) return;
            _lastre = nivel;

            TxtRefrescar.Visibility = nivel >= 1 ? Visibility.Collapsed : Visibility.Visible;
            IconoRefrescar.Margin = new Thickness(0, 0, nivel >= 1 ? 0 : 6, 0);
            BarraVentana.Width = nivel >= 2 ? AnchoBarraEstrecha : AnchoBarra;
            SearchHost.Width = nivel >= 2 ? AnchoBuscadorEstrecho : AnchoBuscador;
        }

        // ── Y que la banda de mando tampoco se monte sobre los botones ───────────────
        // A la izquierda van el escudo, el título, su chip «en directo» y la línea de estado; a
        // la derecha los cinco mandos. Desde que la identidad es una rejilla (1.6.9) lo único
        // que se aprieta es el texto, pero apretar tiene un suelo: el título entero. Con la
        // ventana estrecha el chip se plantaba debajo de «Elegir apps» —lo vio el autor en su
        // pantalla— y a los 900 px el título se quedaba en «Protec». Aquí se suelta lastre por
        // pasos, con los anchos reales de cada pieza y no con umbrales a ojo, porque los mandos
        // cambian de tamaño solos: «Repreguntar» sale según el modo y la etiqueta del botón de
        // modo mide distinto en cada modo.
        //   paso 1: la palabra «Bloqueo total». El interruptor se queda con su «Activo»/«Apagado»
        //           pegado, y el botón del modo ya dice justo antes qué modo está puesto.
        //   paso 2: el chip «en directo». Lo que anuncia (que la pestaña está viva) lo sigue
        //           diciendo el punto verde que late dentro del escudo.
        //   paso 3: el título pasa a «Protección», que es lo que representa el escudo. Sin esto,
        //           a los 900 px de ancho mínimo la letra se cortaba a media palabra.
        int _lastreBanda = -1;
        double _wPalabra = 0;   // «Bloqueo total» medida con la palabra puesta
        double _wChip = 0;      // el chip «en directo» medido con el chip puesto
        double _wTituloLargo = 0, _wTituloCorto = 0;
        const string TituloLargo = "Protección del firewall", TituloCorto = "Protección";
        const double AnchoEscudo = 40, HuecoEscudo = 12, ColchonBanda = 14;

        void AflojaBanda()
        {
            if (BandaGrid == null || BandaDer == null || TxtBandaTitulo == null ||
                TxtPalabraBloqueo == null || ChipDirecto == null) return;
            // Antes de tocar nada, con las piezas puestas: escondidas ya no se miden, y con el
            // título corto no se mide el largo.
            if (TxtPalabraBloqueo.Visibility == Visibility.Visible &&
                TxtPalabraBloqueo.DesiredSize.Width > 0) _wPalabra = Peso(TxtPalabraBloqueo);
            if (ChipDirecto.Visibility == Visibility.Visible &&
                ChipDirecto.DesiredSize.Width > 0) _wChip = Peso(ChipDirecto);
            if (TxtBandaTitulo.Text == TituloLargo && TxtBandaTitulo.DesiredSize.Width > 0)
                _wTituloLargo = Peso(TxtBandaTitulo);
            if (TxtBandaTitulo.Text == TituloCorto && TxtBandaTitulo.DesiredSize.Width > 0)
                _wTituloCorto = Peso(TxtBandaTitulo);
            double total = BandaGrid.ActualWidth;
            if (total < 60 || _wPalabra < 20 || _wChip < 20 || _wTituloLargo < 20) return;

            // Las cuentas se hacen siempre con las piezas PUESTAS, aunque ahora mismo estén
            // escondidas: si se midiera lo que hay, soltar lastre ensancharía la banda, el
            // siguiente repaso creería que sobra sitio y la devolvería, y la banda se pasaría el
            // día parpadeando.
            double mandos = Peso(BandaDer) +
                            (TxtPalabraBloqueo.Visibility == Visibility.Visible ? 0 : _wPalabra);
            if (mandos < 20) return;
            double tituloCorto = _wTituloCorto > 20 ? _wTituloCorto : _wTituloLargo * 0.45;

            // Lo que pide la identidad en cada paso: escudo, hueco, título y —hasta el paso 2—
            // el chip «en directo».
            double Pide(int nivel) => AnchoEscudo + HuecoEscudo +
                (nivel >= 3 ? tituloCorto : _wTituloLargo) + (nivel >= 2 ? 0 : _wChip);
            // Lo que dejan los mandos en cada paso.
            double Deja(int nivel) => total - (mandos - (nivel >= 1 ? _wPalabra : 0));

            int nivel = 0;
            while (nivel < 3 && Pide(nivel) + ColchonBanda > Deja(nivel)) nivel++;
            AplicaLastreBanda(nivel);
        }

        void AplicaLastreBanda(int nivel)
        {
            if (nivel == _lastreBanda) return;
            _lastreBanda = nivel;
            TxtPalabraBloqueo.Visibility = nivel >= 1 ? Visibility.Collapsed : Visibility.Visible;
            ChipDirecto.Visibility = nivel >= 2 ? Visibility.Collapsed : Visibility.Visible;
            TxtBandaTitulo.Text = nivel >= 3 ? TituloCorto : TituloLargo;
        }

        private void OnVentanaMinutos(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_mandosListos) return;
            int i = (int)Math.Round(e.NewValue);
            if (i < 0 || i >= Ventanas.Length) return;
            if (Ventanas[i] == _ventanaMin) return;
            _ventanaMin = Ventanas[i];
            App.Settings.Current.ProtectWindowMinutes = _ventanaMin;
            App.Settings.Save();
            PintaVentana();
            RefreshNow();
        }

        // Los cuatro recuentos son además los cuatro filtros: uno elegido suelta los demás, y
        // volver a pinchar el mismo lo quita.
        private void OnFiltroEstado(object sender, RoutedEventArgs e)
        {
            if (!_mandosListos || _cambiandoFiltro) return;
            var chip = (System.Windows.Controls.Primitives.ToggleButton)sender;

            int cual = chip == FiltroRun ? 1 : chip == FiltroOk ? 2 :
                       chip == FiltroCut ? 3 : chip == FiltroWait ? 4 : 0;

            _cambiandoFiltro = true;
            if (chip.IsChecked == true)
            {
                _filtroEstado = cual;
                foreach (var otro in new[] { FiltroRun, FiltroOk, FiltroCut, FiltroWait })
                    if (otro != chip) otro.IsChecked = false;
            }
            else if (_filtroEstado == cual)
            {
                _filtroEstado = 0;
            }
            _cambiandoFiltro = false;

            App.Settings.Current.ProtectFiltroEstado = _filtroEstado;
            App.Settings.Save();
            RefreshNow();
        }

        // Refrescar a mano: repaso de procesos ya, reglas del firewall caducadas y un vistazo
        // nuevo a la lista. El icono da media vuelta mientras trabaja, que es la única señal de
        // que el botón hizo algo (todo esto tarda menos de un segundo).
        private void OnRefrescar(object sender, RoutedEventArgs e)
        {
            App.Traffic.PedirRepaso();
            App.Firewall.InvalidarReglas();
            Task.Run(() => App.Firewall.WarmCaches());

            try
            {
                var anim = new System.Windows.Media.Animation.DoubleAnimation(0, 360,
                    TimeSpan.FromMilliseconds(600))
                {
                    EasingFunction = new System.Windows.Media.Animation.CircleEase
                    {
                        EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
                    }
                };
                // Al terminar se quita la animación (si no, se queda retenida en 360 y ya no
                // valdría ponerle un Angle a mano) y se vuelve a cero para la siguiente vuelta.
                anim.Completed += (_, _) =>
                {
                    GiroRefrescar.BeginAnimation(
                        System.Windows.Media.RotateTransform.AngleProperty, null);
                    GiroRefrescar.Angle = 0;
                };
                GiroRefrescar.BeginAnimation(
                    System.Windows.Media.RotateTransform.AngleProperty, anim);
            }
            catch { }

            // El repaso de procesos cae en el siguiente sondeo (un segundo), y la lectura de
            // reglas llega de segundo plano: se refresca dos veces para no dejar la lista a
            // medias. La segunda ya trae lo nuevo.
            RefreshNow();
            _refrescoTimer?.Stop();
            _refrescoTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1600)
            };
            _refrescoTimer.Tick += (s, ev) =>
            {
                _refrescoTimer?.Stop();
                App.Firewall.InvalidarReglas();
                RefreshNow();
            };
            _refrescoTimer.Start();
        }

        public ProtectView()
        {
            InitializeComponent();
            App.Traffic.DataUpdated += OnUpdate;
            ThemeService.Cambiado += OnTemaCambia;
            Unloaded += (s, e) =>
            {
                App.Traffic.DataUpdated -= OnUpdate;
                ThemeService.Cambiado -= OnTemaCambia;
                // El repaso de procesos cada sondeo se apaga al descolgar la pestaña: si no,
                // el «en vivo» seguiría costando CPU indefinido aunque nadie lo mire.
                App.Traffic.RepasarCadaVuelta = false;
                _warmTimer?.Stop();
                _warmTimer = null;
                _searchTimer?.Stop();
                _searchTimer = null;
                _refrescoTimer?.Stop();
                _refrescoTimer = null;
                _typing = false;
            };

            // La pastilla del selector de grupo tiene que ponerse encima del tramo activo en
            // cuanto la tarjeta tiene tamaño: antes de medir no se sabe dónde cae cada botón.
            Loaded += (s, e) => MoveSeg(false);
            SegHost.SizeChanged += (s, e) => MoveSeg(false);

            // La franja de cabecera se aprieta con la ventana (OnCabeceraAjuste), pero también le
            // afecta lo que no depende del ancho: un chip «últimas 4 h» gasta casi 30 px más que
            // «en vivo», y las pestañas se ensanchan cuando sube el recuento entre paréntesis. Por
            // eso estos dos también avisan.
            ChipVentana.SizeChanged += (s, e) => PedirAjuste();
            SegTile.SizeChanged += (s, e) => PedirAjuste();
            Loaded += (s, e) => PedirAjuste();

            // La banda de mando (AflojaBanda) se aprieta con la ventana, pero le afectan sobre
            // todo los mandos: «Repreguntar» sale y entra según el modo, y la etiqueta del botón
            // de modo mide distinto en cada modo. Por eso avisan también los dos.
            BandaGrid.SizeChanged += (s, e) => PedirAjuste();
            BandaDer.SizeChanged += (s, e) => PedirAjuste();

            // La cabecera debe encajar con la lista aunque aparezca la barra de desplazamiento.
            // Se averigua con el propio evento de desplazamiento: así no hace falta forzar un
            // UpdateLayout (que congela la interfaz) cada repaso.
            AppsList.AddHandler(ScrollViewer.ScrollChangedEvent,
                new ScrollChangedEventHandler(OnListScrollChanged));

            // Solo se pinta cuando la pestaña está a la vista: si está cerrada se aplaza el
            // repaso y se pone al día al abrirla. El repaso va un tick después del clic
            // (prioridad Loaded) para que cambiar de pestaña se note instantáneo: si se
            // hiciera dentro del propio clic, la ventana se queda parada ~200 ms.
            IsVisibleChanged += (s, e) =>
            {
                if (!IsVisible || !_deferred) return;
                _deferred = false;
                Dispatcher.BeginInvoke(new Action(() => Refresh(forced: true)),
                    System.Windows.Threading.DispatcherPriority.Loaded);
            };

            // Leer el firewall (COM) y la política de salida (netsh) tarda; se hace en segundo
            // plano para que la lista no se congele. Se lanza ya, sin esperar al primer tick.
            Task.Run(() => App.Firewall.WarmCaches());
            _warmTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(12)
            };
            _warmTimer.Tick += (s, e) => Task.Run(() => App.Firewall.WarmCaches());
            _warmTimer.Start();

            _legendOpen = App.Settings.Current.ShowAccessLegend;
            ApplyLegend();

            // La ventana de minutos y el filtro de estado son un ajuste más: se leen antes de
            // pintar nada, para que la primera lista ya salga como el usuario la dejó. Poner
            // Value e IsChecked aquí dispara sus eventos, pero con _mandosListos a false no
            // hacen nada (y no hace falta: la primera Refresh real va justo después).
            int w = App.Settings.Current.ProtectWindowMinutes;
            _ventanaMin = System.Array.IndexOf(Ventanas, w) < 0 ? 15 : w;
            BarraVentana.Value = VentanaIndex();
            PintaVentana();

            int est = App.Settings.Current.ProtectFiltroEstado;
            _filtroEstado = est >= 0 && est <= 4 ? est : 0;
            FiltroRun.IsChecked = _filtroEstado == 1;
            FiltroOk.IsChecked = _filtroEstado == 2;
            FiltroCut.IsChecked = _filtroEstado == 3;
            FiltroWait.IsChecked = _filtroEstado == 4;

            UpdateSortHeaders();
            UpdateHeader();
            Refresh(forced: true);
            _mandosListos = true;
        }

        // ---------- Barras divisoras de columnas (Tráfico / Peso / Pastillas) ----------
        // Cada tirador redimensiona SU columna y nada más: lo que gana o pierde esa columna lo
        // absorbe la de la APLICACIÓN, que es la flexible de la rejilla y siempre puede recortarse
        // con puntos. Así el borde derecho de todo lo que está a la izquierda del tirador se queda
        // quieto, que es lo que se espera de un separador de columnas, sin tener que compensar
        // pares de anchos como se hacía antes.
        private bool _splitDragging;
        private double _splitStartX;
        private string _splitProp = "Traffic";
        private double _startTraffic, _startPeso, _startPill;

        private void OnSplitDown(object sender, MouseButtonEventArgs e)
        {
            _splitProp = (sender as FrameworkElement)?.Tag as string ?? "Traffic";
            _splitDragging = true;
            _splitStartX = e.GetPosition(null).X;
            var L = ListLayout.Current;
            _startTraffic = L.TrafficCol.Value;
            _startPeso = L.PesoCol.Value;
            _startPill = L.PillCol.Value;
            (sender as IInputElement)?.CaptureMouse();
            e.Handled = true;
        }

        private void OnSplitMove(object sender, MouseEventArgs e)
        {
            if (!_splitDragging) return;
            double dx = e.GetPosition(null).X - _splitStartX;
            var L = ListLayout.Current;
            switch (_splitProp)
            {
                case "Peso":
                    L.PesoCol = new GridLength(_startPeso + dx);
                    break;
                case "Pill":
                    L.PillCol = new GridLength(_startPill + dx);
                    break;
                default:
                    L.TrafficCol = new GridLength(_startTraffic + dx);
                    break;
            }
            e.Handled = true;
        }

        private void OnSplitUp(object sender, MouseButtonEventArgs e)
        {
            if (!_splitDragging) return;
            _splitDragging = false;
            (sender as IInputElement)?.ReleaseMouseCapture();
            e.Handled = true;
        }

        // ---------- Encabezado ----------

        private void UpdateHeader()
        {
            bool strict = App.Settings.Current.StrictMode;
            int permitidas = App.Firewall.AllowRuleCount();
            int cortadas = App.Firewall.BlockRuleCount();

            // Los dos mandos se pintan desde el estado real, no desde lo que se pulsó: así
            // si el firewall no cambió (UAC cancelado), la interfaz no miente.
            int modo = CurrentMode();
            if (modo != 0) _ultimoModoCortado = modo;
            SwStrict.IsChecked = strict;
            SwLabel.Text = strict ? "Activo" : "Apagado";
            SwLabel.Foreground = (Brush)FindResource(strict ? "B.Green" : "B.TextDim");
            ModeLabel.Text = ModeName(modo);
            ModeIcon.Text = ModeGlyph(modo);
            ModeIcon.Foreground = (Brush)FindResource(modo == 0 ? "B.Teal" : (modo == 1 ? "B.Green" : "B.Coral"));

            // Repasar permisos solo tiene sentido con el corte activo; sin él, cada fila ya
            // tiene su botón de bloquear/permitir.
            BtnAccess.Visibility = strict ? Visibility.Visible : Visibility.Collapsed;
            BtnReask.Visibility = strict ? Visibility.Visible : Visibility.Collapsed;

            // Quedan avisos en la cola esperando su turno (salen de uno en uno). Se llaman
            // «avisos», no «reglas por decidir»: el chip de «sin decidir» de abajo cuenta otra
            // cosa —con el corte puesto, todo lo que no tiene permiso ya está bloqueado—, y si
            // las dos cifras usaran la misma palabra parecería que una de las dos miente.
            int pendientes = App.Traffic.AvisosEnCola;

            TxtStatus.Text = strict
                ? (modo == 2
                    ? $"Bloqueo total sin avisos — solo salen las {permitidas} {RuleWord(permitidas)} con permiso; lo demás se corta sin preguntar"
                    : (pendientes > 0
                        ? $"Bloqueo total activo — {permitidas} {RuleWord(permitidas)} con permiso y {pendientes} {(pendientes == 1 ? "aviso" : "avisos")} esperando respuesta"
                        : "Bloqueo total activo — Windows corta la salida y se pregunta por cada programa sin permiso"))
                : (cortadas == 0
                    ? "Sin bloqueo total — cada aplicación sale libremente salvo las que bloquees tú"
                    : $"Sin bloqueo total — {cortadas} {RuleWord(cortadas)} con la salida cortada");
            ShieldIcon.Foreground = (Brush)FindResource(strict ? "B.Green" : "B.Teal");

            // Avisos que el monitor no puede ver por sí solo: los que corren elevados no
            // dicen su ruta, y por eso no aparecen ni se puede decidir sobre ellos.
            int ocultos = App.Traffic.HiddenProcessCount;
            if (ocultos > 0 && !FirewallService.IsAdmin)
            {
                var names = App.Traffic.HiddenProcessNames();
                string ejemplo = names.Count == 0 ? "" : " (por ejemplo: " + string.Join(", ", names.Take(4)) + ")";
                TxtHidden.Text = $"{ocultos} programas abiertos no informan su ruta a este monitor" +
                    (ejemplo.Length == 0 ? "" : ejemplo) + ": corren como administrador o están " +
                    "protegidos, así que no salen en la lista ni lanzan el aviso. Reinicia PCJ como " +
                    "administrador para verlos, o usa «Añadir aplicación…» y elige su .exe.";
                HiddenNotice.Visibility = Visibility.Visible;
                BtnElevate.Visibility = Visibility.Visible;
            }
            else
            {
                HiddenNotice.Visibility = Visibility.Collapsed;
            }
        }

        private static string RuleWord(int n) => n == 1 ? "regla" : "reglas";

        // ---------- Los dos mandos del firewall ----------

        // El modo no se guarda en un sitio nuevo: se deduce de los dos ajustes que ya existían,
        // para que el desplegable, el interruptor, la bandeja y Configuración no puedan
        // contradecirse entre sí.
        //   0 · Avisar sin cortar   -> StrictMode = false
        //   1 · Preguntar           -> StrictMode = true  y avisos activos
        //   2 · Bloquear a todos    -> StrictMode = true  y avisos en pausa
        private static int CurrentMode()
            => !App.Settings.Current.StrictMode ? 0
               : (App.Settings.Current.AlertsEnabled ? 1 : 2);

        private static string ModeName(int m) => m == 0 ? "Avisar sin cortar" : (m == 1 ? "Preguntar para conectar" : "Bloquear a todos");

        private static string ModeGlyph(int m) => m == 0 ? "" : (m == 1 ? "" : "");

        // Con el corte ya quitado, el interruptor necesita saber a qué modo volver.
        private int _ultimoModoCortado = 1;

        private void OnModeButton(object sender, RoutedEventArgs e)
        {
            ModePopup.IsOpen = !ModePopup.IsOpen;
        }

        private async void OnPickMode(object sender, RoutedEventArgs e)
        {
            ModePopup.IsOpen = false;
            if (sender is not Button b || b.Tag is not string tag) return;
            if (!int.TryParse(tag, out int wanted)) return;
            if (wanted == CurrentMode()) return;
            await ApplyModeAsync(wanted);
        }

        private async void OnSwitch(object sender, RoutedEventArgs e)
        {
            // El interruptor es el atajo de un toque: apagar deja el modo 0, encender
            // recupera el último modo de corte que tenía.
            int wanted = SwStrict.IsChecked == true ? _ultimoModoCortado : 0;
            if (wanted == CurrentMode()) return;
            await ApplyModeAsync(wanted);
        }

        private async Task ApplyModeAsync(int wanted)
        {
            if (_busy) { UpdateHeader(); return; }

            bool strictAhora = App.Settings.Current.StrictMode;
            bool strictQuiere = wanted != 0;

            if (strictQuiere && !strictAhora)
            {
                // Entrar en bloqueo total: se activa de una vez, sin ventana intermedia. La
                // lista de permiso es la automatica de siempre (lo esencial para tener red, el
                // navegador y lo que ya tuviera salida); despues se repasa app por app con
                // «Elegir apps», que es el panel que queda visible con el corte puesto.
                _busy = true;
                SetMandosEnabled(false);
                bool ok = false;
                int nAllowed = 0;
                await Task.Run(() =>
                {
                    var allowed = AppInventory.SuggestedAllowed();
                    nAllowed = allowed.Count;
                    ok = App.Firewall.SetStrictMode(true, allowed);
                });
                SetMandosEnabled(true);
                _busy = false;

                if (!ok || !App.Settings.Current.StrictMode)
                {
                    string why = FirewallService.LastError;
                    App.Events.Add(EventKind.Info, "No se pudo activar el bloqueo total: " + why, "");
                    MessageBox.Show("No se pudo activar el bloqueo total.\n\n" + why +
                            "\n\nHace falta aceptar la ventana de permisos de administrador; si tu " +
                            "cuenta no es de administrador, abre el programa con «Ejecutar como administrador».",
                        "Monitor de Red PCJ", MessageBoxButton.OK, MessageBoxImage.Warning);
                    UpdateHeader();
                    return;
                }

                App.Events.Add(EventKind.ProtectionToggled,
                    $"Bloqueo total activado — {nAllowed} aplicaciones con salida permitida", "", important: true);
                if (!string.IsNullOrEmpty(FirewallService.LastWarning))
                    App.Events.Add(EventKind.Info,
                        "Sin permiso de salida para: " + FirewallService.LastWarning, "");
            }
            else if (!strictQuiere && strictAhora)
            {
                // Desactivar: devolver la salida a Windows y borrar nuestros permisos.
                int permitidas = App.Firewall.AllowRuleCount();
                var confirm = MessageBox.Show(
                    "Esto quita el bloqueo total: Windows volverá a permitir toda la salida y se " +
                    "borrarán las " + permitidas + " reglas de permiso.\n\n¿Desactivar el bloqueo total?",
                    "Monitor de Red PCJ", MessageBoxButton.YesNo, MessageBoxImage.Question,
                    MessageBoxResult.No);
                if (confirm != MessageBoxResult.Yes) { UpdateHeader(); return; }

                _busy = true;
                SetMandosEnabled(false);
                bool ok = await Task.Run(() => App.Firewall.SetStrictMode(false));
                SetMandosEnabled(true);
                _busy = false;

                if (!ok)
                {
                    string why = FirewallService.LastError;
                    App.Events.Add(EventKind.Info, "No se pudo desactivar el bloqueo total: " + why, "");
                    MessageBox.Show("No se pudo desactivar el bloqueo total.\n\n" + why +
                            "\n\nSi tu cuenta no es de administrador, abre Monitor de Red PCJ con «Ejecutar como administrador».",
                        "Monitor de Red PCJ", MessageBoxButton.OK, MessageBoxImage.Warning);
                    UpdateHeader();
                    return;
                }
                App.Events.Add(EventKind.ProtectionToggled, "Bloqueo total desactivado: salida libre otra vez", "", important: true);
            }

            // Con el corte ya en su sitio, pasar de 1 a 2 (o al revés) solo es pausar los avisos.
            App.Settings.Current.AlertsEnabled = wanted != 2;
            App.Settings.Save();
            if (strictQuiere) _ultimoModoCortado = wanted;
            _structureDirty = true;
            Refresh(forced: true);
        }

        private void SetMandosEnabled(bool on)
        {
            SwStrict.IsEnabled = on;
            BtnMode.IsEnabled = on;
        }

        // La cuarta opción del menú de modos: olvidar todo lo respondido para que PCJ vuelva a
        // preguntar aplicación por aplicación. Primero confirma con las cifras exactas y no toca
        // nada hasta que se acepta. No cambia el modo: lo que se borra son las decisiones, no la
        // forma de funcionar que el usuario ya eligió.
        private async void OnForgetDecisions(object sender, RoutedEventArgs e)
        {
            ModePopup.IsOpen = false;
            if (_busy) { UpdateHeader(); return; }

            int marcas = App.Firewall.DecisionCount();
            int reglas = App.Firewall.DecisionRulesCount();
            int contestadas = App.Traffic.OlvidarMarcasDeApps(soloContar: true);

            string aviso = CurrentMode() == 2
                ? "\n\nEstás en «Bloquear a todos», con los avisos en pausa: al borrarlas no va a " +
                  "preguntar nadie y las aplicaciones se quedarán sin salida hasta que les des " +
                  "permiso a mano. Elige antes «Preguntar para conectar» si quieres ir " +
                  "respondiéndolas una por una."
                : "";

            var confirm = MessageBox.Show(
                "Se borrarán las " + marcas + " " + (marcas == 1 ? "decisión" : "decisiones") +
                " guardadas y las " + reglas + " " + RuleWord(reglas) + " del firewall que las " +
                "acompañan. PCJ volverá a preguntar por las " + contestadas +
                (contestadas == 1 ? " aplicación" : " aplicaciones") + " a las que ya habías " +
                "respondido.\n\nSe conservan la salida del propio monitor, las excepciones del " +
                "sistema, los bloqueos por puerto, la configuración adicional de cada aplicación " +
                "y todo el tráfico medido." + aviso + "\n\n¿Borrar todas las decisiones?",
                "Monitor de Red PCJ", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (confirm != MessageBoxResult.Yes) return;

            _busy = true;
            SetMandosEnabled(false);
            bool ok = await Task.Run(() => App.Firewall.ForgetDecisions());
            if (ok)
            {
                // Las marcas de las fichas van después: con los marcadores del firewall ya
                // borrados es cuando «sin decisión» y «sin contestar» significan lo mismo.
                App.Traffic.OlvidarMarcasDeApps();
                // Con el corte puesto, los que están abiertos ahora se vuelven a preguntar en
                //seguida (la cola los saca de uno en uno). Sin corte se espera a que arranquen.
                if (App.Settings.Current.StrictMode && App.Settings.Current.AlertsEnabled)
                    App.Traffic.VolverAPreguntarAbiertos();
            }
            SetMandosEnabled(true);
            _busy = false;

            if (!ok)
            {
                string why = FirewallService.LastError;
                App.Events.Add(EventKind.Info, "No se pudieron borrar las decisiones: " + why, "");
                MessageBox.Show("No se pudieron borrar las decisiones.\n\n" + why +
                        "\n\nReintenta aceptando la ventana de aprobación de administrador.",
                    "Monitor de Red PCJ", MessageBoxButton.OK, MessageBoxImage.Warning);
                UpdateHeader();
                return;
            }

            App.Events.Add(EventKind.RuleChanged,
                $"Monitor de Red PCJ borró las {marcas} decisiones guardadas: volverá a preguntar " +
                "por cada aplicación", "", true);
            if (!string.IsNullOrEmpty(FirewallService.LastWarning))
                App.Events.Add(EventKind.Info,
                    "Al borrar las decisiones quedó esto sin quitar: " + FirewallService.LastWarning, "");
            _structureDirty = true;
            Refresh(forced: true);
        }

        private async void OnCleanup(object sender, RoutedEventArgs e)
        {
            BtnCleanup.IsEnabled = false;
            int n = await Task.Run(() => App.Firewall.CleanupStaleRules());
            BtnCleanup.IsEnabled = true;

            string msg = n < 0
                ? "Hace falta permisos de administrador para revisar las reglas del firewall.\n\n" +
                  "Usa «Reiniciar como administrador» en el icono de la bandeja y vuelve a intentarlo."
                : (n == 0 ? "No hay reglas obsoletas." : $"Se eliminaron {n} reglas obsoletas.");
            MessageBox.Show(msg, "Limpiar reglas", MessageBoxButton.OK,
                n < 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
            _structureDirty = true;
            Refresh(forced: true);
        }

        private void OnOpenAccess(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            var wizard = new AccessWindow { Owner = Window.GetWindow(this) };
            wizard.ShowDialog();
            _structureDirty = true;
            Refresh(forced: true);
        }

        // Añadir un programa a mano: es la salida cuando el monitor no lo puede ver (porque
        // corre elevado) o cuando nunca llegó a conectar por el bloqueo total.
        private void OnAddApp(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Elige el programa al que quieres darle o quitarle la salida",
                Filter = "Aplicaciones (*.exe)|*.exe|Todos los archivos (*.*)|*.*",
                CheckFileExists = true,
            };
            if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;

            string exe = dlg.FileName;
            try
            {
                var app = App.Traffic.NoteManualApp(exe);
                App.Events.Add(EventKind.Info, $"{app.Name}: añadido a mano a la lista de Protección", exe);

                // Se pregunta enseguida, con la misma ventana flotante de siempre.
                var alert = new AlertWindow(app, "", manual: true) { Owner = Window.GetWindow(this) };
                alert.Show();
            }
            catch (Exception ex)
            {
                MessageBox.Show("No se pudo añadir ese programa: " + ex.Message,
                    "Monitor de Red PCJ", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            _structureDirty = true;
            Refresh(forced: true);
        }

        // «Repreguntar»: mete otra vez en la cola los programas abiertos que no tienen decisión.
        // Hace falta cuando se fueron cerrando avisos sin responder: sin esto se quedarían cortados
        // y sin pregunta hasta cerrarlos y volverlos a abrir.
        private void OnReask(object sender, RoutedEventArgs e)
        {
            if (!App.Settings.Current.StrictMode) { UpdateHeader(); return; }

            if (!App.Settings.Current.AlertsEnabled)
            {
                MessageBox.Show("Los avisos están en pausa, así que no hay nada que preguntar: " +
                    "el modo «Bloquear a todos» corta sin preguntar a nadie.\n\n" +
                    "Elige «Preguntar para conectar» en el botón del modo y después pulsa este botón.",
                    "Monitor de Red PCJ", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            int n = App.Traffic.VolverAPreguntarAbiertos();
            App.Events.Add(EventKind.Info,
                n == 0
                    ? "No hay programas abiertos sin decidir: todos tienen su permiso o su corte."
                    : $"Vuelven a la cola de avisos {n} {(n == 1 ? "programa" : "programas")} abiertos sin decisión.",
                "");
            UpdateHeader();
        }

        private void OnElevate(object sender, RoutedEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = Environment.ProcessPath ?? "",
                    UseShellExecute = true,
                    Verb = "runas",
                });
            }
            catch
            {
                // El usuario canceló la ventana de aprobación de Windows: no pasa nada.
            }
        }

        // ---------- Buscador fluido ----------
        // Escribir una letra en el buscador obliga a recomponer la lista entera: hay unos
        // trescientos programas en marcha y cada uno de ellos es una fila con su plantilla.
        // Hacerlo dentro del propio TextChanged es lo que hacía que el cursor fuera por
        // detrás de lo que se teclea. Mientras se escribe solo se mueve el cursor; la lista
        // se recomponen una vez y cuando se deja de teclear.
        private System.Windows.Threading.DispatcherTimer? _searchTimer;
        private bool _typing;

        private void OnSearch(object sender, TextChangedEventArgs e)
        {
            _filter = Search.Text.Trim().ToLowerInvariant();
            SearchHint.Visibility = Search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            BtnClear.Visibility = Search.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

            _typing = true;
            if (_searchTimer == null)
            {
                _searchTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(220)
                };
                _searchTimer.Tick += (s, ev) =>
                {
                    _searchTimer!.Stop();
                    _typing = false;
                    _structureDirty = true;
                    Refresh(forced: true);
                };
            }
            // Cada letra reinicia la cuenta: si no, repasaría a media palabra.
            _searchTimer.Stop();
            _searchTimer.Start();
        }

        /// Recomponer ya, sin esperar al aplazamiento del buscador: es lo que usan el grupo,
        /// las cabeceras de orden y los botones de permiso, que son un toque suelto y se
        /// quieren ver reflejados al instante.
        private void RefreshNow()
        {
            _searchTimer?.Stop();
            _typing = false;
            _structureDirty = true;
            Refresh(forced: true);
        }

        /// Escriben en la caja de buscar desde fuera (la caja de herramientas, al decir
        /// «en protección» en una fila). El TextChanged hace el resto.
        public void Buscar(string texto)
        {
            Search.Text = texto ?? "";
        }

        private void OnGroupAll(object sender, RoutedEventArgs e) => SetGroup(0);
        private void OnGroupMine(object sender, RoutedEventArgs e) => SetGroup(1);
        private void OnGroupSystem(object sender, RoutedEventArgs e) => SetGroup(2);

        private void OnClearSearch(object sender, RoutedEventArgs e)
        {
            Search.Clear();
            Search.Focus();
        }

        private void SetGroup(int group)
        {
            if (_group == group) return;
            _group = group;
            RefreshNow();
        }

        // La pastilla del selector de grupo se desliza de un tramo a otro en vez de cambiarse de
        // color cada botón: es lo que hace que cambiar de grupo se vea. Se coloca midiendo el
        // botón activo dentro del contenedor, así que hay que llamarla también cuando cambia el
        // texto (los recuentos entre paréntesis mueven el ancho de cada tramo).
        //
        // MoveSeg no mide él: aplaza la medición al siguiente tick de layout (prioridad Loaded),
        // porque justo después de cambiar el texto de los botones el ancho viejo es el que está
        // puesto. Varias llamadas seguidas se juntan en una sola.
        private bool _segPending;
        private void MoveSeg(bool animar)
        {
            if (_segPending || !IsLoaded) return;
            _segPending = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _segPending = false;
                PlaceSeg(animar);
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void PlaceSeg(bool animar)
        {
            if (SegPill == null || SegHost == null) return;
            var b = _group switch { 1 => BtnGroupMine, 2 => BtnGroupSystem, _ => BtnGroupAll };
            if (b == null || b.ActualWidth < 1 || SegHost.ActualWidth < 1) return;

            double x = b.TranslatePoint(new Point(0, 0), SegHost).X;
            double w = b.ActualWidth;
            var move = (TranslateTransform)SegPill.RenderTransform;
            if (SegPill.Width > 0 && Math.Abs(SegPill.Width - w) < 0.5 && Math.Abs(move.X - x) < 0.5)
            {
                SegPill.Opacity = 1;   // ya está en su sitio: solo falta que se vea
                return;
            }

            SegPill.Width = w;
            SegPill.Opacity = 1;
            animar &= Controls.Fx.Animar;
            if (!animar) { move.X = x; return; }
            move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(x, new Duration(TimeSpan.FromMilliseconds(230)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        }

        // ---------- Orden por salida / entrada (cabeceras pulsables) ----------

        // Flechas por codigo de punto, no por literal: el archivo fuente se compila con la
        // codificacion que toque y un literal con acentos o glifos se puede corromper.
        private static readonly string ArrowBoth = char.ConvertFromUtf32(0x2195);   // ↕ sin orden
        private static readonly string ArrowUp = char.ConvertFromUtf32(0x2191);     // ↑
        private static readonly string ArrowDown = char.ConvertFromUtf32(0x2193);   // ↓

        private void OnSortOut(object sender, RoutedEventArgs e) => SetSort(Direction.Out);
        private void OnSortIn(object sender, RoutedEventArgs e) => SetSort(Direction.In);

        private void SetSort(Direction dir)
        {
            if (dir == Direction.Out)
            {
                _sortOut = (_sortOut + 1) % 3;
                if (_sortOut != 0) _sortIn = 0;
            }
            else
            {
                _sortIn = (_sortIn + 1) % 3;
                if (_sortIn != 0) _sortOut = 0;
            }
            UpdateSortHeaders();
            RefreshNow();
        }

        // 0 arriba del todo; 1 debajo. "Sin decidir" cuenta como NO permitida, que es lo que
        // significa segun el modo: con el corte activo se la corta, y sin corte aun no tiene regla.
        private static int SortRank(InventoryEntry e, Direction dir, HashSet<string> snapshot)
        {
            bool allowed = App.Firewall.GetDecision(e.ExePath, dir, snapshot) == Decision.Permitido;
            return allowed ? 0 : 1;
        }

        // OrderBy de LINQ es estable, así que dentro del grupo de permitidas y dentro del de
        // bloqueadas manda el orden normal de la lista (en ejecución, tráfico, nombre).
        private List<InventoryEntry> ApplySort(List<InventoryEntry> entries, HashSet<string> snapshot)
        {
            if (_sortOut == 0 && _sortIn == 0) return entries;
            Direction dir = _sortIn != 0 ? Direction.In : Direction.Out;
            bool invert = (_sortIn != 0 ? _sortIn : _sortOut) == 2;
            return entries.OrderBy(e => invert ? 1 - SortRank(e, dir, snapshot) : SortRank(e, dir, snapshot)).ToList();
        }

        private void UpdateSortHeaders()
        {
            SetSortHeader(BtnHdrOut, MarkOut, "SALIDA", _sortOut);
            SetSortHeader(BtnHdrIn, MarkIn, "ENTRADA", _sortIn);
        }

        private void SetSortHeader(Button b, TextBlock mark, string label, int mode)
        {
            mark.Text = mode switch { 1 => ArrowUp, 2 => ArrowDown, _ => ArrowBoth };
            var accent = (Brush)FindResource(mode == 0 ? "B.TextDim" : "B.Teal");
            mark.Foreground = accent;
            b.Foreground = accent;
            b.ToolTip = label + " — orden de la lista\n" +
                (mode == 0 ? "Orden normal (sin juntar por permiso)."
                           : mode == 1 ? "Ahora: las PERMITIDAS agrupadas arriba."
                                       : "Ahora: las BLOQUEADAS agrupadas arriba.") +
                "\n\nPulsa para cambiar: orden normal → permitidas arriba → bloqueadas arriba.";
        }


        // ---------- Explicación de entrada y salida ----------

        private bool _legendOpen;

        private void OnToggleLegend(object sender, RoutedEventArgs e)
        {
            _legendOpen = !_legendOpen;
            ApplyLegend();
            App.Settings.Current.ShowAccessLegend = _legendOpen;
            App.Settings.Save();
        }

        private void ApplyLegend()
        {
            LegendBody.Visibility = _legendOpen ? Visibility.Visible : Visibility.Collapsed;
            LegendChevron.Text = _legendOpen ? Row.Up : Row.Down;
            BtnLegend.ToolTip = _legendOpen
                ? "Ocultar la explicación"
                : "Ver qué es la salida y qué es la entrada, y por qué una app puede tener una abierta y otra cerrada";
        }

        // ---------- Perfil de firewall (panel flotante) ----------
        // El botón «Perfiles» de la franja abre un Popup donde se puede, de un clic:
        //   · aplicar un perfil guardado (cambia la política de salida -> pide administrador),
        //   · guardar el estado de salida de ahora como perfil nuevo (no toca ninguna regla),
        //   · borrar un perfil, y volver al modo normal.
        // Es el mismo motor que la herramienta «Perfiles y candado» (App.Tools.Perfiles).

        private void OnPerfilAbrir(object sender, RoutedEventArgs e)
        {
            PerfilPopup.IsOpen = true;
            _ = RefrescarPerfilesAsync();
        }

        private void OnPerfilCerrar(object sender, RoutedEventArgs e) => PerfilPopup.IsOpen = false;

        private void OnPerfilPopupOpened(object sender, EventArgs e)
        {
            // Alineamos el panel a la derecha del botón para que no se salga de la ventana.
            try
            {
                if (PerfilPopup.Child is FrameworkElement fe)
                {
                    double w = fe.ActualWidth > 0 ? fe.ActualWidth : 404;
                    PerfilPopup.HorizontalOffset = -(w - BtnPerfilCrear.ActualWidth);
                }
            }
            catch { }
            TxtNuevoPerfil.Focus();
        }

        private void OnPerfilPopupClosed(object sender, EventArgs e) { }

        // Lee en segundo plano (PermitidasActuales consulta el firewall) y pinta la lista.
        private async Task RefrescarPerfilesAsync()
        {
            try
            {
                var (perfiles, actuales, strict) = await Task.Run(() =>
                {
                    var lista = App.Tools.Perfiles.Perfiles();
                    var act = App.Tools.Perfiles.PermitidasActuales();
                    bool s;
                    try { s = App.Settings.Current.StrictMode || FirewallService.IsOutboundDefaultBlocked(); }
                    catch { s = false; }
                    return (lista, act, s);
                });
                ConstruirFilasPerfil(perfiles, actuales, strict);
            }
            catch
            {
                PerfilVacio.Visibility = Visibility.Visible;
                PerfilEstado.Text = "No se pudo leer el estado del firewall ahora mismo.";
            }
        }

        private void ConstruirFilasPerfil(List<MonitorRedPCJ.Services.Tools.Perfil> perfiles,
                                          List<string> actuales, bool strict)
        {
            var usoActual = new HashSet<string>(actuales, StringComparer.OrdinalIgnoreCase);
            PerfilLista.Children.Clear();

            if (strict)
            {
                string? enUso = null;
                foreach (var p in perfiles)
                {
                    var set = new HashSet<string>(p.Permitidas, StringComparer.OrdinalIgnoreCase);
                    if (set.SetEquals(usoActual)) { enUso = p.Nombre; break; }
                }
                PerfilEstado.Text = enUso != null
                    ? "Bloqueo total activo · en uso el perfil «" + enUso + "»"
                    : "Bloqueo total activo · la salida de ahora no coincide con ningún perfil";
            }
            else
            {
                PerfilEstado.Text = "Modo normal: cada app sale según sus reglas. Aplicar un perfil " +
                                    "pondrá el bloqueo total con esa lista exacta.";
            }

            if (perfiles.Count == 0)
            {
                PerfilVacio.Visibility = Visibility.Visible;
                return;
            }
            PerfilVacio.Visibility = Visibility.Collapsed;

            var text = (Brush)FindResource("B.Text");
            var dim = (Brush)FindResource("B.TextDim");
            var raised = (Brush)FindResource("B.Raised");
            var line = (Brush)FindResource("B.Line");
            var accent = (Brush)FindResource("B.Accent");
            var ghost = (Style)FindResource("GhostButton");
            var plain = (Style)FindResource("PlainButton");
            var radius = (CornerRadius)FindResource("R.Button");

            foreach (var p in perfiles)
            {
                string nombre = p.Nombre;
                int cuantas = p.Permitidas.Count;
                var set = new HashSet<string>(p.Permitidas, StringComparer.OrdinalIgnoreCase);
                bool enUso = strict && set.SetEquals(usoActual);

                var row = new Border
                {
                    Background = raised,
                    BorderBrush = line,
                    BorderThickness = new Thickness(1),
                    CornerRadius = radius,
                    Padding = new Thickness(10, 8, 8, 8),
                    Margin = new Thickness(0, 0, 0, 6)
                };
                var g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                info.Children.Add(new TextBlock
                {
                    Text = nombre,
                    FontSize = 13,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = text,
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
                string fecha = p.CreadoUtc == default ? "" : " · " + p.CreadoUtc.ToLocalTime().ToString("dd/MM/yyyy");
                info.Children.Add(new TextBlock
                {
                    Text = cuantas + " apps con salida" + fecha,
                    FontSize = 10.5,
                    Margin = new Thickness(0, 1, 0, 0),
                    Foreground = dim
                });
                Grid.SetColumn(info, 0);
                g.Children.Add(info);

                if (enUso)
                {
                    var chip = new Border
                    {
                        Background = (Brush)FindResource("B.ChipOkBg"),
                        CornerRadius = new CornerRadius(9),
                        Padding = new Thickness(8, 3, 8, 3),
                        Margin = new Thickness(6, 0, 6, 0),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    chip.Child = new TextBlock
                    {
                        Text = "en uso",
                        FontSize = 10.5,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = (Brush)FindResource("B.Green")
                    };
                    Grid.SetColumn(chip, 1);
                    g.Children.Add(chip);
                }

                var aplicar = new Button
                {
                    Content = enUso ? "Aplicado" : "Aplicar",
                    Style = ghost,
                    FontSize = 11.5,
                    Padding = new Thickness(11, 5, 11, 5),
                    Margin = new Thickness(0, 0, 4, 0),
                    IsEnabled = !enUso,
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = "Deja el bloqueo total con exactamente esta lista de permitidas"
                };
                aplicar.Click += (s, e) => _ = AplicarPerfilAsync(nombre);
                Grid.SetColumn(aplicar, 2);
                g.Children.Add(aplicar);

                var borrar = new Button
                {
                    Content = new TextBlock
                    {
                        Text = "\uE74D",
                        FontFamily = (FontFamily)FindResource("F.Icon"),
                        FontSize = 12,
                        Foreground = dim
                    },
                    Style = plain,
                    Width = 26,
                    Height = 26,
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = "Borrar este perfil"
                };
                borrar.Click += (s, e) => BorrarPerfil(nombre);
                Grid.SetColumn(borrar, 3);
                g.Children.Add(borrar);

                row.Child = g;
                PerfilLista.Children.Add(row);
            }
        }

        private async Task AplicarPerfilAsync(string nombre)
        {
            if (_busy) return;
            var ok0 = MessageBox.Show(
                "Aplicar «" + nombre + "» deja el bloqueo total activo con exactamente las apps de " +
                "ese perfil: el resto se queda sin salida.\n\nWindows pedirá permisos de administrador. " +
                "¿Aplicar este perfil?",
                "Perfil de firewall", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (ok0 != MessageBoxResult.Yes) return;

            _busy = true;
            SetMandosEnabled(false);
            bool ok = false; string error = "";
            await Task.Run(() => ok = App.Tools.Perfiles.Aplicar(nombre, out error));
            SetMandosEnabled(true);
            _busy = false;

            if (!ok)
                MessageBox.Show("No se pudo aplicar el perfil:\n\n" + error, "Perfil de firewall",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            UpdateHeader();
            _ = RefrescarPerfilesAsync();
        }

        private void BorrarPerfil(string nombre)
        {
            var r = MessageBox.Show("¿Borrar el perfil «" + nombre + "»?\n\n" +
                                    "Solo se quita de la lista guardada; no cambia el firewall.",
                                    "Perfil de firewall", MessageBoxButton.YesNo, MessageBoxImage.Question,
                                    MessageBoxResult.No);
            if (r != MessageBoxResult.Yes) return;
            App.Tools.Perfiles.Borrar(nombre);
            _ = RefrescarPerfilesAsync();
        }

        private async void OnPerfilGuardar(object sender, RoutedEventArgs e)
        {
            string nombre = (TxtNuevoPerfil.Text ?? "").Trim();
            if (nombre.Length == 0)
            {
                MessageBox.Show("Ponle un nombre al perfil.", "Perfil de firewall",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                TxtNuevoPerfil.Focus();
                return;
            }
            BtnNuevoGuardar.IsEnabled = false;
            bool ok = false; string error = ""; int cuantas = 0;
            await Task.Run(() =>
            {
                ok = App.Tools.Perfiles.Guardar(nombre, out error);
                if (ok) cuantas = App.Tools.Perfiles.PermitidasActuales().Count;
            });
            BtnNuevoGuardar.IsEnabled = true;

            if (!ok)
            {
                MessageBox.Show("No se pudo guardar el perfil:\n\n" + error, "Perfil de firewall",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            TxtNuevoPerfil.Text = "";
            _ = RefrescarPerfilesAsync();
        }

        private async void OnPerfilModoNormal(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            if (!App.Settings.Current.StrictMode && !App.Tools.Perfiles.SalidaYaCortada)
            {
                MessageBox.Show("Ya estás en modo normal: no hay bloqueo total que quitar.",
                    "Perfil de firewall", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var r = MessageBox.Show(
                "Esto quita el bloqueo total: Windows volverá a permitir la salida y cada app quedará " +
                "según sus reglas.\n\nWindows pedirá permisos de administrador. ¿Volver al modo normal?",
                "Perfil de firewall", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (r != MessageBoxResult.Yes) return;

            _busy = true;
            SetMandosEnabled(false);
            bool ok = false; string error = "";
            await Task.Run(() => ok = App.Tools.Perfiles.VolverAlModoNormal(out error));
            SetMandosEnabled(true);
            _busy = false;

            if (!ok)
                MessageBox.Show("No se pudo volver al modo normal:\n\n" + error, "Perfil de firewall",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            UpdateHeader();
            _ = RefrescarPerfilesAsync();
        }

        // ---------- Repaso ----------

        private void OnUpdate() => Dispatcher.BeginInvoke(() => Refresh(forced: false));

        private static readonly TimeSpan Throttle = TimeSpan.FromMilliseconds(1500);

        private void Refresh(bool forced)
        {
            if (_menuOpen) return;   // no desmontar la fila con su menú desplegado
            // El modo «en vivo» pide el repaso de procesos en cada sondeo (antes va uno cada
            // tres). Se apaga en cuanto la pestaña deja de estar a la vista: no tiene sentido
            // gastar repasos en una lista que nadie está mirando.
            App.Traffic.RepasarCadaVuelta = IsVisible && EnVivo;
            if (!IsVisible) { _deferred = true; return; }   // pestaña cerrada: no se trabaja
            // Un toque en un botón manda sobre el aplazamiento pendiente del buscador: si no,
            // se recomponen la lista dos veces seguidas por el mismo cambio.
            if (forced) { _searchTimer?.Stop(); _typing = false; }
            // Con el dedo en el teclado no se toca la lista: si no, el repaso del tráfico
            // volvería a montar las filas por debajo del cursor a mitad de palabra.
            if (!forced && _typing) return;
            DateTime now = DateTime.Now;
            // En vivo el aplazamiento de un segundo y medio se quita: lo pedido es justamente
            // que la lista vaya al ritmo del sondeo (una vuelta por segundo).
            if (!forced && !EnVivo && (now - _lastBuild) < Throttle) return;
            _lastBuild = now;

            if (!_skinned)
            {
                try
                {
                    _skin = new Skin
                    {
                        Allow = (Style)FindResource("PillAllow"),
                        Block = (Style)FindResource("PillBlock"),
                        Pending = (Style)FindResource("PillPending"),
                        Green = (Brush)FindResource("B.Green"),
                        Coral = (Brush)FindResource("B.Coral"),
                        Amber = (Brush)FindResource("B.Amber"),
                        Teal = (Brush)FindResource("B.Teal"),
                        Dim = (Brush)FindResource("B.TextDim"),
                        Idle = IdleBrush(),
                    };
                    _skinned = true;
                }
                catch { return; }   // el tema aún no está listo: se intentará en el próximo repaso
            }

            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            UpdateHeader();
            long ta = System.Diagnostics.Stopwatch.GetTimestamp();

            bool strict = App.Settings.Current.StrictMode;

            // Una sola lectura de las reglas del firewall para toda la lista: preguntar fila por
            // fila volvía a mirar la caché cien veces por repaso.
            var snapshot = App.Firewall.RuleSnapshot();
            long t1 = System.Diagnostics.Stopwatch.GetTimestamp();

            var all = AppInventory.Collect(_filter);
            long t2 = System.Diagnostics.Stopwatch.GetTimestamp();

            // La ventana de tiempo (1.6.2): fuera de ella solo se enseña lo que sigue abierto.
            // El permiso o el rechazo de las que cerraron NO se borra, queda guardado y se
            // aplica igual si vuelven; simplemente dejan de ocupar sitio en la lista.
            int mins = MinutosReales();
            DateTime corte = mins == 0
                ? DateTime.MinValue
                : DateTime.UtcNow.AddMinutes(-mins);
            int total = all.Count;
            all = all.Where(e => Viva(e, corte)).ToList();
            _nFueraDeVentana = total - all.Count;

            int mineAll = all.Count(e => !e.IsSystem);
            int sysAll = all.Count - mineAll;

            var entries = _group switch
            {
                1 => all.Where(e => !e.IsSystem).ToList(),
                2 => all.Where(e => e.IsSystem).ToList(),
                _ => all
            };

            _nRunning = _nAllowed = _nCut = _nUndecided = _nOpenIn = 0;
            CuentaEstados(entries, snapshot);

            // El chip de estado se aplica después de contar: los cuatro números de arriba son
            // el resumen del grupo, y si cambiaran al filtrar dejarían de servir de referencia.
            if (_filtroEstado != 0)
                entries = entries.Where(e => PasaFiltro(e, _filtroEstado, snapshot)).ToList();

            // El filtro de cabecera va despues del grupo y del buscador: reordena lo que ya se
            // esta ensenando, no anade aplicaciones nuevas a la lista.
            entries = ApplySort(entries, snapshot);
            long t3 = System.Diagnostics.Stopwatch.GetTimestamp();

            SetGroupButton(BtnGroupAll, "Todas", all.Count, _group == 0);
            SetGroupButton(BtnGroupMine, "Mis apps", mineAll, _group == 1);
            SetGroupButton(BtnGroupSystem, "Del sistema", sysAll, _group == 2);

            // Cambió el texto de los tramos, así que quizá cambió su ancho: la pastilla va detrás.
            MoveSeg(true);

            // La barra de PESO es relativa a la más pesada de lo que se está viendo, así que
            // hay que saber cuál es: el aviso de cada fila lo nombra para que el porcentaje
            // se pueda leer sin tener que adivinar con qué se compara.
            double peak = 0;
            string peakName = "";
            foreach (var e in entries)
                if (e.Bps > peak) { peak = e.Bps; peakName = e.Name; }
            if (peak < 1) { peak = 1; peakName = ""; }

            // ¿Siguen los mismos programas? Entonces solo se actualizan los valores, sin tocar
            // el orden ni reconstruir la lista.
            bool sameMembers = !_structureDirty && entries.Count == _rows.Count &&
                               entries.TrueForAll(e => _index.ContainsKey(e.ExePath));

            if (sameMembers)
            {
                // Valores nuevos en las filas de siempre, sin desmontar la lista ni cambiar su
                // orden. Antes se dejaba el Entry viejo en cada fila, y como ese Entry guarda
                // el Bps copiado al inventariar, la barra de PESO se quedaba congelada en lo
                // que movía cada aplicación cuando la lista se montó por última vez.
                foreach (var e in entries)
                {
                    if (!_index.TryGetValue(e.ExePath, out var row)) continue;
                    row.Entry = e;
                    ApplyRow(row, strict, snapshot, peak, peakName);
                }
            }
            else
            {
                var next = new List<Row>(entries.Count);
                var index = new Dictionary<string, Row>(StringComparer.OrdinalIgnoreCase);
                foreach (var e in entries)
                {
                    if (!_index.TryGetValue(e.ExePath, out var row))
                        row = new Row { ExePath = e.ExePath };
                    row.Entry = e;
                    ApplyRow(row, strict, snapshot, peak, peakName);
                    next.Add(row);
                    index[e.ExePath] = row;
                }
                _rows = next;
                _index = index;
                AppsList.ItemsSource = _rows;
                _structureDirty = false;
            }

            WriteCount(entries.Count, strict);
            EnsureIcons();
            long t4 = System.Diagnostics.Stopwatch.GetTimestamp();
            MsBanda = Ms(t0, ta); MsReglas = Ms(ta, t1); MsInventario = Ms(t1, t2);
            MsOrden = Ms(t2, t3); MsFilas = Ms(t3, t4);
        }

        // Despiece del último repaso, en milisegundos. Lo imprime --search-bench para saber
        // dónde se va el tiempo cuando la pestaña se nota pesada; costar seis lecturas de
        // reloj por repaso no se nota en comparación.
        //   MsBanda      · la banda de arriba y la política de salida
        //   MsReglas     · las reglas del firewall (van cacheadas)
        //   MsInventario · AppInventory.Collect: histórico + lo que está en marcha
        //   MsOrden      · grupo y orden de cabecera
        //   MsFilas      · botones de grupo, valores de las filas y pie
        internal double MsBanda, MsReglas, MsInventario, MsOrden, MsFilas;
        private static double Ms(long desde, long hasta)
            => (hasta - desde) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

        // Pincel congelado: un pincel sin congelar avisa a cada elemento que la usa. Se saca de
        // la paleta (B.BarIdle) y se tira al cambiar de tema, como lo demás del repaso.
        private static SolidColorBrush? _idle;
        private static Brush IdleBrush()
        {
            if (_idle == null)
            {
                var b = Application.Current?.Resources["B.BarIdle"] as SolidColorBrush;
                _idle = new SolidColorBrush(b?.Color ?? Color.FromRgb(0xC6, 0xD5, 0xE2));
                _idle.Freeze();
            }
            return _idle;
        }

        // El rail de la fila no necesita pinceles propios: usa los mismos del Skin (verde = tiene
        // permiso, coral = cortada, gris = sin decidir), que ya se rehacen al cambiar de tema.

        // Fondos de la etiqueta de análisis, en el mismo orden que RiskLevel. Salen de las fichas
        // de la paleta (Chip*Bg) para que la misma app se reconozca igual aquí que en la ventana
        // de «Elegir apps». Congelados y compartidos: se usan en las 300 filas a la vez.
        private static SolidColorBrush[]? _riskTints;
        private static SolidColorBrush[] RiskTints()
        {
            if (_riskTints == null)
            {
                var t = new SolidColorBrush[4];
                t[(int)RiskLevel.EsencialRed] = Tint("B.ChipBadBg");
                t[(int)RiskLevel.SistemaWindows] = Tint("B.ChipWarnBg");
                t[(int)RiskLevel.Aplicacion] = Tint("B.ChipOkBg");
                t[(int)RiskLevel.Desconocido] = Tint("B.ChipNeutralBg");
                _riskTints = t;
            }
            return _riskTints;
        }
        private static SolidColorBrush Tint(string clave)
        {
            var b = Application.Current?.Resources[clave] as SolidColorBrush;
            var s = new SolidColorBrush(b?.Color ?? Color.FromRgb(0xEE, 0xF3, 0xF8));
            s.Freeze();
            return s;
        }

        // Texto corto de la etiqueta: el analisis completo cabe en el aviso del ratón, no en la fila.
        private static string RiskTag(RiskInfo info)
        {
            if (!string.IsNullOrEmpty(info.LabelOverride)) return "seguridad";
            return info.Level switch
            {
                RiskLevel.EsencialRed => "esencial de red",
                RiskLevel.SistemaWindows => "de Windows",
                RiskLevel.Aplicacion => "app normal",
                _ => "sin clasificar"
            };
        }

        private Brush RiskForeground(RiskLevel level) => level switch
        {
            RiskLevel.EsencialRed => _skin.Coral,
            RiskLevel.SistemaWindows => _skin.Amber,
            RiskLevel.Aplicacion => _skin.Green,
            _ => _skin.Dim
        };

        // Pinta una fila a partir de su inventario. Los contadores del pie se suman aquí.
        // Lo que dice la configuración adicional en la fila: la línea ámbar del detalle, la
        // pastilla de Salida nombrando el modo en vez de «Permitido» (que se queda corto) y el
        // color del rail. Está separado para que la fotografía de diseño pueda simularlo sin
        // escribir en apps.json.
        private void PintaAjuste(Row r, TrackedApp? cfg)
        {
            var ajuste = AppAjustes.PorId(cfg?.ModoAdicional);
            r.AjusteLine = AppAjustes.DetalleFila(cfg);
            if (ajuste.Id == AppAjustes.SinConfigurar) return;

            r.OutText = ajuste.Corto;
            r.OutTip = "Configuración adicional: " + ajuste.Nombre + "\n\n" + ajuste.Detalle;
            if (ajuste.Id == "bloquearTodo")
            {
                r.OutPill = _skin.Block;
                r.Rail = _skin.Coral;
                r.OutGlyph = Cross;   // «corte total» con la palomita parecía un permiso
            }
            else if (ajuste.ConcedeSalida)
            {
                r.OutPill = _skin.Allow;
                r.Rail = _skin.Green;
                r.OutGlyph = Check;
            }
        }

        private void ApplyRow(Row r, bool strict, HashSet<string> snapshot, double peak, string peakName)
        {
            var e = r.Entry;
            if (e == null) return;

            // Se pregunta al firewall de verdad, no a una marca: en el modo normal lo que
            // el usuario no tocó está "sin decidir", y en el estricto simplemente no sale.
            var outD = App.Firewall.GetDecision(e.ExePath, Direction.Out, snapshot);
            var inD = App.Firewall.GetDecision(e.ExePath, Direction.In, snapshot);
            bool expanded = _expanded.Contains(e.ExePath);
            var icon = AppIcon.Cached(e.ExePath);

            r.App = e.App;
            r.Name = e.Name;
            r.Folder = e.Folder;
            r.Running = e.Running;
            r.Icon = icon;
            r.HasIcon = icon != null;
            r.Rx = Format.Bps(e.App?.ReceivedBps ?? 0);
            r.Tx = Format.Bps(e.App?.SentBps ?? 0);
            r.TotalRx = Format.Bytes(e.TotalRx);
            r.TotalTx = Format.Bytes(e.TotalTx);

            r.OutText = OutLabel(outD);
            r.InText = InLabel(inD);
            r.OutGlyph = Glyph(outD);
            r.InGlyph = Glyph(inD);
            r.OutPill = Pill(outD, _skin.Allow, _skin.Block, _skin.Pending);
            // La entrada cerrada es el estado normal de Windows: se muestra igual que un
            // bloqueo nuestro, pero el aviso lo aclara.
            r.InPill = inD == Decision.Permitido ? _skin.Allow : _skin.Block;
            r.OutTip = OutTip(outD, strict);
            r.InTip = InTip(inD == Decision.Permitido);

            // 1.6.0: la configuración adicional se pinta al final, cuando ya está puesta la
            // pastilla y el rail de la decisión de siempre. Si se pintara antes, el rail de
            // abajo la volvería a su color y el modo quedaría dicho a medias.
            r.Dot = e.Running ? _skin.Green : _skin.Dim;
            r.BarBrush = e.Bps > 0 ? _skin.Teal : _skin.Idle;
            r.Frac = e.Bps > 0 ? Math.Min(1.0, e.Bps / peak) : 0;
            r.Rail = outD == Decision.Permitido ? _skin.Green
                       : outD == Decision.Bloqueado ? _skin.Coral : _skin.Idle;
            PintaAjuste(r, e.App);

            // La barra de PESO no tiene una escala fija, así que sin decirlo en el aviso es
            // imposible saber qué significa su longitud: cuánto mueve y con qué se compara.
            if (e.Bps <= 0)
            {
                r.BarTip = "Sin tráfico en este momento.\n" +
                           "Acumulado de esta aplicación: ↓ " + Format.Bytes(e.TotalRx) +
                           " · ↑ " + Format.Bytes(e.TotalTx) +
                           "\n\nLa barra se llena según lo que se mueve ahora mismo, no según lo acumulado.";
            }
            else
            {
                int porCiento = (int)Math.Round(Math.Min(1.0, e.Bps / peak) * 100);
                r.BarTip = Format.Bps(e.Bps) + " · " + porCiento + " % del máximo de la lista" +
                           (porCiento >= 100 || string.IsNullOrEmpty(peakName)
                               ? " (es la que más se mueve ahora mismo).\n"
                               : " (" + peakName + ", " + Format.Bps(peak) + ").\n") +
                           "La barra es relativa: si cambia la más pesada, todas cambian de largo\n" +
                           "aunque esta aplicación siga moviendo lo mismo.";
            }

            r.Expanded = expanded;
            r.DetailVisibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            r.SystemTag = e.IsSystem ? Visibility.Visible : Visibility.Collapsed;

            // Etiqueta de analisis: sale en cuanto el repaso de segundo plano la tiene calculada
            // (lea el archivo exe, así que no se hace en el hilo de la interfaz).
            RiskInfo? ri = PeekRisk(e.ExePath);
            if (ri == null)
            {
                r.RiskVisibility = Visibility.Collapsed;
            }
            else
            {
                r.RiskVisibility = Visibility.Visible;
                r.RiskText = RiskTag(ri);
                r.RiskFg = RiskForeground(ri.Level);
                r.RiskBg = RiskTints()[(int)ri.Level];
                r.RiskTip = ri.Label + "\n\n" + ri.What +
                    (string.IsNullOrEmpty(ri.Effect) ? "" : "\n\n" + ri.Effect) +
                    (string.IsNullOrEmpty(ri.Advice) ? "" : "\n\n" + ri.Advice);
            }

            if (expanded) FillDetail(r);
        }

        private void WriteCount(int shown, bool strict)
        {
            // Los cuatro recuentos van en sus fichas de la franja de arriba: cifra y qué significa.
            // Antes era una frase corrida, y para saber cuántas están cortadas había que leerla.
            StatRun.Text = _nRunning.ToString();
            StatAllowed.Text = _nAllowed.ToString();
            StatCut.Text = _nCut.ToString();
            StatWait.Text = _nUndecided.ToString();

            if (shown == 0)
            {
                TxtCount.Text = _filter.Length > 0
                    ? "Sin aplicaciones que coincidan"
                    : _filtroEstado != 0
                        ? "Ninguna en este filtro"
                        : "Sin aplicaciones en este grupo";
                TxtCount.ToolTip = _filtroEstado != 0
                    ? "Vuelve a pulsar la cifra de arriba para quitar el filtro."
                    : null;
                return;
            }

            // El título de la lista se queda corto (cabe junto al selector y el buscador); el
            // detalle de las dos direcciones, en el aviso al pasar el ratón.
            TxtCount.Text = shown + (shown == 1 ? " aplicación" : " aplicaciones") +
                (_group == 1 ? " tuyas" : _group == 2 ? " de Windows" : "") +
                (_filtroEstado == 0 ? "" : " · " + NombreFiltro(_filtroEstado));
            TxtCount.ToolTip =
                $"SALIDA: {_nAllowed} permitidas · {_nCut} bloqueadas · {_nUndecided} sin decidir" +
                (strict ? " (el bloqueo total corta lo que no esté marcado)" : " (sin regla tuya Windows las deja salir)") +
                $"\nENTRADA: {_nOpenIn} permitidas · {shown - _nOpenIn} bloqueadas" +
                (_nFueraDeVentana > 0
                    ? $"\n\nY {_nFueraDeVentana} que cerraron no están en la lista (más de {DuracionVentana()}); su permiso sigue guardado."
                    : "") +
                (_filtroEstado == 0
                    ? "\n\nPulsa una cifra de arriba para quedarte solo con esas. La flecha de la izquierda abre la ruta, el acumulado y el análisis."
                    : "\n\nPulsa otra vez la cifra marcada para volver a verlas todas.");
        }

        // Los iconos y las etiquetas de análisis se leen del disco: en segundo plano y una sola
        // vez por ruta. El análisis rellena _riskCache, que es lo que pinta la etiqueta de la
        // fila en el repaso siguiente, así que aquí no hace falta refrescar la lista.
        private void EnsureIcons()
        {
            if (_iconsLoading || _iconBatches > 30) return;
            var missing = _rows.Where(r => !AppIcon.Known(r.ExePath))
                               .Select(r => r.ExePath).Distinct().ToList();
            List<string> missingRisk;
            lock (_riskGate)
                missingRisk = _rows.Where(r => !_riskCache.ContainsKey(r.ExePath))
                                   .Select(r => r.ExePath).Distinct().ToList();
            if (missing.Count == 0 && missingRisk.Count == 0) return;

            _iconsLoading = true;
            _iconBatches++;
            Task.Run(() =>
            {
                foreach (var exe in missing)
                {
                    try { AppIcon.Get(exe); } catch { }
                }
                foreach (var exe in missingRisk)
                {
                    RiskInfo info;
                    try { info = AppRisk.Analyze(exe); }
                    catch { info = new RiskInfo { Level = RiskLevel.Desconocido }; }
                    lock (_riskGate) _riskCache[exe] = info;
                }
                _iconsLoading = false;
                Dispatcher.BeginInvoke(PaintIcons);
            });
        }

        // Poner los iconos ya extraídos sin reconstruir la lista: solo se tocan las filas
        // que todavía no tienen su imagen.
        private void PaintIcons()
        {
            int pintados = 0;
            foreach (var r in _rows)
            {
                if (r.HasIcon) continue;
                var ic = AppIcon.Cached(r.ExePath);
                if (ic == null) continue;
                r.Icon = ic;
                r.HasIcon = true;
                pintados++;
            }
            if (pintados > 0) return;
            // Ninguno nuevo: puede que falten rutas por leer, se reintenta en otro repaso.
            EnsureIcons();
        }

        // Contenido de la fila desplegada: tráfico acumulado, hosts, ruta completa y análisis.
        private void FillDetail(Row r)
        {
            var e = r.Entry;
            if (e == null) return;

            var line = new StringBuilder();
            line.Append(e.Running ? "En ejecución" : "Cerrada ahora");
            line.Append($" · acumulado ↓{Format.Bytes(e.TotalRx)} ↑{Format.Bytes(e.TotalTx)}");
            if (e.App != null && e.App.Hosts.Count > 0)
            {
                var hosts = App.Traffic.HostsOf(e.App).Take(5).ToList();
                line.Append($" · {e.App.Hosts.Count} destino(s) ({string.Join(", ", hosts)})");
            }
            if (e.App != null && e.App.TotalReceived + e.App.TotalSent == 0)
                line.Append(" · sin tráfico medido todavía");
            r.DetailTraffic = line.ToString();
            r.DetailPath = "Ruta: " + e.ExePath;

            try
            {
                var risk = RiskFor(e.ExePath);
                r.DetailRisk = "Análisis: " + risk.Label + " — " + risk.What +
                    (string.IsNullOrEmpty(risk.Effect) ? "" : " · " + risk.Effect);
                r.RiskBrush = RiskForeground(risk.Level);
            }
            catch
            {
                r.DetailRisk = "El análisis de esta aplicación no se pudo calcular.";
            }
        }

        private void OnExpand(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not Row row) return;
            ToggleRow(row);
        }

        // Abrir o cerrar una fila: se saca aparte porque el mando de la cabecera hace lo mismo
        // sobre todas las visibles de golpe.
        private void ToggleRow(Row row, bool? forzar = null)
        {
            bool open = !_expanded.Contains(row.ExePath);
            if (forzar.HasValue)
            {
                if (forzar.Value == _expanded.Contains(row.ExePath)) return;
                open = forzar.Value;
            }
            if (open) _expanded.Add(row.ExePath); else _expanded.Remove(row.ExePath);
            row.Expanded = open;
            row.DetailVisibility = open ? Visibility.Visible : Visibility.Collapsed;
            if (open) FillDetail(row);
        }

        // La flecha de la cabecera abre o cierra toda la lista de un toque, y decide por cómo
        // están la mayoría: si había alguna cerrada, abre todas; si estaban todas abiertas,
        // las cierra. Con cien filas, desplegarlas una a una no es un mando, es un castigo.
        private bool _allOpen;
        private void OnExpandAll(object sender, RoutedEventArgs e)
        {
            bool abrir = !_allOpen;
            foreach (var r in _rows)
            {
                if (r.Entry == null) continue;
                ToggleRow(r, abrir);
            }
            _allOpen = abrir;
            // La flecha de la cabecera gira con las de las filas: es el mismo gesto en dos
            // sitios y si una se mueve y la otra no, parece que el mando no ha respondido.
            Controls.Fx.SetTurned(BtnExpandAll, abrir);
        }

        // ---------- Píldoras desplegables ----------

        private void OnOutPill(object sender, RoutedEventArgs e) => OpenPillMenu((Button)sender, Direction.Out);
        private void OnInPill(object sender, RoutedEventArgs e) => OpenPillMenu((Button)sender, Direction.In);

        private void OpenPillMenu(Button pill, Direction dir)
        {
            // La fila va en Tag (y de respaldo en Content): sin eso el menú no se abriría.
            if ((pill.Tag as Row ?? pill.Content as Row) is not Row row) return;
            bool strict = App.Settings.Current.StrictMode && dir == Direction.Out;
            var current = App.Firewall.GetDecision(row.ExePath, dir, App.Firewall.RuleSnapshot());

            string estado = dir == Direction.Out
                ? current switch
                {
                    Decision.Permitido => "Ahora: Permitido — sale a internet",
                    Decision.Bloqueado => strict
                        ? "Ahora: Bloqueado — el bloqueo total le corta la salida"
                        : "Ahora: Bloqueado — tú le cortaste la salida",
                    _ => "Ahora: Sin decidir — no tiene regla tuya y Windows la deja salir"
                }
                : current == Decision.Permitido
                    ? "Ahora: Permitido — recibe conexiones de la red"
                    : "Ahora: Bloqueado — no recibe conexiones (así viene Windows por defecto)";

            var menu = new ContextMenu
            {
                PlacementTarget = pill,
                Placement = PlacementMode.Bottom,
                MinWidth = Math.Max(pill.ActualWidth, 258),
            };
            menu.Items.Add(new MenuItem { Header = estado, IsEnabled = false });
            menu.Items.Add(new Separator());

            var grant = new MenuItem
            {
                Header = dir == Direction.Out
                    ? (strict ? "Permitido: darle salida a internet" : "Permitido: dejarle salir")
                    : "Permitido: que reciba conexiones de la red",
                IsEnabled = current != Decision.Permitido,
            };
            grant.Click += (s, e2) => _ = ApplyAsync(row, dir, Decision.Permitido);

            var cut = new MenuItem
            {
                Header = dir == Direction.Out
                    ? (strict ? "Bloqueado: quitarle la salida" : "Bloqueado: cortarle la salida")
                    : "Bloqueado: no dejarle recibir conexiones",
                IsEnabled = dir == Direction.In
                    ? current == Decision.Permitido
                    : current != Decision.Bloqueado,
            };
            cut.Click += (s, e2) => _ = ApplyAsync(row, dir, Decision.Bloqueado);

            menu.Items.Add(grant);
            menu.Items.Add(cut);

            _menuOpen = true;
            menu.Closed += (s, e2) => { _menuOpen = false; };
            menu.IsOpen = true;
        }

        // ---------- Acciones de la fila desplegada ----------

        private void OnOpenFolder(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not Row row) return;
            try
            {
                string exe = row.ExePath;
                if (System.IO.File.Exists(exe))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = "/select,\"" + exe + "\"",
                        UseShellExecute = true
                    });
                }
                else if (System.IO.Directory.Exists(row.Folder))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    { FileName = row.Folder, UseShellExecute = true });
                }
            }
            catch { /* abrir el explorador no es crítico */ }
        }

        private void OnCopyPath(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not Row row) return;
            try { Clipboard.SetText(row.ExePath); } catch { }
        }

        // Que el aviso flotante vuelva a salir la próxima vez que abra el programa.
        private void OnAskAgain(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not Row row) return;
            App.Traffic.AskAgain(row.ExePath);
            App.Events.Add(EventKind.Info, $"{row.Name}: volverá a preguntar cuando se abra", row.ExePath);
            _structureDirty = true;
            Refresh(forced: true);
        }

        // Los cinco modos de salida y los dos refuerzos de esta aplicación (1.6.0). La ventana es
        // modal a propósito: mientras se decide, la lista no se refresca encima del cambio.
        private void OnAjusteApp(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not Row row) return;
            string exe = row.ExePath;
            if (string.IsNullOrEmpty(exe)) return;

            var w = new AjusteAppWindow(exe, row.Name, row.Icon) { Owner = Window.GetWindow(this) };
            w.ShowDialog();
            if (!w.Aplicado) return;

            _structureDirty = true;
            Refresh(forced: true);
        }

        private async Task ApplyAsync(Row row, Direction dir, Decision target)        {
            if (_busyApp) return;
            string exe = row.ExePath;
            bool strict = App.Settings.Current.StrictMode && dir == Direction.Out;

            // "Permitido" puede abrir la ventana de UAC, que tarda lo que el usuario
            // tarde en responder: se hace fuera del hilo de interfaz para no congelar la ventana.
            _busyApp = true;
            bool ok = await Task.Run(() => App.Firewall.SetDecision(exe, dir, target));
            _busyApp = false;

            string dirWord = dir == Direction.Out ? "salida" : "entrada";

            if (!ok)
            {
                string why = FirewallService.LastError;
                App.Events.Add(EventKind.Info, $"{row.Name}: no se pudo cambiar la {dirWord} — {why}", exe);
                MessageBox.Show("No se pudo cambiar la " + dirWord + " de " + row.Name + ".\n\n" + why +
                        "\n\nReintenta aceptando la ventana de aprobación de administrador.",
                    "Monitor de Red PCJ", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            bool open = target == Decision.Permitido;
            if (row.App != null)
            {
                if (dir == Direction.Out)
                {
                    row.App.OutAction = open ? RuleAction.Allow : RuleAction.Block;
                    row.App.IsKnown = true;
                }
                else row.App.InAction = open ? RuleAction.Allow : RuleAction.Block;
            }

            App.Events.Add(EventKind.RuleChanged,
                open ? $"{row.Name}: {dirWord} PERMITIDA"
                     : $"Monitor de Red PCJ bloqueó la {dirWord} de {row.Name}", exe, true);

            // El firewall ya refresca su caché de reglas al confirmar el cambio.
            _structureDirty = true;
            Refresh(forced: true);
        }

        private static void SetGroupButton(Button b, string label, int count, bool active)
        {
            b.Content = $"{label} ({count})";
            b.Tag = active ? "on" : "off";
        }

        // Si la lista lleva barra de desplazamiento, el contenido se estrecha y la cabecera
        // tiene que estrecharse igual; si no, las columnas se descuadran un dedo.
        // Se aprovecha el propio evento de desplazamiento: forzar un UpdateLayout aquí era
        // parte de la lentitud.
        private double _bandRight = -1;

        private void OnListScrollChanged(object? sender, ScrollChangedEventArgs e)
        {
            // ScrollChangedEventArgs no trae ScrollableHeight: se saca del extent menos la ventana.
            double right = e.ExtentHeight - e.ViewportHeight > 0.5
                ? SystemParameters.VerticalScrollBarWidth : 0;
            if (Math.Abs(_bandRight - right) > 0.5)
            {
                _bandRight = right;
                HeaderBand.Margin = new Thickness(0, 0, right, 0);
            }
        }

        // ---------- Etiquetas y avisos de las pastillas ----------

        private static string OutLabel(Decision d) => d switch
        {
            Decision.Permitido => "Permitido",
            Decision.Bloqueado => "Bloqueado",
            _ => "Sin decidir"
        };

        // En la entrada no hay término medio: o hay regla que la abre, o Windows la corta.
        private static string InLabel(Decision d) => d == Decision.Permitido ? "Permitido" : "Bloqueado";

        private string Glyph(Decision d) => d switch
        {
            Decision.Permitido => Check,
            Decision.Bloqueado => Cross,
            _ => Dash
        };

        private static readonly string Check = "\u2713";   // ✓
        private static readonly string Cross = "\u2715";   // ✕
        private static readonly string Dash = "\u2013";    // –

        private static Style Pill(Decision d, Style allow, Style block, Style pending) => d switch
        {
            Decision.Permitido => allow,
            Decision.Bloqueado => block,
            _ => pending
        };

        private static string OutTip(Decision d, bool strict)
        {
            string baseTip = strict
                ? (d == Decision.Permitido
                    ? "Permitido: tiene una regla de permiso, por eso sale a internet."
                    : d == Decision.Bloqueado
                        ? "Bloqueado: el bloqueo total le corta la salida hasta que la marques."
                        : "Sin decidir: no tiene regla tuya y el bloqueo total la corta.")
                : d switch
                {
                    Decision.Permitido => "Permitido: tú le diste salida y puede conectar.",
                    Decision.Bloqueado => "Bloqueado: tú le cortaste la salida.",
                    _ => "Sin decidir: no tiene ninguna regla tuya; Windows la deja salir."
                };
            return "SALIDA (lo que el programa manda a internet)\n" + baseTip + "\nPulsa para elegir.";
        }

        // Windows corta la entrada por defecto en los tres perfiles: cerrada no es un bloqueo
        // nuestro, es lo normal. Solo lo que el usuario abre aparece como abierto.
        private static string InTip(bool open) =>
            "ENTRADA (lo que otros equipos piden a este PC)\n" + (open
                ? "Permitido: puede recibir conexiones desde la red."
                : "Bloqueado: no recibe conexiones. Es lo que hace Windows por defecto; " +
                  "abrirlo solo sirve para un programa que deba compartir algo en la red (carpetas, impresora, servidor).")
            + "\nPulsa para elegir.";

        // ---------- Comprobación de diseño (--protect-preview) ----------

        // Ajuste previo a pintar: deja el orden de cabecera como se quiere revisar.
        // El 5 no ordena nada: abre el detalle de todas las filas (se hace al final, ver
        // PreviewAbrirDetalle), que es la única forma de fotografiar ese panel.
        private void PreviewSort(int which)
        {
            _sortOut = which == 1 || which == 2 ? which : 0;
            _sortIn = which == 3 || which == 4 ? which - 2 : 0;
            UpdateSortHeaders();
            _structureDirty = true;
            Refresh(forced: true);
        }

        // Lo mismo para la ventana de minutos y el chip de estado, desde los modos de
        // comprobación que se lanzan por línea de mandatos. No guardan en Ajustes: son fotos.
        private void PreviewVentana(int minutos)
        {
            _ventanaMin = System.Array.IndexOf(Ventanas, minutos) < 0 ? 15 : minutos;
            BarraVentana.Value = VentanaIndex();
            PintaVentana();
            _structureDirty = true;
            Refresh(forced: true);
        }

        private void PreviewFiltro(int estado)
        {
            _filtroEstado = estado;
            // El cambio de IsChecked dispararía OnFiltroEstado, que volvería a elegir el filtro
            // y a guardar el ajuste: se corta con la misma bandera que usan los clics.
            _cambiandoFiltro = true;
            FiltroRun.IsChecked = estado == 1;
            FiltroOk.IsChecked = estado == 2;
            FiltroCut.IsChecked = estado == 3;
            FiltroWait.IsChecked = estado == 4;
            _cambiandoFiltro = false;
            _structureDirty = true;
            Refresh(forced: true);
        }

        // Resumen del último repaso, para el parte de --protect-window-check.
        private string EstadoResumen()
            => $"filas {_rows.Count,4} · en ejecución {_nRunning,3} · permitidas {_nAllowed,3}" +
               $" · bloqueadas {_nCut,3} · sin decidir {_nUndecided,3} · fuera de la ventana {_nFueraDeVentana,3}";

        // Abrir el detalle de todas las filas para fotografiarlo. Se hace aparte, y ya con la
        // lista llena: al preparar el orden el inventario aún está llegando de segundo plano,
        // así que abrir entonces no abría ninguna fila.
        private void PreviewAbrirDetalle()
        {
            if (!_allOpen) OnExpandAll(this, new RoutedEventArgs());
        }

        // Dos configuraciones adicionales simuladas para la foto: se cambia la fila, que es solo
        // de mostrar, y nunca el TrackedApp de verdad, así que apps.json no se toca. Sin esto el
        // PNG de diseño no puede enseñar la línea ámbar ni la pastilla del modo, porque en una
        // máquina recién instalada ninguna aplicación lo tiene puesto todavía.
        private void PreviewSimulaAjuste()
        {
            var dos = _rows.Where(r => r.Entry != null).Take(2).ToList();
            if (dos.Count == 0) return;
            PintaAjuste(dos[0], new TrackedApp
            {
                ExePath = dos[0].Entry!.ExePath,
                ModoAdicional = "soloPuertos",
                PuertosAdicionales = "443, 53/udp",
                SoloRedLocal = true,
            });
            if (dos.Count > 1) PintaAjuste(dos[1], new TrackedApp
            {
                ExePath = dos[1].Entry!.ExePath,
                ModoAdicional = "bloquearTodo",
                HeredarAHijos = true,
            });
        }

        // Monta la pestaña en una ventana sin borde fuera del alcance del ratón, la deja repasar
        // lo que tarde el fondo en traer iconos y análisis, y la guarda como PNG. No añade ni
        // quita reglas: es solo lectura del estado actual.
        public static void RenderPreview(string path, int waitMs, int sort, int chip, int minutos,
                                         int ancho = 1180)
        {
            // Las animaciones de Fx se apagan para fotografiar: un PNG sacado a mitad de un
            // gesto sale con las barras a medias y parece un fallo de maquetación.
            Controls.Fx.Animar = false;
            var view = new ProtectView();
            if (sort != 0) view.PreviewSort(sort);
            // El sentinela de «no se pidió ventana» no puede ser -1: ese número es ahora el
            // extremo «en vivo» de la barra, un valor legítimo que hay que poder fotografiar.
            if (minutos != int.MinValue) view.PreviewVentana(minutos);
            if (chip != 0) view.PreviewFiltro(chip);

            var win = new Window
            {
                Content = view,
                // El ancho se puede pedir: la franja de la cabecera tiene que aguantar estrecha
                // sin que el chip de la ventana se plante encima del selector de grupo, y la
                // ventana real baja hasta los 900 px de MinWidth.
                Width = Math.Max(760, ancho),
                Height = 820,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                ShowActivated = false,
                Left = -9000,
                Top = 120,
                Background = Brushes.White,
            };
            win.Show();

            // La lista solo se pinta con la pestaña a la vista, y el análisis sale de segundo
            // plano: hay que dejar que el bucle de mensajes trabaje un rato antes de capturar.
            var frame = new System.Windows.Threading.DispatcherFrame();
            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(Math.Max(1500, waitMs))
            };
            timer.Tick += (s, e) => { timer.Stop(); frame.Continue = false; };
            timer.Start();
            System.Windows.Threading.Dispatcher.PushFrame(frame);

            // Fotografía con el detalle abierto: hay que dejar que el árbol visual se reocupe
            // de las filas nuevas antes de capturar, si no saldrían cerradas.
            if (sort == 5)
            {
                view.PreviewAbrirDetalle();
                view.PreviewSimulaAjuste();
                for (int i = 0; i < 4; i++)
                    System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                        () => { }, System.Windows.Threading.DispatcherPriority.Loaded);
            }

            int w = Math.Max(1, (int)Math.Ceiling(win.ActualWidth));
            int h = Math.Max(1, (int)Math.Ceiling(win.ActualHeight));
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(win);

            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            using (var fs = System.IO.File.Create(path)) enc.Save(fs);

            // Geometría de la franja de arriba: un PNG deja VER que el chip de la ventana de
            // minutos se planta sobre el selector de grupo, pero no cuánto se sale. Estas cuatro
            // líneas dicen el hueco exacto, y con el ancho de la ventana de foto se comprueba
            // cualquier ancho entre los 900 px mínimos y la pantalla entera.
            try
            {
                var geo = new StringBuilder();
                geo.AppendLine("FRANJA DE LA CABECERA — " + DateTime.Now);
                geo.AppendLine($"ancho de la ventana de foto: {win.ActualWidth:0}");
                void Mide(string nombre, FrameworkElement el)
                {
                    if (el == null) { geo.AppendLine("  " + nombre + ": (no existe)"); return; }
                    var q = el.TransformToVisual(view).Transform(new Point(0, 0));
                    geo.AppendLine($"  {nombre}: x {q.X:0} a {q.X + el.ActualWidth:0}" +
                                   $" (ancho {el.ActualWidth:0})");
                }
                Mide("título", view.TxtCount);
                Mide("Refrescar", view.BtnRefrescar);
                Mide("barra de minutos", view.BarraVentana);
                Mide("chip de la ventana", view.ChipVentana);
                Mide("selector de grupo", view.SegTile);
                Mide("buscador", view.SearchHost);
                double hueco = view.SegTile.TransformToVisual(view).Transform(new Point(0, 0)).X
                             - (view.ChipVentana.TransformToVisual(view).Transform(new Point(0, 0)).X
                                + view.ChipVentana.ActualWidth);
                geo.AppendLine($"  → hueco entre el chip y el selector: {hueco:0} px · lastre aplicado: nivel {view._lastre}");
                geo.AppendLine("  (vale con tal de que el hueco sea positivo: 0 es tocarse)");

                // La banda de mando de arriba: aquí el que se planta encima del otro es el chip
                // «en directo» sobre el botón «Elegir apps» (lo vio el autor en su pantalla). Se mide
                // lo mismo: el borde derecho del chip contra el borde izquierdo del primer mando,
                // y cuánto pediría cada mitad de la banda.
                geo.AppendLine();
                geo.AppendLine("BANDA DE MANDO — identidad contra los cinco mandos");
                Mide("identidad", view.BandaIzq);
                Mide("chip «en directo»", view.ChipDirecto);
                Mide("Elegir apps", view.BtnAccess);
                Mide("modo", view.BtnMode);
                Mide("mandos (los cinco juntos)", view.BandaDer);
                double xMando = view.BtnAccess.TransformToVisual(view).Transform(new Point(0, 0)).X;
                // El último trozo visible de la identidad: con el chip escondido es el título, y
                // medir siempre el chip daría un hueco negativo mentira (una pieza colapsada se
                // queda con la x del último repaso y ancho 0).
                bool chipPuesto = view.ChipDirecto.Visibility == Visibility.Visible;
                FrameworkElement ultimo = chipPuesto ? view.ChipDirecto : view.TxtBandaTitulo;
                double xUltimo = ultimo.TransformToVisual(view).Transform(new Point(0, 0)).X +
                                 ultimo.ActualWidth;
                double mandosBanda = Peso(view.BandaDer);
                double dejaBanda = view.BandaGrid.ActualWidth - mandosBanda;
                double pideBanda = view._wTituloLargo < 20 ? 0
                    : AnchoEscudo + HuecoEscudo +
                      (view._lastreBanda >= 3 ? view._wTituloCorto : view._wTituloLargo) +
                      (chipPuesto ? view._wChip : 0);
                geo.AppendLine($"  → hueco entre la identidad y «Elegir apps»: {xMando - xUltimo:0} px" +
                               $" · lastre de banda: nivel {view._lastreBanda}" +
                               (chipPuesto ? "" : " (chip escondido)"));
                geo.AppendLine($"  → la identidad pide {pideBanda:0} px y los mandos le dejan {dejaBanda:0} px" +
                               $" (la banda mide {view.BandaGrid.ActualWidth:0})");
                geo.AppendLine("  (vale con tal de que el hueco sea positivo y lo que pide la identidad" +
                               " quepa en lo que le dejan: 0 es tocarse)");
                System.IO.File.WriteAllText(path + ".cabecera.txt", geo.ToString(), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                try
                {
                    System.IO.File.WriteAllText(path + ".cabecera.txt",
                        "no se pudo medir la franja: " + ex.Message);
                }
                catch { }
            }

            // Volcado de los avisos flotantes: un PNG no puede enseñar un tooltip, y sin esto
            // no hay forma de saber si el enlazado de la barra de PESO saca texto o una cadena
            // vacía. Se recorre el árbol visual de verdad, así que lo que se escribe es lo que
            // vería el usuario al pasar el ratón.
            try
            {
                var barras = new List<FrameworkElement>();
                BuscarConNombre(win, "Peso en la lista", barras);
                var parte = new StringBuilder();
                parte.AppendLine("AVISOS DE LA COLUMNA PESO — " + DateTime.Now);
                parte.AppendLine("barras encontradas en el árbol visual: " + barras.Count);
                for (int i = 0; i < barras.Count; i++)
                {
                    var ctx = barras[i].DataContext;
                    string nombre = "";
                    try { nombre = ctx?.GetType().GetProperty("Name")?.GetValue(ctx) as string ?? ""; } catch { }
                    parte.AppendLine();
                    parte.AppendLine($"fila {i + 1} · {nombre}:");
                    parte.AppendLine("  " + (barras[i].ToolTip as string ?? "(el aviso no es texto)"));
                }
                System.IO.File.WriteAllText(path + ".peso.txt", parte.ToString(), Encoding.UTF8);
            }
            catch { }

            win.Close();
        }

        // Deja trabajar al bucle de mensajes un rato sin dormir el hilo: es lo que hace falta
        // para que el sondeo de segundo plano traiga el inventario antes de medir o de capturar.
        private static void Esperar(int ms)
        {
            var frame = new System.Windows.Threading.DispatcherFrame();
            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(Math.Max(200, ms))
            };
            timer.Tick += (s, e) => { timer.Stop(); frame.Continue = false; };
            timer.Start();
            System.Windows.Threading.Dispatcher.PushFrame(frame);
        }

        // Parte de la ventana de minutos y de los cuatro filtros: recorre las ocho posiciones de
        // la barra y los cuatro chips, y escribe cuántas filas deja cada uno. Solo lee procesos,
        // reglas y ajustes; no cambia el modo ni escribe una sola regla.
        //   MonitorRedPCJ.exe --protect-window-check parte.txt
        public static void WindowCheck(string archivo)
        {
            Controls.Fx.Animar = false;
            var view = new ProtectView();
            var win = new Window
            {
                Content = view,
                Width = 1180,
                Height = 820,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                ShowActivated = false,
                Left = -9000,
                Top = 120,
                Background = Brushes.White,
            };
            win.Show();
            Esperar(7000);

            var parte = new StringBuilder();
            parte.AppendLine("VENTANA DE MINUTOS Y FILTROS DE PROTECCIÓN — " + DateTime.Now);
            parte.AppendLine("inventario guardado (histórico + en marcha): " +
                             AppInventory.Collect("").Count);
            parte.AppendLine();
            parte.AppendLine("LA BARRA DE MINUTOS");
            foreach (int m in Ventanas)
            {
                view.PreviewVentana(m);
                Esperar(400);
                string rt = m == Vivo ? "en vivo" : m == 0 ? "todas" : m + " min";
                // La etiqueta del chip se comprueba a la vez: es lo que el usuario ve, y el modo
                // «en vivo» se distingue también por su color de acento.
                parte.AppendLine("  " + rt.PadRight(12) + "chip \"" + view.TxtVentana.Text +
                                 "\" · " + view.EstadoResumen());
            }
            parte.AppendLine();
            parte.AppendLine("LOS CUATRO CHIPS, con la ventana en 15 min");
            view.PreviewVentana(15);
            Esperar(400);
            parte.AppendLine("  " + "ninguno".PadRight(12) + view.EstadoResumen());
            string[] nombres = { "", "ejecución", "permitidas", "bloqueadas", "sin decidir" };
            for (int chip = 1; chip <= 4; chip++)
            {
                view.PreviewFiltro(chip);
                Esperar(400);
                parte.AppendLine("  " + nombres[chip].PadRight(12) + view.EstadoResumen());
            }
            view.PreviewFiltro(0);

            parte.AppendLine();
            parte.AppendLine("EL MENÚ DE MODOS: CUATRO OPCIONES (las tres primeras cambian de modo)");
            view.PreviewModos(parte);

            view.ChequeaBanda(win, parte);

            win.Close();
            System.IO.File.WriteAllText(archivo, parte.ToString(), Encoding.UTF8);
        }

        // La banda de mando probada a varios anchos de ventana. Lo que vio el autor —el chip
        // «en directo» plantado debajo de «Elegir apps»— no se ve en una foto hecha a un solo
        // ancho, y el lastre por pasos tiene que soltarse en el momento justo: ni antes (sobra
        // información) ni después (ya se montó). En cada paso se mide el borde derecho de lo
        // último que enseña la identidad contra el borde izquierdo del primer mando, y si el
        // título se está recortando a medias.
        void ChequeaBanda(Window win, StringBuilder parte)
        {
            parte.AppendLine();
            parte.AppendLine("LA BANDA DE MANDO, DE 900 A 1400 PX");
            foreach (int ancho in new[] { 900, 950, 1000, 1050, 1100, 1200, 1400 })
            {
                win.Width = ancho;
                Esperar(500);
                double xMando = BtnAccess.TransformToVisual(this).Transform(new Point(0, 0)).X;
                FrameworkElement ultimo = ChipDirecto.Visibility == Visibility.Visible
                    ? ChipDirecto : TxtBandaTitulo;
                double xUltimo = ultimo.TransformToVisual(this).Transform(new Point(0, 0)).X +
                                 ultimo.ActualWidth;
                double hueco = xMando - xUltimo;
                bool tituloEntero = TxtBandaTitulo.ActualWidth >= TxtBandaTitulo.DesiredSize.Width - 1;
                string suelta = _lastreBanda <= 0 ? "nada" :
                    _lastreBanda == 1 ? "la palabra «Bloqueo total»" :
                    _lastreBanda == 2 ? "además el chip «en directo»"
                                      : "además el título se queda corto";
                parte.AppendLine($"  {ancho} px · hueco {hueco:0} px · nivel {_lastreBanda}" +
                                 $" (suelta {suelta}) · título «{TxtBandaTitulo.Text}»");
                parte.AppendLine("    " + (hueco > 0
                    ? "ok    nada se monta sobre «Elegir apps»"
                    : "MAL   algo se monta sobre «Elegir apps»"));
                parte.AppendLine("    " + (tituloEntero
                    ? "ok    el título se lee entero"
                    : "MAL   el título está recortado y el lastre no lo evitó"));
            }
            win.Width = 1180;
            Esperar(400);
        }

        // Fotografía el desplegable de modos con sus cuatro opciones de verdad. El contenido de un
        // Popup vive en su propio árbol visual —fuera de la ventana, y por eso RenderTargetBitmap
        // de la ventana no lo capturaría—, así que se saca ese contenido del Popup, se le da sitio
        // con holgura para que la sombra no se corte y se pinta suelto. No pulsa ningún botón.
        //   MonitorRedPCJ.exe --modos-preview modos.png [nocturna]
        public static void ModesPreview(string path)
        {
            Controls.Fx.Animar = false;
            var view = new ProtectView();
            var win = new Window
            {
                Content = view,
                Width = 1180,
                Height = 820,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                ShowActivated = false,
                Left = -9000,
                Top = 120,
                Background = Brushes.White,
            };
            win.Show();
            Esperar(2500);

            var contenido = view.ModePopup.Child;
            view.ModePopup.Child = null;
            view.ModePopup.IsOpen = false;
            if (contenido is not FrameworkElement el)
            {
                win.Close();
                System.IO.File.WriteAllText(path + ".err", "El desplegable de modos no tiene contenido.");
                return;
            }

            // Se mide con el ancho que el menú trae puesto (346, más el margen del borde) para que
            // las descripciones partan la línea igual que en la pantalla.
            el.Measure(new Size(360, 2000));
            var sz = el.DesiredSize;
            double pad = 26;
            var host = new Grid
            {
                Width = sz.Width + pad * 2,
                Height = sz.Height + pad * 2,
                Background = Brushes.Transparent,
            };
            el.HorizontalAlignment = HorizontalAlignment.Center;
            el.VerticalAlignment = VerticalAlignment.Center;
            host.Children.Add(el);
            host.Measure(new Size(host.Width, host.Height));
            host.Arrange(new Rect(0, 0, host.Width, host.Height));
            host.UpdateLayout();

            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                Math.Max(1, (int)Math.Ceiling(host.Width)),
                Math.Max(1, (int)Math.Ceiling(host.Height)), 96, 96, PixelFormats.Pbgra32);
            rtb.Render(host);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            using (var fs = System.IO.File.Create(path)) enc.Save(fs);
            win.Close();
        }

        // Abre el desplegable de modos —aquí la ventana está fuera de pantalla, a -9000 px, así
        // que nadie lo ve— y enumera sus opciones con el título de verdad y el Tag. La cuarta
        // («Borrar todas las decisiones») no lleva Tag porque no es un modo: por eso se comprueba
        // que haya cuatro y que la última no tenga número de modo. No pulsa ningún botón.
        public void PreviewModos(StringBuilder parte)
        {
            ModePopup.IsOpen = true;
            Esperar(300);
            int items = 0, sinTag = 0;
            if (ModePopup.Child is Border borde && borde.Child is StackPanel palo)
            {
                foreach (var c in palo.Children)
                {
                    if (c is not Button b) continue;
                    items++;
                    if (b.Tag is not string) sinTag++;
                    parte.AppendLine("  " + items + " · «" + TituloItem(b) + "»  modo=" +
                                     (b.Tag is string t ? t : "(no es un modo)"));
                }
            }
            ModePopup.IsOpen = false;
            parte.AppendLine("  opciones: " + items + (items == 4 ? " ok" : " FALLO: se esperaban 4"));
            parte.AppendLine("  la última sin número de modo: " + (sinTag == 1 ? "ok" : "FALLO"));
        }

        // El título de una opción del menú: es el primer texto dentro de la columna de letra, no
        // el icono (que va suelto en su propia columna).
        private static string TituloItem(Button b)
        {
            if (b.Content is Grid g)
                foreach (var c in g.Children)
                    if (c is StackPanel sp && sp.Children.Count > 0 && sp.Children[0] is TextBlock t)
                        return t.Text;
            return b.Content is string s ? s : "";
        }

        // Reúne los elementos de la ventana cuyo nombre de accesibilidad sea el pedido. Ese
        // nombre se pone a propósito en la plantilla de fila para poder encontrar la barra
        // desde fuera sin depender de cómo esté montada por dentro.
        private static void BuscarConNombre(DependencyObject nodo, string nombre, List<FrameworkElement> fuera)
        {
            int n = VisualTreeHelper.GetChildrenCount(nodo);
            for (int i = 0; i < n; i++)
            {
                var h = VisualTreeHelper.GetChild(nodo, i);
                if (h is FrameworkElement fe &&
                    fe.GetValue(System.Windows.Automation.AutomationProperties.NameProperty) is string s &&
                    s == nombre) fuera.Add(fe);
                BuscarConNombre(h, nombre, fuera);
            }
        }

        // ---------- Medida del buscador (--search-bench) ----------

        // Deja trabajar al bucle de mensajes un rato, como lo haría con el usuario delante.
        private static void Pump(int ms)
        {
            var frame = new System.Windows.Threading.DispatcherFrame();
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
            t.Tick += (s, e) => { t.Stop(); frame.Continue = false; };
            t.Start();
            System.Windows.Threading.Dispatcher.PushFrame(frame);
        }

        // Compara las dos formas de responder al teclado, sobre la lista de verdad del equipo:
        //   ANTES: cada letra recomponía la lista entera dentro del propio TextChanged.
        //   AHORA: cada letra solo arregla el cursor, y la lista se recomponen una vez al
        //          dejar de teclear.
        // Escribe un parte de texto y no toca nada más (ni reglas, ni modo de firewall).
        public static void SearchBench(string path, string word)
        {
            word = string.IsNullOrWhiteSpace(word) ? "chrome" : word.Trim();
            var sb = new StringBuilder();
            sb.AppendLine("MEDIDA DEL BUSCADOR DE PROTECCIÓN — " + DateTime.Now);
            sb.AppendLine("palabra de prueba: «" + word + "»");

            var view = new ProtectView();
            var win = new Window
            {
                Content = view,
                Width = 1180,
                Height = 820,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                ShowActivated = false,
                Left = -9000,
                Top = 120,
                Background = Brushes.White,
            };
            win.Show();
            Pump(2500);   // que el fondo dé tiempo a traer iconos y análisis

            int filas = view._rows.Count;
            sb.AppendLine($"filas en la lista sin filtrar: {filas}");
            sb.AppendLine();

            // ---- ANTES: repaso inmediato en cada letra ----
            double totalAntes = 0;
            for (int i = 1; i <= word.Length; i++)
            {
                string pref = word.Substring(0, i).ToLowerInvariant();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                view._filter = pref;
                view._structureDirty = true;
                view.Refresh(forced: true);
                sw.Stop();
                totalAntes += sw.Elapsed.TotalMilliseconds;
                sb.AppendLine($"  antes · letra {i} «{pref}»: {sw.Elapsed.TotalMilliseconds,7:F1} ms con {view._rows.Count} filas" +
                              $"  [banda {view.MsBanda:F0} · reglas {view.MsReglas:F0} · inventario {view.MsInventario:F0} · orden {view.MsOrden:F0} · filas {view.MsFilas:F0}]");
            }
            sb.AppendLine($"  => antes se quedaba el hilo de interfaz {totalAntes:F0} ms bloqueado por escribir «{word}»");
            sb.AppendLine();

            // ---- AHORA: el manejador de verdad, letra a letra ----
            // Se bombea el bucle 40 ms entre teclas (menos de los 220 ms del aplazamiento),
            // así el repaso no llega a dispararse a media palabra, igual que escribiendo rápido.
            view.Buscar("");
            Pump(600);
            double peorAhora = 0, totalAhora = 0;
            for (int i = 1; i <= word.Length; i++)
            {
                Pump(40);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                view.Search.Text = word.Substring(0, i);
                sw.Stop();
                double ms = sw.Elapsed.TotalMilliseconds;
                peorAhora = Math.Max(peorAhora, ms);
                totalAhora += ms;
                sb.AppendLine($"  ahora · letra {i} «{word.Substring(0, i)}»: {ms,7:F1} ms");
            }
            sb.AppendLine($"  => ahora cada tecla cuesta {totalAhora / word.Length:F1} ms de media " +
                          $"(la peor {peorAhora:F1} ms) y el hilo de interfaz se libera al instante");

            // Se deja caer el aplazamiento y se mide el único repaso que produce la palabra.
            Pump(400);
            sb.AppendLine($"  tras dejar de teclear: la lista queda con {view._rows.Count} filas " +
                          $"filtradas por «{word.ToLowerInvariant()}» (un solo repaso)");
            sb.AppendLine();
            sb.AppendLine(totalAntes > 0 && totalAhora > 0
                ? $"RESULTADO: escribir «{word}» bloqueaba {totalAntes:F0} ms la interfaz y ahora bloquea {totalAhora:F0} ms durante la escritura."
                : "RESULTADO: no dio tiempo a medir.");

            try { System.IO.File.WriteAllText(path, sb.ToString(), Encoding.UTF8); } catch { }
            win.Close();
        }
    }
}
