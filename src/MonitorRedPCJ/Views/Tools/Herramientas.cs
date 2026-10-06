using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MonitorRedPCJ.Models;
using MonitorRedPCJ.Services;
using MonitorRedPCJ.Services.Tools;

namespace MonitorRedPCJ.Views.Tools
{
    /// Lo que la ventana anfitriona (la caja de herramientas o la pestaña de la ventana
    /// principal) tiene que responder cuando una herramienta quiere llevarse al usuario a
    /// otra parte. Está aquí para que los paneles no dependan de ToolsWindow.
    internal interface IAnfitrion
    {
        void IrAHerramienta(string id);
        void IrAProteccion(string buscar);
        void IrARecursos();
    }

    // ============================================================================
    //  Base de todas las pantallas
    // ============================================================================

    internal abstract class HerramientaPanel : ContentControl
    {
        protected readonly PanelVivo Panel;
        protected readonly List<Action> _descolgar = new();
        public IAnfitrion? Anfitrion { get; set; }
        public string Id { get; protected set; } = "";

        /// Lo que ve la comprobación automática (--tools-check) del esqueleto ya pintado.
        public PanelVivo Esqueleto => Panel;

        /// Descolgar sin esperar al Unloaded de la ventana: lo usa esa misma comprobación.
        public void SoltarPublico() => Soltar();

        protected HerramientaPanel(string titulo, string ayuda)
        {
            Panel = new PanelVivo(titulo, ayuda);
            Content = Panel;
            Loaded += (s, e) => { Suscribir(); try { Refrescar(); } catch { } };
            Unloaded += (s, e) => Soltar();
        }

        protected virtual void Suscribir() { }

        protected virtual void Soltar()
        {
            foreach (var d in _descolgar) { try { d(); } catch { } }
            _descolgar.Clear();
        }

        /// Se engancha a un evento del servicio y deja apuntado cómo desengancharse.
        protected void Escuchar(Action<Action> colgar, Action<Action> soltar)
        {
            var h = (Action)(() => { try { EnUI(Refrescar); } catch { } });
            colgar(h);
            _descolgar.Add(() => soltar(h));
        }

        protected void EnUI(Action a)
        {
            if (Dispatcher.CheckAccess()) a();
            else Dispatcher.BeginInvoke(a);
        }

        public abstract void Refrescar();

        /// Pie que avisa de que la herramienta está apagada: sin eso, la pantalla mostraría
        /// datos viejos como si fueran de este instante.
        protected string EstadoApagada(string id)
        {
            var def = ToolCatalog.PorId(id);
            return ToolCatalog.Activada(id)
                ? ""
                : "Apagada: no se está mirando nada nuevo" +
                  (def != null && def.Consumo.Length > 0 ? ". " + def.Consumo : "") ;
        }

        protected static void Aviso(string titulo, string texto)
        {
            try { App.Tray?.Toast(titulo, texto); } catch { }
        }
    }

    // ============================================================================
    //  1 · Radar de destinos
    // ============================================================================

    internal sealed class RadarPanel : HerramientaPanel
    {
        private bool _historico;
        private bool _soloRastreo;
        private readonly HashSet<string> _marcados = new(StringComparer.OrdinalIgnoreCase);

        public RadarPanel() : base("A quién llama cada programa",
            "Destinos con su nombre resuelto en inverso. En coral, lo que está en la lista de rastreo.")
        {
            Id = "radar";
            Panel.Cabecera(Columna.Libre("Destino"), Columna.Fija("IP", 122), Columna.Fija("Puerto", 58, true),
                           Columna.Fija("Protocolo", 86), Columna.Fija("Pinta", 120));
            Panel.BarraDebajo = true;   // seis botones: en la misma línea se comían el título
            Refrescar();
        }

        protected override void Suscribir()
        {
            Escuchar(h => App.Tools.Radar.Actualizado += h, h => App.Tools.Radar.Actualizado -= h);
        }

        private void Botonera()
        {
            Panel.LimpiarBarra();
            Panel.Pastilla("Ahora", () => { _historico = false; Refrescar(); }, !_historico);
            Panel.Pastilla("Histórico", () => { _historico = true; Refrescar(); }, _historico);
            Panel.Pastilla("Solo rastreo", () => { _soloRastreo = !_soloRastreo; Refrescar(); }, _soloRastreo);
            Panel.Boton("Resolver ahora", () => App.Tools.Radar.ResolverAhora(), "",
                        "Vuelve a consultar los nombres que faltan");
            Panel.Boton("Bloquear elegidos (hosts)", Bloquear, "",
                        "Escribe en el fichero hosts los dominios marcados con la casilla");
            Panel.Boton("Quitar bloqueos", Quitar, "",
                        "Devuelve el fichero hosts a como estaba antes de bloquear");
        }

        private void Bloquear()
        {
            if (_marcados.Count == 0)
            {
                MessageBox.Show("Marca antes con la casilla los dominios que quieras cortar.",
                    "Radar de destinos", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var marca = _marcados.ToList();
            if (MessageBox.Show(
                    "Se van a escribir " + marca.Count + " dominios en el fichero hosts de Windows " +
                    "(apuntando a 0.0.0.0). PCJ guarda una copia del hosts actual antes de tocarlo.\n\n" +
                    string.Join("\n", marca.Take(12)) + (marca.Count > 12 ? "\n…" : ""),
                    "Bloquear en hosts", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            if (RadarService.AplicarBloqueos(marca, out string error))
            {
                App.Settings.Current.BloqueoHostsActivo = true;
                App.Settings.Save();
                App.Events.Add(EventKind.Info, "Bloqueo en hosts: " + marca.Count + " dominios de rastreo",
                    string.Join(", ", marca.Take(20)), important: false);
                _marcados.Clear();
                Aviso("Radar de destinos", marca.Count + " dominios bloqueados en el fichero hosts.");
            }
            else
                MessageBox.Show("No se pudo escribir el fichero hosts: " + error,
                    "Radar de destinos", MessageBoxButton.OK, MessageBoxImage.Warning);
            Refrescar();
        }

        private void Quitar()
        {
            if (!RadarService.QuitarBloqueos(out string error))
            {
                MessageBox.Show("No se pudo devolver el hosts: " + error, "Radar de destinos",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            App.Settings.Current.BloqueoHostsActivo = false;
            App.Settings.Save();
            App.Events.Add(EventKind.Info, "Quittado el bloqueo de hosts", "El fichero hosts vuelve a su estado anterior.",
                important: false);
            Refrescar();
        }

        public override void Refrescar()
        {
            Botonera();
            var radar = App.Tools.Radar;
            var lista = _historico ? radar.DestinosHistoricos() : radar.DestinosActuales();
            var vistos = lista.AsEnumerable();
            if (_soloRastreo) vistos = vistos.Where(d => d.Rastreador);

            var filas = new List<Fila>();
            foreach (var grupo in vistos.GroupBy(d => string.IsNullOrEmpty(d.App) ? "Sin nombre" : d.App)
                                        .OrderByDescending(g => g.Count()))
            {
                filas.Add(new Fila(grupo.Key, grupo.Count() + (grupo.Count() == 1 ? " destino" : " destinos"))
                { EsGrupo = true });

                foreach (var d in grupo.OrderByDescending(x => x.Rastreador).ThenBy(x => x.Host).ThenBy(x => x.Ip).Take(40))
                {
                    string destino = string.IsNullOrEmpty(d.Host) ? "sin nombre (" + d.Ip + ")" : d.Host;
                    var f = new Fila(destino, d.Ip, d.Puerto.ToString(), d.EsUdp ? "UDP" : "TCP",
                                     d.EnRedLocal ? "En tu red" : d.Rastreador ? "Rastreo" : "Normal")
                    {
                        Tinte = d.Rastreador ? Kit.Coral : (d.EnRedLocal ? Kit.Dim : null),
                        Tip = d.Exe,
                        Chip = d.Rastreador ? "rastreo" : null,
                        ChipFondo = d.Rastreador ? Kit.P(0xFFFCEBEB) : Kit.FondoSuave,
                        ChipTinta = d.Rastreador ? Kit.Coral : Kit.Dim,
                    };
                    if (d.Rastreador && !string.IsNullOrEmpty(d.Host))
                    {
                        string host = d.Host;
                        var ch = Kit.Casilla("", _marcados.Contains(host), on =>
                        {
                            if (on) _marcados.Add(host); else _marcados.Remove(host);
                        });
                        ch.ToolTip = "Marcar " + host + " para bloquearlo en hosts";
                        f.Casilla = ch;
                    }
                    if (!string.IsNullOrEmpty(d.Exe))
                    {
                        string app = d.App;
                        f.Botones.Add(("En protección", () => Anfitrion?.IrAProteccion(app),
                                       "Abrir Protección con esta aplicación buscada"));
                    }
                    filas.Add(f);
                }
            }

            Panel.Filas(filas, 200);

            var bloqueados = RadarService.Bloqueados();
            string pie = (_historico ? "Histórico desde que instalaste PCJ" : "Conectados ahora") +
                         ": " + lista.Count + " destinos · " + radar.NombresResueltos +
                         " nombres resueltos en caché · " + bloqueados.Count +
                         " bloqueados en hosts" + (bloqueados.Count > 0 ? " (" + string.Join(", ", bloqueados.Take(4)) + "…)" : "");
            string apagada = EstadoApagada("radar");
            Panel.Pie(apagada.Length > 0 ? apagada + "  ·  " + pie : pie);
        }
    }

    // ============================================================================
    //  2 · Identidad de las apps
    // ============================================================================

    internal sealed class FirmasPanel : HerramientaPanel
    {
        public FirmasPanel() : base("Firma digital y ruta de cada programa",
            "Verifica la firma con Windows y avisa cuando el nombre no cuadra con el editor o la ruta es una temporal.")
        {
            Id = "firmas";
            Panel.Cabecera(Columna.Libre("Aplicación"), Columna.Fija("Editor", 160),
                           Columna.Fija("Firmante", 180), Columna.Fija("Estado", 170),
                           Columna.Fija("Peso", 74, true));
            Panel.Boton("Repasar ahora", () => App.Tools.Firmas.RepasarAhora(), "",
                        "Vuelve a comprobar todos los ejecutivos conocidos");
            Refrescar();
        }

        protected override void Suscribir()
        {
            Escuchar(h => App.Tools.Firmas.Actualizado += h, h => App.Tools.Firmas.Actualizado -= h);
        }

        public override void Refrescar()
        {
            var f = App.Tools.Firmas;
            var filas = f.Resultados()
                .OrderByDescending(x => x.Sospechosa)
                .ThenBy(x => x.Estado == "valida" ? 0 : x.Estado == "sin_firmar" ? 1 : 2)
                .ThenBy(x => x.App)
                .Select(x =>
                {
                    Brush tinte = x.Estado switch
                    {
                        "valida" => x.Sospechosa ? Kit.Ambar : Kit.Verde,
                        "sin_firmar" => Kit.Ambar,
                        _ => Kit.Coral,
                    };
                    return new Fila(x.App, x.Editor, x.Firmante, SignatureService.EstadoLegible(x.Estado),
                                    Format.Bytes(x.Bytes))
                    {
                        Sub = x.Exe,
                        Tinte = tinte,
                        Tip = x.Detalle.Length > 0 ? x.Detalle + "\n" + x.Exe : x.Exe,
                        Chip = x.Sospechosa ? "revisa" : null,
                        ChipFondo = x.Sospechosa ? Kit.P(0xFFFCEBEB) : Kit.FondoSuave,
                        ChipTinta = x.Sospechosa ? Kit.Coral : Kit.Dim,
                    };
                })
                .ToList();

            Panel.Filas(filas, 200);

            int sospechosas = filas.Count(x => x.Chip != null);
            string estado = f.Trabajando ? "repasando ahora…" : "en reposo";
            string pie = f.TotalAnalizadas + " aplicaciones analizadas · " + f.Pendientes +
                         " pendientes · " + sospechosas + " para revisar · " + estado;
            string apagada = EstadoApagada("firmas");
            Panel.Pie(apagada.Length > 0 ? apagada : pie);
        }
    }

    // ============================================================================
    //  3 · Cuota de datos
    // ============================================================================

    internal sealed class CuotaPanel : HerramientaPanel
    {
        private bool _hoy;
        private readonly BarraProgreso _barra = new() { Margin = new Thickness(0, 14, 0, 4) };

        public CuotaPanel() : base("Lo que gastas día, semana y mes",
            "Cuenta lo que entra y sale partida por aplicación. El límite te lo pones tú; con 0 GB solo cuenta.")
        {
            Id = "cuota";
            Panel.Cabecera(Columna.Libre("Aplicación"), Columna.Fija("En el periodo", 130, true));
            Panel.Boton("Empezar de cero", Empezar, "",
                        "Borra las cuentas guardadas; no toca el histórico de tráfico");
            Refrescar();
        }

        protected override void Suscribir()
        {
            Escuchar(h => App.Tools.Cuota.Actualizado += h, h => App.Tools.Cuota.Actualizado -= h);
        }

        private void Empezar()
        {
            if (MessageBox.Show("Esto borra las cuentas de cuota guardadas. El histórico de tráfico no se toca." +
                                "\n\n¿Empezar de cero?", "Cuota de datos", MessageBoxButton.YesNo,
                                MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            App.Tools.Cuota.EmpezarDeCero();
            Refrescar();
        }

        private void GuardarAjustes(double gb, int aviso, int dia)
        {
            var s = App.Settings.Current;
            s.CuotaMensualGB = Math.Max(0, gb);
            s.AvisoCuotaPorCiento = Math.Min(100, Math.Max(10, aviso));
            App.Settings.Save();
            App.Tools.Cuota.DiaDeReinicio = dia;
            Refrescar();
        }

        public override void Refrescar()
        {
            var q = App.Tools.Cuota;
            var s = App.Settings.Current;

            // Formulario de la cuota: tres campos y ya.
            Panel.Extra.Children.Clear();
            var form = new StackPanel { Orientation = Orientation.Horizontal };
            var campoGb = Kit.Campo("Cuota mensual (GB)", s.CuotaMensualGB.ToString("0.#"), 84);
            var campoAviso = Kit.Campo("Avisar al llegar al (%)", s.AvisoCuotaPorCiento.ToString(), 60);
            var campoDia = Kit.Campo("Día de reinicio", q.DiaDeReinicio.ToString(), 46);
            form.Children.Add(campoGb);
            form.Children.Add(campoAviso);
            form.Children.Add(campoDia);
            var guardar = Kit.Chip("Guardar", null, null, Kit.P(0xFFE4F4F4), Kit.Teal, 12);
            guardar.ToolTip = "Guarda la cuota, el aviso y el día de reinicio";
            guardar.MouseLeftButtonUp += (s2, e2) =>
            {
                double.TryParse(((TextBox)campoGb.Children[1]).Text.Replace(',', '.'), out double gb);
                int.TryParse(((TextBox)campoAviso.Children[1]).Text, out int aviso);
                int.TryParse(((TextBox)campoDia.Children[1]).Text, out int dia);
                if (aviso <= 0) aviso = s.AvisoCuotaPorCiento;
                if (dia <= 0) dia = q.DiaDeReinicio;
                GuardarAjustes(gb, aviso, dia);
            };
            form.Children.Add(guardar);
            Panel.Extra.Children.Add(form);

            var total = q.TotalDelMes;
            Panel.KpisLimpias();
            Panel.Kpi("Mes en curso", Format.Bytes(total.bytes), total.etiqueta,
                      q.PorcentajeDeCuota >= 100 ? Kit.Coral : q.PorcentajeDeCuota >= 80 ? Kit.Ambar : Kit.Texto);
            if (s.CuotaMensualGB > 0)
            {
                double resto = s.CuotaMensualGB * 1024d * 1024d * 1024d - total.bytes;
                Panel.Kpi("Restante", Format.Bytes((long)Math.Max(0, resto)),
                          "de " + s.CuotaMensualGB.ToString("0.#") + " GB",
                          resto <= 0 ? Kit.Coral : Kit.Texto);
            }
            Panel.Kpi("Hoy", Format.Bytes(q.TotalDeHoy), "desde las 00:00");
            Panel.Kpi("7 días", Format.Bytes(q.TotalDeUnaSemana), "movidos esta semana");

            _barra.Porcentaje = q.PorcentajeDeCuota;
            _barra.TextoEncima = s.CuotaMensualGB > 0
                ? q.PorcentajeDeCuota.ToString("0") + " % de la cuota de " + s.CuotaMensualGB.ToString("0.#") + " GB"
                : "Sin límite puesto: solo cuenta";
            Panel.Extra.Children.Add(_barra);

            Panel.LimpiarBarra();
            Panel.Pastilla("Este mes", () => { _hoy = false; Refrescar(); }, !_hoy);
            Panel.Pastilla("Hoy", () => { _hoy = true; Refrescar(); }, _hoy);

            var top = _hoy ? q.TopDeHoy(14) : q.TopDelMes(14);
            Panel.Filas(top.Select(t => new Fila(t.app, Format.Bytes(t.bytes))
            { Tinte = t.bytes > total.bytes / 3 ? Kit.Ambar : null }).ToList(), 30);

            string apagada = EstadoApagada("cuota");
            bool enCeros = total.bytes == 0 && q.TotalDeHoy == 0 && q.TotalDeUnaSemana == 0;
            Panel.Pie(apagada.Length > 0 ? apagada
                : enCeros
                    ? "La cuota empieza a contar desde que la enciendes: PCJ no puede saber lo que ya " +
                      "gastaste antes de hoy. A partir de ahora suma lo que mide el monitor."
                : "Periodo de facturación: " + total.etiqueta + " · aviso al " + s.AvisoCuotaPorCiento +
                  " % · el contador solo suma lo que ya mide el monitor.");
        }
    }

    // ============================================================================
    //  4 · Máquina del tiempo
    // ============================================================================

    internal sealed class TiempoPanel : HerramientaPanel
    {
        private int _ventanaMin = 5;              // minutos por barra
        private int _posicion = -1;               // -1 = ahora mismo
        private readonly GraficoBarras _grafico = new() { Margin = new Thickness(0, 10, 0, 0) };
        private readonly Slider _linea = new()
        {
            Minimum = 0,
            IsSnapToTickEnabled = true,
            TickFrequency = 1,
            Margin = new Thickness(0, 6, 0, 0),
            Height = 22,
        };
        private List<(DateTime desde, long rx, long tx, Dictionary<string, long[]> apps)> _buckets = new();

        public TiempoPanel() : base("Volver a un momento y ver qué estaba hablando",
            "Cada barra es un trocito de tiempo. La lista de debajo es lo que se movió en ese trozo, no el acumulado.")
        {
            Id = "tiempo";
            Panel.Cabecera(Columna.Libre("Aplicación"), Columna.Fija("Bajada", 110, true),
                           Columna.Fija("Subida", 110, true), Columna.Fija("Total", 110, true));
            Panel.Extra.Children.Add(_tituloBloque);
            Panel.Extra.Children.Add(_grafico);
            Panel.Extra.Children.Add(_linea);
            _linea.ValueChanged += (s, e) =>
            {
                if (_posicion == (int)e.NewValue) return;
                _posicion = (int)e.NewValue;
                PintarBloque();
            };
            Refrescar();
        }

        protected override void Suscribir()
        {
            Escuchar(h => App.Tools.Tiempo.Actualizado += h, h => App.Tools.Tiempo.Actualizado -= h);
        }

        private double HorasDeLaVentana() => _ventanaMin switch { 1 => 3, 5 => 12, _ => 48 };

        private void Botonera()
        {
            Panel.LimpiarBarra();
            foreach (int v in new[] { 1, 5, 15 })
                Panel.Pastilla(v + " min", () => { _ventanaMin = v; _posicion = -1; Refrescar(); }, _ventanaMin == v);
            Panel.Pastilla("Ahora", () => { _posicion = -1; Refrescar(); }, _posicion < 0);
        }

        public override void Refrescar()
        {
            Botonera();
            var muestras = App.Tools.Tiempo.Muestras(HorasDeLaVentana());
            _buckets = new List<(DateTime, long, long, Dictionary<string, long[]>)>();

            long ticksPorBucket = _ventanaMin * 60L * TimeSpan.TicksPerSecond;
            Dictionary<string, long[]> apps = new(StringComparer.OrdinalIgnoreCase);
            DateTime inicio = DateTime.MinValue;
            long rx = 0, tx = 0;
            bool abierto = false;

            foreach (var m in muestras)
            {
                long marca = m.HoraUtc.Ticks;
                if (!abierto || (marca - inicio.Ticks) >= ticksPorBucket)
                {
                    if (abierto) _buckets.Add((inicio, rx, tx, apps));
                    inicio = m.HoraUtc; rx = tx = 0;
                    apps = new Dictionary<string, long[]>(StringComparer.OrdinalIgnoreCase);
                    abierto = true;
                }
                rx += m.Rx; tx += m.Tx;
                foreach (var kv in m.Apps)
                {
                    if (!apps.TryGetValue(kv.Key, out var v)) v = apps[kv.Key] = new long[2];
                    v[0] += kv.Value[0]; v[1] += kv.Value[1];
                }
            }
            if (abierto) _buckets.Add((inicio, rx, tx, apps));

            // Si la ventana elegida da demasiadas barras, se ensancha para que quepan.
            if (_buckets.Count > 120)
            {
                int factor = (_buckets.Count + 119) / 120;
                var juntas = new List<(DateTime, long, long, Dictionary<string, long[]>)>();
                for (int i = 0; i < _buckets.Count; i += factor)
                {
                    var grupo = _buckets.Skip(i).Take(factor).ToList();
                    var mapa = new Dictionary<string, long[]>(StringComparer.OrdinalIgnoreCase);
                    foreach (var b in grupo)
                        foreach (var kv in b.apps)
                        {
                            if (!mapa.TryGetValue(kv.Key, out var v)) v = mapa[kv.Key] = new long[2];
                            v[0] += kv.Value[0]; v[1] += kv.Value[1];
                        }
                    juntas.Add((grupo[0].Item1, grupo.Sum(b => b.Item2), grupo.Sum(b => b.Item3), mapa));
                }
                _buckets = juntas;
            }

            _grafico.Datos = _buckets.Select(b => (b.Item2, b.Item3)).ToList();
            int marcado = _posicion < 0 ? _buckets.Count - 1 : Math.Min(_posicion, Math.Max(0, _buckets.Count - 1));
            _grafico.Marcado = marcado;
            _linea.Maximum = Math.Max(0, _buckets.Count - 1);
            _linea.Value = _posicion < 0 ? Math.Max(0, _buckets.Count - 1) : _posicion;
            _grafico.InvalidateVisual();

            PintarBloque();

            string apagada = EstadoApagada("tiempo");
            Panel.Pie(apagada.Length > 0 ? apagada
                : _buckets.Count == 0
                    ? "Todavía no hay muestras: tarda treinta segundos en aparecer la primera."
                    : _buckets.Count + " bloques de " + _ventanaMin + " min · se conservan " +
                      App.Settings.Current.HistoryRetentionDays + " días (Configuración)" +
                      (App.Tools.Tiempo.Trabajando ? "" : " · apagada, no se guarda nada nuevo"));
        }

        private void PintarBloque()
        {
            if (_buckets.Count == 0)
            {
                Panel.Filas(new List<Fila>());
                return;
            }
            int i = _posicion < 0 ? _buckets.Count - 1 : Math.Min(_posicion, _buckets.Count - 1);
            var b = _buckets[i];
            var filas = b.Item4.OrderByDescending(kv => kv.Value[0] + kv.Value[1])
                               .Take(30)
                               .Select(kv => new Fila(kv.Key, Format.Bytes(kv.Value[0]), Format.Bytes(kv.Value[1]),
                                                      Format.Bytes(kv.Value[0] + kv.Value[1])))
                               .ToList();
            Panel.Filas(filas, 30);
            _grafico.Marcado = i;
            _grafico.InvalidateVisual();
            _tituloBloque.Text = "Bloque de " + _ventanaMin + " min empezado a las " +
                                 b.Item1.ToLocalTime().ToString("dd MMM · HH:mm");
        }

        // Encima del gráfico va una línea diciendo qué bloque está seleccionado.
        private readonly TextBlock _tituloBloque = new()
        {
            FontSize = 12,
            Foreground = Kit.Dim,
            Margin = new Thickness(0, 8, 0, 0),
        };
    }

    // ============================================================================
    //  5 · Perfiles y candado
    // ============================================================================

    internal sealed class PerfilesPanel : HerramientaPanel
    {
        public PerfilesPanel() : base("Cambiar de reglas de un clic, y cortar todo",
            "Un perfil guarda la lista de aplicaciones con salida. El candado corta toda la salida sin borrar tus reglas.")
        {
            Id = "perfiles";
            Panel.Cabecera(Columna.Libre("Perfil"), Columna.Fija("Guardado", 140), Columna.Fija("Permitidas", 90, true));
            Refrescar();
        }

        protected override void Suscribir()
        {
            Escuchar(h => App.Tools.Perfiles.Actualizado += h, h => App.Tools.Perfiles.Actualizado -= h);
        }

        public override void Refrescar()
        {
            var p = App.Tools.Perfiles;

            Panel.LimpiarBarra();
            if (p.CandadoPuesto)
                Panel.Boton("Soltar el candado", () =>
                {
                    if (p.SoltarCandado(out string e)) Aviso("Candado", "La salida vuelve a la normalidad.");
                    else MessageBox.Show("No se pudo soltar el candado: " + e, "Perfiles y candado",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    Refrescar();
                }, "", "Devuelve las reglas tal y como estaban antes de cortar");
            else
                Panel.Boton("Cortar toda la salida", () =>
                {
                    if (MessageBox.Show("El candado deja a todo el equipo sin salida a internet, incluida " +
                                        "esta aplicación. Tus reglas no se borran: se guardan aparte y «Soltar " +
                                        "el candado» las devuelve.\n\n¿Cortar la salida ahora?",
                                        "Perfiles y candado", MessageBoxButton.YesNo,
                                        MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                    if (p.PonerCandado(out string e)) Aviso("Candado", "Salida cortada. Pulsa «Soltar el candado» para volver.");
                    else MessageBox.Show("No se pudo poner el candado: " + e, "Perfiles y candado",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    Refrescar();
                }, "", "Corta toda la salida y deja tus reglas a buen recaudo");

            Panel.Boton("Volver al modo normal", () =>
            {
                if (p.VolverAlModoNormal(out string e))
                    Aviso("Perfiles", "Bloqueo total quitado: cada aplicación decide según tus reglas.");
                else MessageBox.Show("No se pudo volver al modo normal: " + e, "Perfiles y candado",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                Refrescar();
            }, "", "Quita el bloqueo total sin borrar reglas");

            // Arriba del todo: el campo con el nombre del perfil nuevo.
            Panel.Extra.Children.Clear();
            var form = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            var campo = Kit.Campo("Nombre del perfil nuevo", "Casa", 150);
            form.Children.Add(campo);
            var caja = (TextBox)campo.Children[1];
            form.Children.Add(Kit.Chip("Guardar el estado actual como perfil", () =>
            {
                string nombre = caja.Text.Trim();
                if (nombre.Length == 0)
                {
                    MessageBox.Show("Ponle un nombre al perfil.", "Perfiles y candado",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                if (!p.Guardar(nombre, out string e))
                    MessageBox.Show("No se pudo guardar: " + e, "Perfiles y candado",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                else
                    Aviso("Perfiles", "Guardado «" + nombre + "» con " + p.PermitidasActuales().Count +
                                      " aplicaciones con salida.");
                Refrescar();
            }, null, Kit.P(0xFFE4F4F4), Kit.Teal, 12,
               "Toma la lista de aplicaciones que ahora tienen salida y la guarda con ese nombre"));
            Panel.Extra.Children.Add(form);

            var filas = new List<Fila>();
            foreach (var per in p.Perfiles().OrderBy(x => x.Nombre))
            {
                string nombre = per.Nombre;
                int cuantas = per.Permitidas.Count;
                filas.Add(new Fila(nombre, Kit.Hora(per.CreadoUtc.ToLocalTime()), cuantas.ToString())
                {
                    Sub = "Salida permitida para " + cuantas + " aplicaciones",
                    Botones =
                    {
                        ("Aplicar", () =>
                        {
                            if (MessageBox.Show("Esto deja el bloqueo total activo con exactamente las " +
                                                cuantas + " aplicaciones del perfil «" + nombre +
                                                "». Las demás se quedan sin salida." +
                                                "\n\n¿Aplicar?", "Perfiles y candado",
                                                MessageBoxButton.YesNo, MessageBoxImage.Question)
                                != MessageBoxResult.Yes) return;
                            if (!p.Aplicar(nombre, out string e))
                                MessageBox.Show("No se pudo aplicar: " + e, "Perfiles y candado",
                                    MessageBoxButton.OK, MessageBoxImage.Warning);
                            else Aviso("Perfiles", "Aplicado «" + nombre + "».");
                            Refrescar();
                        }, null),
                        ("Borrar", () =>
                        {
                            if (MessageBox.Show("¿Borrar el perfil «" + nombre +
                                                "»? Las reglas del firewall no se tocan.",
                                                "Perfiles y candado", MessageBoxButton.YesNo,
                                                MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                            p.Borrar(nombre);
                            Refrescar();
                        }, null),
                    },
                });
            }

            Panel.Filas(filas, 60);

            int vivas = p.PermitidasActuales().Count;
            Panel.Pie("Ahora mismo tienen salida " + vivas + " aplicaciones · " +
                      (p.CandadoPuesto ? "el candado está puesto" : "candado suelto") + " · " +
                      (FirewallService.IsAdmin ? "con permisos de administrador"
                       : "sin permisos: los cambios de firewall pedirán UAC"));
        }
    }

    // ============================================================================
    //  6 · Auditor de fugas
    // ============================================================================

    internal sealed class FugasPanel : HerramientaPanel
    {
        public FugasPanel() : base("Revisión de seguridad del equipo",
            "QUIC, DNS sobre HTTPS, resolvers que no son los tuyos, reglas sucias y permisos sin uso.")
        {
            Id = "fugas";
            Panel.Cabecera(Columna.Fija("Comprobación", 210), Columna.Libre("Qué se vio"),
                           Columna.Fija("Estado", 96));
            Panel.Boton("Comprobar ahora", () => { App.Tools.Fugas.ComprobarAhora(); Refrescar(); }, "",
                        "Lee las tablas que PCJ ya consulta; no cambia nada");
            Panel.Boton("Copiar resumen", () =>
            {
                try
                {
                    Clipboard.SetText(App.Tools.Fugas.Resumen());
                    Aviso("Auditor de fugas", "Resumen copiado al portapapeles.");
                }
                catch { }
            }, "", "Copia el informe entero del auditor");
            Refrescar();
        }

        protected override void Suscribir()
        {
            Escuchar(h => App.Tools.Fugas.Actualizado += h, h => App.Tools.Fugas.Actualizado -= h);
        }

        public override void Refrescar()
        {
            var f = App.Tools.Fugas;
            var hallazgos = f.Resultados();

            var filas = hallazgos.Select(h =>
            {
                Brush tinte = h.Nivel switch { 2 => Kit.Coral, 1 => Kit.Ambar, _ => Kit.Verde };
                var fila = new Fila(h.Nombre, h.Detalle, h.Nivel switch { 2 => "FUGA", 1 => "REVISAR", _ => "BIEN" })
                {
                    Tinte = tinte,
                    ChipFondo = h.Nivel == 2 ? Kit.P(0xFFFCEBEB) : h.Nivel == 1 ? Kit.P(0xFFFDF1DD) : Kit.P(0xFFE6F6EF),
                    ChipTinta = tinte,
                };
                fila.Chip = null;   // el estado ya sale en su columna, sin pegatina extra
                if (h.Accion.Length > 0)
                    fila.Botones.Add((h.Accion, () => Ejecutar(h.Accion), null));
                return fila;
            }).ToList();

            Panel.Extra.Children.Clear();
            Panel.Extra.Children.Add(Kit.Casilla("Revisar cada hora", f.RepasoHorario, on =>
            {
                f.RepasoHorario = on;
                App.Settings.Current.AuditorCadaHora = on;
                App.Settings.Save();
                Refrescar();
            }, "Añade una pasada automática cada 60 minutos y anota en el registro lo nuevo que aparezca"));

            Panel.Filas(filas, 40);

            int fugas = hallazgos.Count(x => x.Nivel == 2), revisa = hallazgos.Count(x => x.Nivel == 1);
            string apagada = EstadoApagada("fugas");
            Panel.Pie((apagada.Length > 0 ? apagada + "  ·  " : "") +
                      hallazgos.Count + " comprobaciones · " + fugas + " fuga(s) clara, " + revisa +
                      " para revisar · última pasada " + Kit.Hora(f.UltimaComprobacion));
        }

        private void Ejecutar(string accion)
        {
            switch (accion)
            {
                case "Ver en Protección":
                    Anfitrion?.IrAProteccion("");
                    break;
                case "Ver destinos":
                    Anfitrion?.IrAHerramienta("radar");
                    break;
                case "Ver adapters":
                    Anfitrion?.IrARecursos();
                    break;
                case "Limpiar reglas obsoletas":
                    if (MessageBox.Show("Se borrarán las reglas de PCJ cuyo ejecutable ya no existe en el disco." +
                                        "\n\n¿Limpiar?", "Auditor de fugas", MessageBoxButton.YesNo,
                                        MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                    int n = LeakAuditService.LimpiarObsoletas();
                    App.Events.Add(EventKind.Info, "Reglas de PCJ limpiadas",
                        n + " reglas obsoletas borradas.", important: false);
                    MessageBox.Show("Se han borrado " + n + " reglas obsoletas.", "Auditor de fugas",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    App.Tools.Fugas.ComprobarAhora();
                    break;
                case "Volver a aplicar":
                    Anfitrion?.IrAProteccion("");
                    break;
                case "Revisar candado":
                    Anfitrion?.IrAHerramienta("perfiles");
                    break;
            }
        }
    }

    // ============================================================================
    //  7 · Tráfico por país
    // ============================================================================

    internal sealed class PaisesPanel : HerramientaPanel
    {
        public PaisesPanel() : base("Dónde están los servidores con los que hablas",
            "Cada IP destino se asocia a un país. Los países nuevos se consultan una vez y se quedan en caché.")
        {
            Id = "paises";
            Panel.Cabecera(Columna.Fija("País", 220), Columna.Fija("Destinos", 80, true),
                           Columna.Fija("Conexiones", 96, true), Columna.Libre("Aplicaciones"));
            Panel.Boton("Resolver ahora", () =>
            {
                bool salio = App.Tools.Paises.ResolverAhora();
                Panel.Pie(salio
                    ? "Poniendo al día las direcciones que faltan: se preguntan de cien en cien y " +
                      "la lista se va rellenando sola."
                    : "No se puede salir a preguntar: enciende primero «Consultar ip-api.com» aquí " +
                      "abajo, o deja una base local geoipv4.csv en la carpeta de datos.");
            }, "", "Manda al servidor las direcciones que aún no tienen país");
            Panel.Boton("Quitar la caché", () =>
            {
                App.Tools.Paises.Limpiar();
                Refrescar();
            }, "", "Borra los países consultados; se volverán a preguntar cuando hagan falta");
            Panel.Boton("Olvidar vistos", () =>
            {
                App.Tools.Paises.OlvidarVistos();
                Refrescar();
            }, "", "Olvida los países ya avisados, para que vuelvan a saltar los avisos");
            Refrescar();
        }

        protected override void Suscribir()
        {
            Escuchar(h => App.Tools.Paises.Actualizado += h, h => App.Tools.Paises.Actualizado -= h);
        }

        public override void Refrescar()
        {
            var g = App.Tools.Paises;
            var lista = g.Paises();
            int sinPais = lista.FirstOrDefault(x => x.Codigo == "??")?.Destinos ?? 0;

            Panel.Filas(lista.Select(p => new Fila(p.Nombre + "  (" + p.Codigo + ")", p.Destinos.ToString(),
                                                   p.Conexiones.ToString(), string.Join(", ", p.Apps.Take(5)))
            {
                Sub = p.Ips.Count + " direcciones distintas",
                Tinte = p.EsLocal || p.Codigo == "??" ? Kit.Dim : null,
                Tip = string.Join(", ", p.Ips.Take(30)),
            }).ToList(), 80);

            Panel.Extra.Children.Clear();
            var casillas = new StackPanel();
            casillas.Children.Add(Kit.Casilla("Consultar ip-api.com para saber el país de cada IP",
                App.Settings.Current.PaisesConsultarOnline, on =>
            {
                App.Settings.Current.PaisesConsultarOnline = on;
                App.Settings.Save();
                if (on) g.ResolverAhora(); else g.OlvidarCola();
                Refrescar();
            }, "Saca del equipo la dirección IP destino, sin cifrar, una sola vez por dirección. " +
               "Apagado: la columna se queda en «Sin resolver» salvo que pongas tú la base local."));
            casillas.Children.Add(Kit.Casilla("Avisar si aparece un país nuevo",
                App.Settings.Current.PaisesAvisarNuevos, on =>
            {
                App.Settings.Current.PaisesAvisarNuevos = on;
                App.Settings.Save();
                g.AvisarNuevos = on;
            }, "Cada estreno queda anotado en el registro y en un aviso flotante"));
            Panel.Extra.Children.Add(casillas);

            string fuente = g.BaseLocalCargada
                ? "base local geoipv4.csv (" + g.RangosEnBase + " rangos), sin salir a internet"
                : App.Settings.Current.PaisesConsultarOnline
                    ? "consultando ip-api.com con caché"
                    : "sin consultar: «Consultar ip-api.com» está apagada";
            // Si no sale nada, que se sepa por qué: sin esto la pantalla parecía rota cuando lo
            // que pasaba es que el propio firewall de PCJ no dejaba preguntar.
            string porque = "";
            if (App.Settings.Current.PaisesConsultarOnline && g.EnCache == 0 &&
                !string.IsNullOrEmpty(GeoService.UltimoError))
                porque = "  ·  " + GeoService.UltimoError;
            string apagada = EstadoApagada("paises");
            Panel.Pie((apagada.Length > 0 ? apagada + "  ·  " : "") +
                      "Origen: " + fuente + " · " + g.EnCache + " IP en caché · " + g.EnCola +
                      " en cola" + (sinPais > 0 ? " · " + sinPais + " destinos sin país" : "") +
                      porque +
                      " · el volumen no se puede partir por país: PCJ mide bytes por aplicación");
        }
    }

    // ============================================================================
    //  8 · Guardián de la LAN
    // ============================================================================

    internal sealed class LanPanel : HerramientaPanel
    {
        public LanPanel() : base("Dispositivos nuevos, cambios y redes gemelas",
            "Recuerda lo que hay en tu red y avisa cuando entra un equipo nuevo o aparece otra BSSID con tu nombre de wifi.")
        {
            Id = "lan";
            Panel.Cabecera(Columna.Fija("Equipo", 180), Columna.Fija("IP", 120), Columna.Fija("MAC", 140),
                           Columna.Fija("Visto", 120), Columna.Libre("De dónde sale el nombre"));
            Panel.Boton("Escanear ahora", () => App.Scanner.ScanAsync(), "",
                        "Recorre la subred del adaptador activo; tarda unos segundos");
            Panel.Boton("Comprobar wifi", () => { App.Tools.Lan.ComprobarWifi(); Refrescar(); }, "",
                        "Lee las redes que ve el adaptador y busca nombres repetidos con otra BSSID");
            Panel.Boton("Olvidar todo", () =>
            {
                if (MessageBox.Show("Se borra la lista de equipos guardada. La siguiente pasada volverá a " +
                                    "avisar de todo lo que haya en la red.",
                                    "Guardián de la LAN", MessageBoxButton.YesNo,
                                    MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                App.Tools.Lan.OlvidarTodo();
                Refrescar();
            }, "", "Empieza de cero con la lista de equipos conocidos");
            Refrescar();
        }

        protected override void Suscribir()
        {
            Escuchar(h => App.Tools.Lan.Actualizado += h, h => App.Tools.Lan.Actualizado -= h);
            Escuchar(h => App.Scanner.ScanCompleted += h, h => App.Scanner.ScanCompleted -= h);
        }

        public override void Refrescar()
        {
            var lan = App.Tools.Lan;
            var filas = new List<Fila>();

            var sospechas = lan.Sospechas;
            foreach (var s in sospechas)
                filas.Add(new Fila("Red gemela: " + s.Ssid, s.BssidNueva, "", "", Kit.Hora(lan.UltimaLecturaWifi))
                {
                    Tinte = Kit.Coral,
                    Sub = "Ese nombre de wifi aparece con otra dirección de hardware",
                });

            foreach (var e in lan.Equipos().OrderBy(x => x.nombre).ThenBy(x => x.ip))
            {
                string mac = e.mac, ip = e.ip;
                filas.Add(new Fila(
                    string.IsNullOrEmpty(e.nombre) ? "equipo sin nombre" : e.nombre,
                    e.ip, e.mac, Kit.Hora(e.vistoUtc),
                    e.fiable ? "NetBIOS de la red" : "resolución inversa")
                {
                    Tinte = Kit.Dim,
                    Botones = { ("Confiar", () => { lan.Confiar(mac, ip); Refrescar(); },
                                 "Marcar como conocido para que no vuelva a saltar el aviso") },
                });
            }

            Panel.Filas(filas, 120);

            Panel.Extra.Children.Clear();
            Panel.Extra.Children.Add(Kit.Casilla("Avisar de dispositivos nuevos", lan.AvisarNuevos, on =>
            {
                lan.AvisarNuevos = on;
            }, "Cada equipo nuevo queda en el registro y en un aviso flotante"));

            string apagada = EstadoApagada("lan");
            Panel.Pie((apagada.Length > 0 ? apagada + "  ·  " : "") +
                      lan.Conocidos + " equipos guardados · " + App.Scanner.Devices.Count +
                      " en la última exploración · wifi leído " + Kit.Hora(lan.UltimaLecturaWifi) +
                      (sospechas.Count > 0 ? " · " + sospechas.Count + " red(es) sospechosa(s)" : ""));
        }
    }

    // ============================================================================
    //  9 · Puertos expuestos
    // ============================================================================

    internal sealed class PuertosPanel : HerramientaPanel
    {
        public PuertosPanel() : base("Qué está escuchando desde tu red",
            "Puerto atado a 127.0.0.1 solo lo ve tu PC; atado a 0.0.0.0 lo ve cualquiera de tu red.")
        {
            Id = "puertos";
            Panel.Cabecera(Columna.Fija("Programa", 200), Columna.Fija("Puerto", 62, true),
                           Columna.Fija("Protocolo", 86), Columna.Fija("Desde", 130),
                           Columna.Libre("Servicio"));
            Refrescar();
        }

        protected override void Suscribir()
        {
            Escuchar(h => App.Tools.Puertos.Actualizado += h, h => App.Tools.Puertos.Actualizado -= h);
        }

        public override void Refrescar()
        {
            var p = App.Tools.Puertos;
            var lista = p.Escuchadores();

            // Política de ENTRADA del firewall (cacheada; la primera pasada puede salir false
            // hasta que netsh responde, y a los 15 s se relee sola). Si Windows ya corta toda
            // la entrada, un puerto que escucha en 0.0.0.0 NO es alcanzable desde la red: deja
            // de ser rojo y pasa a verde con la nota de que está cubierto.
            bool entradaCortada = FirewallService.InboundBlockedNow();

            Panel.Filas(lista.Select(e =>
            {
                bool expuesto = e.Delicado && e.Alcance > 0;
                bool pcjCorta = expuesto && App.Firewall.PortBlocked(e.Puerto, e.EsTcp);
                bool cubierto = expuesto && (entradaCortada || pcjCorta);
                bool peligro = expuesto && !cubierto;

                string servicio = PortAuditService.NombrePuerto(e.Puerto)
                    + (string.IsNullOrEmpty(e.Servicio) ? "" : " · " + e.Servicio);

                var f = new Fila(e.App, e.Puerto.ToString(), e.EsTcp ? "TCP" : "UDP",
                                 PortAuditService.AlcanceTexto(e.Alcance), servicio)
                {
                    Tinte = peligro ? Kit.Coral : cubierto ? Kit.Verde : null,
                    Sub = e.Exe,
                    Tip = e.Direccion + " → " + e.Exe,
                };

                if (pcjCorta) { f.Chip = "cortado por PCJ"; f.ChipTinta = Kit.Verde; }
                else if (cubierto) { f.Chip = "firewall corta entrada"; f.ChipTinta = Kit.Verde; }
                else if (peligro) { f.Chip = "revisar"; f.ChipTinta = Kit.Coral; }

                string portapapeles = e.App + " · " + (e.EsTcp ? "TCP" : "UDP") + " · " + e.Puerto +
                                      " · " + PortAuditService.AlcanceTexto(e.Alcance) + " · " + e.Exe;
                f.Botones.Add(("Copiar", () =>
                {
                    try { Clipboard.SetText(portapapeles); } catch { }
                }, "Llevar esta fila al portapapeles"));

                if (expuesto)
                {
                    int puerto = e.Puerto; bool tcp = e.EsTcp;
                    if (pcjCorta)
                    {
                        f.Botones.Add(("Quitar bloqueo", () => CortarEntrada(puerto, tcp, false),
                            "Borra la regla de entrada que puso PCJ para este puerto (no toca el servicio de Windows)"));
                    }
                    else
                    {
                        f.Botones.Add(("Bloquear entrada", () => CortarEntrada(puerto, tcp, true),
                            cubierto
                                ? "El firewall ya corta la entrada por defecto; esto añade una regla explícita solo para este puerto"
                                : "Crea una regla de ENTRADA que corta este puerto, sin apagar el servicio de Windows"));
                    }
                }
                return f;
            }).ToList(), 200);

            Panel.Extra.Children.Clear();
            Panel.Extra.Children.Add(Kit.Casilla("Avisar si se abre un puerto nuevo", p.AvisarNuevos, on =>
            {
                p.AvisarNuevos = on;
                App.Settings.Current.PuertosAvisarNuevos = on;
                App.Settings.Save();
            }, "Cada escuchador nuevo queda en el registro y en un aviso flotante"));

            int fuera = lista.Count(x => x.Alcance > 0);
            int delicados = lista.Count(x => x.Delicado && x.Alcance > 0);
            int rojos = lista.Count(x => x.Delicado && x.Alcance > 0
                                         && !(entradaCortada || App.Firewall.PortBlocked(x.Puerto, x.EsTcp)));
            string cobertura = !FirewallService.PolicyReadable
                ? "no se pudo leer la política del firewall"
                : entradaCortada
                    ? "la entrada está cortada por defecto: los delicados quedan cubiertos salvo que otro permiso la abra"
                    : "la entrada NO está cortada por defecto: los rojos son alcanzables desde tu red";
            string apagada = EstadoApagada("puertos");
            Panel.Pie((apagada.Length > 0 ? apagada + "  ·  " : "") +
                      lista.Count + " escuchadores · " + fuera + " escuchan fuera de este PC · " +
                      delicados + " en puertos delicados · " + rojos + " sin cubrir · " + cobertura);
        }

        // Cortar o soltar la entrada de un puerto. La operación puede pedir UAC y tardar, así
        // que se va a un hilo aparte y la tabla se repinta al volver, sin congelar la ventana.
        private void CortarEntrada(int puerto, bool tcp, bool bloquear)
        {
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                bool ok = App.Firewall.BlockPortIn(puerto, tcp, bloquear);
                string err = FirewallService.LastError;
                EnUI(() =>
                {
                    string proto = tcp ? "TCP" : "UDP";
                    if (ok)
                    {
                        Aviso("Puertos expuestos", bloquear
                            ? "Entrada a " + puerto + " " + proto + " cortada."
                            : "Quitada la regla de entrada de " + puerto + " " + proto + ".");
                    }
                    else
                    {
                        MessageBox.Show((bloquear ? "No se pudo cortar la entrada: " : "No se pudo quitar el bloqueo: ")
                                        + (err.Length > 0 ? err : "Windows Firewall no confirmó el cambio."),
                            "Puertos expuestos", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                    Refrescar();
                });
            });
        }
    }

    // ============================================================================
    //  10 · Informe de seguridad
    // ============================================================================

    internal sealed class InformePanel : HerramientaPanel
    {
        private string _periodo = "30";
        private readonly HashSet<string> _secciones = new(ReportService.Secciones.Select(s => s.id));

        public InformePanel() : base("Resumen del periodo, en HTML o CSV",
            "Se monta con lo que PCJ ya tiene en disco. No manda nada a ningún sitio.")
        {
            Id = "informe";
            Panel.Cabecera();
            Panel.BarraDebajo = true;   // cuatro pastillas y cuatro botones: no caben junto al título
            Panel.Boton("Generar HTML", () => Generar(true), "",
                        "Página HTML en la carpeta de datos de PCJ");
            Panel.Boton("Generar CSV", () => Generar(false), "",
                        "Archivo CSV para abrirlo en una hoja de cálculo");
            Panel.Boton("Ver el último", VerUltimo, "", "Abrir el último informe generado");
            Panel.Boton("Abrir carpeta", () =>
            {
                try
                {
                    Directory.CreateDirectory(App.Tools.Informe.Carpeta);
                    Process.Start(new ProcessStartInfo("explorer.exe",
                        "\"" + App.Tools.Informe.Carpeta + "\"") { UseShellExecute = true });
                }
                catch { }
            }, "", "Carpeta donde PCJ guarda los informes");
            Refrescar();
        }

        private void Botonera()
        {
            Panel.LimpiarBarra();
            foreach (var (id, etiqueta) in new[] { ("hoy", "Hoy"), ("7", "7 días"), ("30", "30 días"), ("todo", "Todo") })
                Panel.Pastilla(etiqueta, () => { _periodo = id; Refrescar(); }, _periodo == id);
            Panel.Boton("Generar HTML", () => Generar(true), "", null);
            Panel.Boton("Generar CSV", () => Generar(false), "", null);
            Panel.Boton("Ver el último", VerUltimo, "", null);
            Panel.Boton("Abrir carpeta", () =>
            {
                try
                {
                    Directory.CreateDirectory(App.Tools.Informe.Carpeta);
                    Process.Start(new ProcessStartInfo("explorer.exe",
                        "\"" + App.Tools.Informe.Carpeta + "\"") { UseShellExecute = true });
                }
                catch { }
            }, "", null);
        }

        private void Generar(bool html)
        {
            Botonera();
            string err;
            string ruta = html
                ? App.Tools.Informe.GenerarHTML(_periodo, _secciones, out err)
                : App.Tools.Informe.GenerarCSV(_periodo, _secciones, out err);
            if (string.IsNullOrEmpty(ruta))
            {
                MessageBox.Show("No se pudo generar el informe: " + err, "Informe de seguridad",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                Refrescar();
                return;
            }
            App.Events.Add(EventKind.Info, "Informe de seguridad generado",
                (html ? "HTML" : "CSV") + " · " + ruta + " · periodo " + ReportService.Etiqueta(_periodo),
                important: false);
            if (MessageBox.Show("Informe listo:\n" + ruta + "\n\n¿Abrirlo ahora?", "Informe de seguridad",
                    MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                Abrir(ruta);
            Refrescar();
        }

        private void Abrir(string ruta)
        {
            try
            {
                Process.Start(new ProcessStartInfo(ruta) { UseShellExecute = true });
            }
            catch { }
        }

        private void VerUltimo()
        {
            var u = App.Tools.Informe.UltimoInforme;
            if (string.IsNullOrEmpty(u) || !File.Exists(u))
            {
                MessageBox.Show("Todavía no has generado ningún informe.", "Informe de seguridad",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            Abrir(u);
        }

        public override void Refrescar()
        {
            Botonera();
            Panel.Extra.Children.Clear();

            var titulo = Kit.TextoDe("Secciones del informe", 12.5, Kit.Texto, true);
            titulo.Margin = new Thickness(0, 0, 0, 6);
            Panel.Extra.Children.Add(titulo);

            var caja = new WrapPanel();
            foreach (var (id, nombre) in ReportService.Secciones)
            {
                string copia = id;
                caja.Children.Add(Kit.Casilla(nombre, _secciones.Contains(id), on =>
                {
                    if (on) _secciones.Add(copia); else _secciones.Remove(copia);
                }));
            }
            Panel.Extra.Children.Add(caja);

            var ult = App.Tools.Informe.UltimoInforme;
            Panel.Pie("Se guarda en " + App.Tools.Informe.Carpeta + " · " +
                      (string.IsNullOrEmpty(ult) ? "aún no has generado ninguno" : "último: " + Path.GetFileName(ult)) +
                      " · elige " + ReportService.Etiqueta(_periodo).ToLowerInvariant() +
                      " arriba y " + _secciones.Count + " de " + ReportService.Secciones.Length + " secciones");
        }
    }

    // ============================================================================
    //  Las dos que se mudaron desde la ventana principal
    // ============================================================================

    internal sealed class VistaEmpotrada : HerramientaPanel
    {
        public VistaEmpotrada(string id) : base(ToolCatalog.PorId(id)?.Nombre ?? id,
                                                ToolCatalog.PorId(id)?.Lema ?? "")
        {
            Id = id;
            Panel.Cabecera();
            FrameworkElement vista = id == "escaner" ? (FrameworkElement)new ScannerView() : new LogView();
            var marco = new Border
            {
                Height = 470,
                Margin = new Thickness(0, 2, 0, 0),
                Child = vista,
            };
            Panel.Extra.Children.Add(marco);
        }

        public override void Refrescar()
        {
            Panel.Pie("Sigue funcionando igual que antes: solo cambió de sitio.");
        }
    }

    // ============================================================================
    //  Fábrica
    // ============================================================================

    internal static class Herramientas
    {
        /// Crea la pantalla viva de una herramienta. Cada una se monta una sola vez por
        /// ventana y se queda en memoria mientras la ventana esté abierta.
        public static HerramientaPanel Crear(string id, IAnfitrion anfitrion)
        {
            HerramientaPanel p = id switch
            {
                "radar" => new RadarPanel(),
                "firmas" => new FirmasPanel(),
                "cuota" => new CuotaPanel(),
                "tiempo" => new TiempoPanel(),
                "perfiles" => new PerfilesPanel(),
                "fugas" => new FugasPanel(),
                "paises" => new PaisesPanel(),
                "lan" => new LanPanel(),
                "puertos" => new PuertosPanel(),
                "informe" => new InformePanel(),
                "escaner" or "registros" => new VistaEmpotrada(id),
                _ => new VistaEmpotrada("registros"),
            };
            p.Anfitrion = anfitrion;
            return p;
        }

        /// La misma pantalla pero dentro de la ventana principal, para la opción de Ajustes
        /// de quedarse con una herramienta siempre a mano.
        public static HerramientaPanel CrearEnPrincipal(string id, IAnfitrion anfitrion) => Crear(id, anfitrion);
    }
}
