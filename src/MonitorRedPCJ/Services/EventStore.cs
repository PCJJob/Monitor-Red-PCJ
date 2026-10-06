using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using MonitorRedPCJ.Models;

namespace MonitorRedPCJ.Services
{
    public class EventStore
    {
        private readonly JsonStore<List<NetEvent>> _store = new JsonStore<List<NetEvent>>(Paths.EventsFile);
        private readonly object _lock = new object();
        public List<NetEvent> Events { get; private set; }

        public event Action? EventAdded;

        public EventStore()
        {
            Events = _store.Load(() => new List<NetEvent>());
        }

        public void Add(EventKind kind, string message, string detail = "", bool important = true)
        {
            var ev = new NetEvent
            {
                TimeUtc = DateTime.UtcNow,
                Kind = kind,
                Message = message,
                Detail = detail,
                Important = important,
            };
            lock (_lock)
            {
                Events.Insert(0, ev);
                if (Events.Count > 5000) Events.RemoveRange(5000, Events.Count - 5000);
                Persist();
            }
            EventAdded?.Invoke();
        }

        // En incógnito el repaso se ve en pantalla pero no se escribe: al cerrar la app, ese
        // tramo de registro no existió para nadie que abra el PC después.
        private void Persist()
        {
            if (Recording.Incognito) return;
            _store.Save(Events);
        }

        // ---------- Modo incógnito ----------

        // Al entrar se guarda una foto del registro tal como estaba; al salir se vuelve a esa
        // foto. Así el tramo de avisos visto durante el incógnito no queda en events.json ni
        // asoma después, igual que el histórico de tráfico.
        private string? _beforeIncognito;

        /// on = incógnito activado; on = false se desactiva y se recupera lo de antes.
        public void ApplyIncognito(bool on)
        {
            if (on)
            {
                lock (_lock)
                {
                    try { _beforeIncognito = JsonSerializer.Serialize(Events); }
                    catch { _beforeIncognito = null; }
                }
                return;
            }

            string? backup;
            lock (_lock) { backup = _beforeIncognito; _beforeIncognito = null; }
            if (string.IsNullOrEmpty(backup)) return;

            List<NetEvent>? restored = null;
            try { restored = JsonSerializer.Deserialize<List<NetEvent>>(backup); } catch { }
            if (restored == null) return;

            lock (_lock)
            {
                Events = restored;
                _store.Save(Events);
            }
            EventAdded?.Invoke();
        }

        // Vaciar el registro a propósito (botón de Configuración): memoria y disco.
        public void Clear()
        {
            lock (_lock)
            {
                Events.Clear();
                _store.Save(Events);
            }
            EventAdded?.Invoke();
        }

        public void MarkAllRead()
        {
            lock (_lock)
            {
                foreach (var e in Events) e.Read = true;
                Persist();
            }
            EventAdded?.Invoke();
        }

        public int UnreadCount
        {
            get { lock (_lock) return Events.Count(e => !e.Read && e.Important); }
        }

        // Copia para la UI: la lista viva cambia desde el hilo de sondeo.
        public List<NetEvent> Snapshot()
        {
            lock (_lock) return new List<NetEvent>(Events);
        }
    }
}
