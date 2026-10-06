using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MonitorRedPCJ.Models;

namespace MonitorRedPCJ.Services.Tools
{
    public class Perfil
    {
        public string Nombre { get; set; } = "";
        public DateTime CreadoUtc { get; set; }
        public List<string> Permitidas { get; set; } = new();
    }

    internal class PerfilesDatos
    {
        public List<Perfil> perfiles { get; set; } = new();
    }

    /// Perfiles y candado: conjuntos de permisos de salida guardados con nombre, y el corte
    /// total de la salida con vuelta atrás exacta.
    public class ProfileService
    {
        public static ProfileService? Instancia { get; private set; }

        public event Action? Actualizado;

        private readonly object _lock = new object();
        private readonly string _fichero;
        private PerfilesDatos _d = new();

        public ProfileService()
        {
            _fichero = Path.Combine(Paths.Root, "perfiles.json");
            Instancia = this;
            Cargar();
        }

        public void Start() { ComprobarCandadoSueltoEnArranque(); }
        public void Stop() { }

        // ---------- Qué está permitido ahora ----------

        /// Las aplicaciones que ahora mismo tienen regla de permiso de salida. Es la foto que
        /// se guarda en un perfil: se lee del firewall, no de la memoria, para que el perfil
        /// coincida con lo que de verdad ocurre en Windows.
        public List<string> PermitidasActuales()
        {
            var snapshot = App.Firewall.RuleSnapshot();
            var salida = new List<string>();
            foreach (var a in SignatureService.AppsConocidas())
                if (snapshot.Contains(FirewallService.AllowRuleName(a, Direction.Out)))
                    salida.Add(a);
            return salida;
        }

        public List<Perfil> Perfiles()
        {
            lock (_lock) return _d.perfiles
                .OrderBy(p => p.Nombre, StringComparer.OrdinalIgnoreCase)
                .Select(p => new Perfil
                {
                    Nombre = p.Nombre,
                    CreadoUtc = p.CreadoUtc,
                    Permitidas = new List<string>(p.Permitidas),
                })
                .ToList();
        }

        public string Error { get; private set; } = "";

        public bool Guardar(string nombre, out string error)
        {
            error = "";
            nombre = (nombre ?? "").Trim();
            if (nombre.Length == 0) { error = "Ponle un nombre al perfil."; return false; }
            var lista = PermitidasActuales();
            if (lista.Count == 0)
            {
                error = "Ahora mismo no hay ninguna aplicación con permiso de salida guardado, así que " +
                        "el perfil saldría vacío. Dale salida a lo que quieras permitir y guárdalo.";
                return false;
            }
            lock (_lock)
            {
                var viejo = _d.perfiles.FirstOrDefault(p => p.Nombre.Equals(nombre, StringComparison.OrdinalIgnoreCase));
                if (viejo != null) _d.perfiles.Remove(viejo);
                _d.perfiles.Add(new Perfil
                {
                    Nombre = nombre,
                    CreadoUtc = DateTime.UtcNow,
                    Permitidas = lista,
                });
            }
            GuardarArchivo();
            App.Events.Add(EventKind.RuleChanged, "Perfil «" + nombre + "» guardado con " +
                lista.Count + " aplicaciones", "", important: false);
            Actualizado?.Invoke();
            return true;
        }

        public bool Borrar(string nombre)
        {
            lock (_lock) _d.perfiles.RemoveAll(p => p.Nombre.Equals(nombre, StringComparison.OrdinalIgnoreCase));
            GuardarArchivo();
            Actualizado?.Invoke();
            return true;
        }

        /// Aplica un perfil: bloqueo total con esta lista exacta de permitidas.
        /// Pasa por permisos de administrador (UAC) porque cambia la política de salida.
        public bool Aplicar(string nombre, out string error)
        {
            error = "";
            Perfil? p;
            lock (_lock) p = _d.perfiles.FirstOrDefault(x => x.Nombre.Equals(nombre, StringComparison.OrdinalIgnoreCase));
            if (p == null) { error = "Ese perfil ya no existe."; return false; }

            var vivas = p.Permitidas.Where(File.Exists).ToList();
            if (vivas.Count == 0)
            {
                error = "Ninguna aplicación de ese perfil sigue instalada en su ruta, así que aplicarlo " +
                        "dejaría el equipo sin salida.";
                return false;
            }

            bool ok = App.Firewall.SetStrictMode(true, vivas);
            if (!ok)
            {
                error = FirewallService.LastError.Length > 0 ? FirewallService.LastError
                    : "Windows no aceptó el cambio de política de salida.";
                return false;
            }
            App.Events.Add(EventKind.ProtectionToggled, "Monitor de Red PCJ aplicó el perfil «" + nombre + "»",
                vivas.Count + " aplicaciones con salida; el resto quedó cortado.", important: true);
            Actualizado?.Invoke();
            return true;
        }

        /// Quita el bloqueo total: vuelve al modo normal, donde cada app sale según su regla.
        public bool VolverAlModoNormal(out string error)
        {
            error = "";
            bool ok = App.Firewall.SetStrictMode(false, null);
            if (!ok)
            {
                error = FirewallService.LastError.Length > 0 ? FirewallService.LastError
                    : "Windows no aceptó el cambio de política de salida.";
                return false;
            }
            App.Events.Add(EventKind.ProtectionToggled,
                "Monitor de Red PCJ quitó el bloqueo total (perfiles)", "", important: true);
            Actualizado?.Invoke();
            return true;
        }

        // ---------- Candado ----------

        public bool CandadoPuesto => FirewallService.CandadoEnMarcador();

        /// El bloqueo total ya corta la salida por defecto: encima el candado no aportaría nada,
        /// así que la interfaz lo muestra como «ya estás cortado» en vez de permitir pulsarlo.
        public bool SalidaYaCortada
        {
            get
            {
                try { return App.Settings.Current.StrictMode || FirewallService.IsOutboundDefaultBlocked(); }
                catch { return false; }
            }
        }

        public bool PonerCandado(out string error)
        {
            error = "";
            if (SalidaYaCortada)
            {
                error = "La salida ya está cortada con el bloqueo total: usa «Soltar el candado» desde " +
                        "Protección para volver al modo normal.";
                return false;
            }
            bool ok = App.Firewall.Candado(true);
            if (!ok)
            {
                error = FirewallService.LastError.Length > 0 ? FirewallService.LastError
                    : "Windows no aceptó cortar la salida.";
                return false;
            }
            App.Events.Add(EventKind.ProtectionToggled,
                "Monitor de Red PCJ cortó toda la salida (candado)",
                "Tus reglas no se borraron: se pausaron. Al soltar el candado vuelven tal cual.",
                important: true);
            Actualizado?.Invoke();
            return true;
        }

        public bool SoltarCandado(out string error)
        {
            error = "";
            bool ok = App.Firewall.Candado(false);
            if (!ok)
            {
                error = FirewallService.LastError.Length > 0 ? FirewallService.LastError
                    : "Windows no aceptó devolver la salida.";
                return false;
            }
            App.Events.Add(EventKind.ProtectionToggled,
                "Monitor de Red PCJ soltó el candado: la salida vuelve a la normalidad", "", important: true);
            Actualizado?.Invoke();
            return true;
        }

        /// Si el equipo se apagó con el candado puesto, la política de Windows sigue cortada al
        /// arrancar. PCJ la suelta y lo anota: nadie se queda sin internet por un olvido.
        public void ComprobarCandadoSueltoEnArranque()
        {
            try
            {
                if (!FirewallService.CandadoEnMarcador()) return;
                if (!FirewallService.IsAdmin)
                {
                    App.Events.Add(EventKind.ProtectionToggled,
                        "El candado sigue puesto y esta copia no tiene permisos para quitarlo",
                        "Reinicia PCJ como administrador o suéltalo desde Perfiles y candado.",
                        important: true);
                    return;
                }
                string error;
                if (SoltarCandado(out error))
                    App.Events.Add(EventKind.Info,
                        "El candado se quitó solo al arrancar",
                        "El equipo se apagó o reinició con la salida cortada; Monitor de Red PCJ la devolvió.",
                        important: true);
            }
            catch { }
        }

        // ---------- Persistencia ----------

        private void Cargar()
        {
            try
            {
                if (!File.Exists(_fichero)) return;
                var leido = JsonSerializer.Deserialize<PerfilesDatos>(File.ReadAllText(_fichero));
                if (leido?.perfiles != null) _d = leido;
            }
            catch { }
        }

        private void GuardarArchivo()
        {
            try
            {
                PerfilesDatos copia;
                lock (_lock)
                {
                    copia = new PerfilesDatos
                    {
                        perfiles = _d.perfiles.Select(p => new Perfil
                        {
                            Nombre = p.Nombre,
                            CreadoUtc = p.CreadoUtc,
                            Permitidas = new List<string>(p.Permitidas),
                        }).ToList()
                    };
                }
                Paths.Ensure();
                File.WriteAllText(_fichero, JsonSerializer.Serialize(copia));
            }
            catch { }
        }
    }
}
