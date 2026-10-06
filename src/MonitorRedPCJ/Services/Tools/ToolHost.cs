using System;
using System.Collections.Generic;
using System.Linq;
using MonitorRedPCJ.Models;

namespace MonitorRedPCJ.Services.Tools
{
    /// Dueño del ciclo de vida de las diez herramientas. Cada una tiene su servicio, pero solo
    /// se arranca el que el usuario tiene encendido: con todo apagado no hay ningún hilo, ningún
    /// timer y ninguna lectura de red añadida por PCJ.
    public class ToolHost
    {
        public static ToolHost? Instancia { get; private set; }

        public RadarService Radar { get; } = new();
        public SignatureService Firmas { get; } = new();
        public QuotaService Cuota { get; } = new();
        public TimeMachineService Tiempo { get; } = new();
        public ProfileService Perfiles { get; } = new();
        public LeakAuditService Fugas { get; } = new();
        public GeoService Paises { get; } = new();
        public LanGuardService Lan { get; } = new();
        public PortAuditService Puertos { get; } = new();
        public ReportService Informe { get; } = new();

        public event Action? EstadoCambiado;

        private readonly HashSet<string> _activas = new(StringComparer.OrdinalIgnoreCase);

        public ToolHost()
        {
            Instancia = this;
        }

        public bool Activa(string id) => _activas.Contains(id);

        /// Arranca lo que el usuario dejó encendido la última vez. Se llama después de tener
        /// el tráfico y el escáner en marcha, porque varias herramientas se apoyan en ellos.
        public void Arrancar()
        {
            foreach (var t in ToolCatalog.Nuevas)
                if (ToolCatalog.Activada(t.Id)) Encender(t.Id, avisar: false);
            // El candado se comprueba aunque la herramienta esté apagada: si el equipo se
            // reinició con él puesto, la salida sigue cortada y hay que devolverla.
            try { Perfiles.ComprobarCandadoSueltoEnArranque(); } catch { }
        }

        /// Apaga todo al cerrar PCJ.
        public void Parar()
        {
            foreach (var id in _activas.ToList()) Apagar(id);
        }

        public void Aplicar(string id, bool on)
        {
            if (on) Encender(id, avisar: true);
            else Apagar(id);
            EstadoCambiado?.Invoke();
        }

        private void Encender(string id, bool avisar)
        {
            if (_activas.Contains(id)) return;
            try
            {
                switch (id)
                {
                    case "radar": Radar.Start(); break;
                    case "firmas": Firmas.Start(); break;
                    case "cuota": Cuota.Start(); break;
                    case "tiempo": Tiempo.Start(); break;
                    case "perfiles": Perfiles.Start(); break;
                    case "fugas": Fugas.Start(App.Settings.Current.AuditorCadaHora); break;
                    case "paises":
                        Paises.AvisarNuevos = App.Settings.Current.PaisesAvisarNuevos;
                        Paises.Start();
                        break;
                    case "lan": Lan.Start(); break;
                    case "puertos":
                        Puertos.AvisarNuevos = App.Settings.Current.PuertosAvisarNuevos;
                        Puertos.Start();
                        break;
                    case "informe": Informe.Start(); break;
                    default: return;
                }
            }
            catch { return; }
            _activas.Add(id);
            if (avisar)
            {
                var t = ToolCatalog.PorId(id);
                App.Events.Add(EventKind.Info,
                    "Herramienta encendida: " + (t?.Nombre ?? id),
                    t?.Consumo ?? "", important: false);
            }
        }

        private void Apagar(string id)
        {
            if (!_activas.Contains(id)) return;
            _activas.Remove(id);
            try
            {
                switch (id)
                {
                    case "radar": Radar.Stop(); break;
                    case "firmas": Firmas.Stop(); break;
                    case "cuota": Cuota.Stop(); break;
                    case "tiempo": Tiempo.Stop(); break;
                    case "perfiles": Perfiles.Stop(); break;
                    case "fugas": Fugas.Stop(); break;
                    case "paises": Paises.Stop(); break;
                    case "lan": Lan.Stop(); break;
                    case "puertos": Puertos.Stop(); break;
                    case "informe": Informe.Stop(); break;
                }
            }
            catch { }
        }

        /// Lo que necesita la ventana para el pie de página: cuántas herramientas están
        /// encendidas y por eso cuántos hilos propios hay en marcha.
        public int Encendidas => _activas.Count;
    }
}
