using System;

namespace MonitorRedPCJ.Services
{
    // El único interruptor del modo incógnito. Mientras está apagado el registro, ninguna
    // pieza escribe histórico en disco (apps.json, events.json, devices.json): la
    // monitorización en vivo, los avisos y las reglas del firewall siguen igual.
    //
    // Es estático a propósito: lo consultan tres servicios distintos que se construyen en
    // momentos diferentes, y una bandera en cada uno se desincronizaría.
    public static class Recording
    {
        private static readonly object L = new object();

        public static event Action? Changed;

        private static bool _enabled = true;

        public static bool Enabled
        {
            get { lock (L) return _enabled; }
        }

        // Cómodo para leer: if (Recording.Incognito) return;
        public static bool Incognito
        {
            get { lock (L) return !_enabled; }
        }

        public static void Set(bool enabled)
        {
            lock (L)
            {
                if (_enabled == enabled) return;
                _enabled = enabled;
            }
            Changed?.Invoke();
        }
    }
}
