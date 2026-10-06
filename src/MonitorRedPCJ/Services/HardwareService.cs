using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace MonitorRedPCJ.Services
{
    // Lectura de CPU, memoria, disco y GPU vía PerformanceCounters
    public class HardwareService
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
        private PerformanceCounter? _cpu;
        private PerformanceCounter? _diskRead;
        private PerformanceCounter? _diskWrite;
        private List<PerformanceCounter> _gpu = new();
        private DateTime _gpuRefreshed = DateTime.MinValue;

        public double CpuPercent { get; private set; }
        public double MemoryUsedGb { get; private set; }
        public double MemoryTotalGb { get; private set; }
        public double DiskReadBps { get; private set; }
        public double DiskWriteBps { get; private set; }
        public double GpuPercent { get; private set; } = -1; // -1 = no disponible

        // Temperatura del disco (NVMe/M.2 o SATA). Se lee por WMI, que es lento y —en algunos
        // equipos— exige permisos de administrador, así que va en segundo plano y cacheada.
        public double? DiskTempC { get; private set; }
        public string DiskTempModel { get; private set; } = "";
        private DateTime _tempAt = DateTime.MinValue;
        private int _tempBusy;
        private const double TempSeconds = 20;

        public HardwareService()
        {
            try { _cpu = new PerformanceCounter("Processor", "% Processor Time", "_Total"); } catch { }
            try { _diskRead = new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", "_Total"); } catch { }
            try { _diskWrite = new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", "_Total"); } catch { }
            var mem = GC.GetGCMemoryInfo();
            MemoryTotalGb = mem.TotalAvailableMemoryBytes / 1024.0 / 1024 / 1024;
        }

        // "GPU Engine" tiene una instancia por proceso y motor, así que se leen todas las
        // del motor 3D y se toma el máximo; la lista cambia cuando nacen o mueren procesos.
        private void RebuildGpu()
        {
            _gpuRefreshed = DateTime.UtcNow;
            var list = new List<PerformanceCounter>();
            try
            {
                var names = new PerformanceCounterCategory("GPU Engine").GetInstanceNames().ToList();
                var engines = names.Where(n => n.Contains("engtype_3D")).ToList();
                if (engines.Count == 0) engines = names.Where(n => n.Contains("phys0")).ToList();
                foreach (var name in engines)
                {
                    try { list.Add(new PerformanceCounter("GPU Engine", "Utilization Percentage", name)); } catch { }
                }
            }
            catch { }
            _gpu = list;
        }

        private double ReadGpu()
        {
            if (_gpu.Count == 0 || DateTime.UtcNow - _gpuRefreshed > TimeSpan.FromSeconds(20)) RebuildGpu();
            double best = -1;
            foreach (var c in _gpu)
            {
                try { best = Math.Max(best, c.NextValue()); } catch { }
            }
            return best;
        }

        public void Sample()
        {
            try { if (_cpu != null) CpuPercent = _cpu.NextValue(); } catch { }
            try { if (_diskRead != null) DiskReadBps = _diskRead.NextValue(); } catch { }
            try { if (_diskWrite != null) DiskWriteBps = _diskWrite.NextValue(); } catch { }
            try { GpuPercent = ReadGpu(); } catch { }

            try
            {
                var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
                if (GlobalMemoryStatusEx(ref m))
                {
                    MemoryTotalGb = m.ullTotalPhys / 1024.0 / 1024 / 1024;
                    MemoryUsedGb = (m.ullTotalPhys - m.ullAvailPhys) / 1024.0 / 1024 / 1024;
                }
            }
            catch { }

            MaybeRefreshDiskTemp();
        }

        // Lanza la lectura de temperatura como mucho cada TempSeconds y solo si no hay otra en
        // marcha: powershell tarda y no debe repetirse cada muestra (que va a 1 por segundo).
        private void MaybeRefreshDiskTemp()
        {
            if ((DateTime.Now - _tempAt).TotalSeconds < TempSeconds) return;
            if (Interlocked.CompareExchange(ref _tempBusy, 1, 0) != 0) return;
            _tempAt = DateTime.Now;
            Task.Run(() =>
            {
                try
                {
                    var (model, temp) = ReadDiskTemp();
                    if (temp.HasValue) { DiskTempModel = model; DiskTempC = temp; }
                }
                finally { Interlocked.Exchange(ref _tempBusy, 0); }
            });
        }

        // MSFT_StorageReliabilityCounter (la misma fuente que usa el panel «Propiedades de
        // disco avanzadas» de Windows) da la temperatura por disco físico. Se elige el primero
        // que la informe —en un portátil con un solo NVMe, ese es el M.2.
        //
        // Dos cosas probadas en la máquina real:
        //  · El cmdlet NO se puede llamar suelto: sin disco delante Windows responde
        //    «No se puede resolver el conjunto de parámetros» y no devuelve nada. Hay que
        //    canalizar Get-PhysicalDisk | Get-StorageReliabilityCounter.
        //  · Exige permisos de administrador; sin ellos responde «El cliente no tenía acceso
        //    disponible a un recurso CIM». Por eso esta marca va de la mano del arranque
        //    siempre elevado.
        private static (string model, double? temp) ReadDiskTemp()
        {
            const string script =
                "$ProgressPreference='SilentlyContinue';" +
                "foreach($d in (Get-PhysicalDisk -EA SilentlyContinue)){" +
                "$r=$d|Get-StorageReliabilityCounter -EA SilentlyContinue|?{$null -ne $_.Temperature}|select -First 1;" +
                "if($r){$d.FriendlyName+'|'+$r.Temperature;break}}";
            try
            {
                // -EncodedCommand (Base64 UTF-16) evita cualquier lío de comillas al pasar el
                // guion por la línea de órdenes de Windows.
                string enc = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
                var psi = new ProcessStartInfo("powershell.exe",
                    "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + enc)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p == null) return ("", null);
                // El error se drena aparte: si se llena el búfer, powershell se queda esperando.
                var err = p.StandardError.ReadToEndAsync();
                string o = p.StandardOutput.ReadToEnd().Trim();
                if (!p.WaitForExit(15000)) { try { p.Kill(); } catch { } }
                int bar = o.IndexOf('|');
                if (bar <= 0) return ("", null);
                string model = o.Substring(0, bar).Trim();
                if (double.TryParse(o.Substring(bar + 1).Trim(),
                        System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out double t))
                    return (model, t);
                return (model, null);
            }
            catch { return ("", null); }
        }
    }
}
