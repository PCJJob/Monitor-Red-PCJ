using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MonitorRedPCJ.Models;
using MonitorRedPCJ.Native;

namespace MonitorRedPCJ.Services
{
    // Escáner LAN: descubre la subred del adaptador activo, hace ping sweep + ARP
    // y resuelve nombres por DNS inverso.
    public class NetworkScannerService
    {
        private readonly EventStore _events;
        private readonly JsonStore<List<DeviceInfo>> _store = new JsonStore<List<DeviceInfo>>(Paths.DevicesFile);
        private List<DeviceInfo> _devices;
        private readonly object _lock = new object();
        private int _scanning;

        public event Action? ScanCompleted;

        // Leer los adaptadores (GetAllNetworkInterfaces) es caro y la ventana principal lo
        // pide cada segundo. Se cachea el nombre y solo se vuelve a leer cada 20 segundos
        // o cuando Windows avisa de un cambio de red.
        private string _networkName = "Red";
        private DateTime _networkNameAt = DateTime.MinValue;
        private const double NetworkNameSeconds = 20;

        public NetworkScannerService(EventStore events)
        {
            _events = events;
            _devices = _store.Load(() => new List<DeviceInfo>());
            RefreshNetworkName(force: true);
            try { System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += (s, e) => RefreshNetworkName(); }
            catch { }
        }

        private void RefreshNetworkName(bool force = false)
        {
            if (!force && (DateTime.Now - _networkNameAt).TotalSeconds < NetworkNameSeconds) return;
            _networkNameAt = DateTime.Now;
            try
            {
                var adapter = NativeNet.GetAdapters().FirstOrDefault(a => a.Up && !string.IsNullOrEmpty(a.Ip));
                string name = adapter?.Name ?? "Red";
                _networkName = string.IsNullOrEmpty(name) ? "Red" : name;
            }
            catch { _networkName = "Red"; }
        }

        public string CurrentNetworkName() => _networkName;

        public IReadOnlyList<DeviceInfo> Devices
        {
            get { lock (_lock) return _devices.OrderByDescending(d => d.LastSeenUtc).ToList(); }
        }

        public bool IsScanning => _scanning == 1;

        public void ScanAsync()
        {
            if (Interlocked.CompareExchange(ref _scanning, 1, 0) != 0) return;
            Task.Run(() =>
            {
                try { Scan(); }
                finally
                {
                    Interlocked.Exchange(ref _scanning, 0);
                    ScanCompleted?.Invoke();
                }
            });
        }

        private void Scan()
        {
            var adapter = NativeNet.GetAdapters().FirstOrDefault(a => a.Up && !string.IsNullOrEmpty(a.Ip));
            if (adapter == null) return;

            // Subred /24 del adaptador (suficiente para redes domésticas)
            var parts = adapter.Ip.Split('.');
            string prefix = string.Join(".", parts.Take(3));

            var found = new List<DeviceInfo>();
            var tasks = new List<Task>();
            using var sem = new SemaphoreSlim(48); // 48 "hilos" de escaneo

            for (int i = 1; i <= 254; i++)
            {
                string ip = $"{prefix}.{i}";
                tasks.Add(Task.Run(async () =>
                {
                    await sem.WaitAsync();
                    try
                    {
                        var d = Probe(ip);
                        if (d != null) lock (found) found.Add(d);
                    }
                    finally { sem.Release(); }
                }));
            }
            Task.WaitAll(tasks.ToArray(), TimeSpan.FromSeconds(40));

            lock (_lock)
            {
                foreach (var d in found)
                {
                    var existing = _devices.FirstOrDefault(x => x.Ip == d.Ip);
                    if (existing == null)
                    {
                        _devices.Add(d);
                        _events.Add(EventKind.NewDevice, "Nuevo dispositivo en la red",
                            $"{d.Name} ({d.Ip})", important: true);
                    }
                    else
                    {
                        existing.LastSeenUtc = d.LastSeenUtc;
                        existing.Mac = d.Mac;
                        if (!string.IsNullOrEmpty(d.Name)) existing.Name = d.Name;
                    }
                }
                // En incógnito no se guarda qué dispositivos se vieron: la lista en pantalla
                // sigue estando, pero al cerrar la app no queda rastro en devices.json.
                if (!Recording.Incognito) _store.Save(_devices);
            }
        }

        // ---------- Modo incógnito ----------

        // Igual que con el tráfico: durante el incógnito la lista de dispositivos se ve en
        // pantalla, pero no se escribe en devices.json, y al desactivar el modo se vuelve a la
        // foto de cuando se activó, para que los equipos vistos en ese rato no queden guardados.
        private string? _beforeIncognito;

        /// on = incógnito activado; on = false se desactiva y se recupera la lista de antes.
        public void ApplyIncognito(bool on)
        {
            if (on)
            {
                lock (_lock)
                {
                    try { _beforeIncognito = JsonSerializer.Serialize(_devices); }
                    catch { _beforeIncognito = null; }
                }
                return;
            }

            string? backup;
            lock (_lock) { backup = _beforeIncognito; _beforeIncognito = null; }
            if (string.IsNullOrEmpty(backup)) return;

            List<DeviceInfo>? restored = null;
            try { restored = JsonSerializer.Deserialize<List<DeviceInfo>>(backup); } catch { }
            if (restored == null) return;

            lock (_lock)
            {
                _devices = restored;
                _store.Save(_devices);
            }
            ScanCompleted?.Invoke();
        }

        // Vaciar el listado de dispositivos a propósito (botón de Configuración).
        public void ClearDevices()
        {
            lock (_lock)
            {
                _devices.Clear();
                _store.Save(_devices);
            }
            ScanCompleted?.Invoke();
        }

        private DeviceInfo? Probe(string ip)
        {
            try
            {
                using var ping = new Ping();
                var reply = ping.Send(ip, 250);
                if (reply == null || reply.Status != IPStatus.Success) return null;
            }
            catch { return null; }

            var info = new DeviceInfo { Ip = ip, LastSeenUtc = DateTime.UtcNow };
            try { info.Mac = NativeNet.GetMacForIp(ip) ?? ""; } catch { }
            try
            {
                var entry = Dns.GetHostEntry(ip);
                info.Name = entry.HostName.Split('.')[0];
                info.Description = entry.HostName;
            }
            catch { info.Name = ip; }
            return info;
        }
    }
}
