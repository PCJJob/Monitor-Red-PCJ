using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MonitorRedPCJ.Models;

namespace MonitorRedPCJ.Services
{
    // Una fila del inventario: cada ejecutable que el monitor conoce, haya conectado o no.
    public class InventoryEntry
    {
        public string ExePath = "";
        public string Name = "";
        public string Folder = "";
        public TrackedApp? App;          // null si nunca conectó (solo está en marcha)
        public bool Running;
        public bool IsSystem;
        public double Bps;
        public long TotalRx, TotalTx;
    }

    // Inventario común de aplicaciones: lo que aparace en la pestaña Protección y en el
    // asistente "Quién puede salir a internet" tiene que ser el mismo conjunto, y no solo
    // el histórico de tráfico. Con el bloqueo total una app recién instalada no conecta
    // nunca, así que si solo se mirara el histórico no aparecería jamás.
    public static class AppInventory
    {
        private static readonly string Windir =
            (Environment.GetFolderPath(Environment.SpecialFolder.Windows) ?? "").TrimEnd('\\');

        // Sistema = lo que vive dentro de Windows. Lo instalado por el usuario (Program Files,
        // WindowsApps, %LOCALAPPDATA%) cuenta como "mis aplicaciones", aunque sea un servicio.
        public static bool IsSystemPath(string exePath)
            => !string.IsNullOrEmpty(Windir) &&
               exePath.StartsWith(Windir + "\\", StringComparison.OrdinalIgnoreCase);

        public static string FolderOf(string exePath)
        {
            try { return Path.GetDirectoryName(exePath) ?? ""; } catch { return ""; }
        }

        public static List<InventoryEntry> Collect(string filter = "")
        {
            var list = new List<InventoryEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string f = (filter ?? "").Trim().ToLowerInvariant();

            foreach (var app in App.Traffic.GetApps())
            {
                string exe = app.ExePath;
                if (string.IsNullOrEmpty(exe) || !seen.Add(exe)) continue;
                if (f.Length > 0 && !Matches(exe, app.Name, f)) continue;
                list.Add(new InventoryEntry
                {
                    ExePath = exe,
                    Name = string.IsNullOrEmpty(app.Name) ? Path.GetFileNameWithoutExtension(exe) : app.Name,
                    Folder = FolderOf(exe),
                    App = app,
                    Running = app.IsRunning,
                    IsSystem = IsSystemPath(exe),
                    Bps = app.ReceivedBps + app.SentBps,
                    TotalRx = app.TotalReceived,
                    TotalTx = app.TotalSent,
                });
            }

            // En marcha pero sin histórico: son justo las que el bloqueo total dejó sin salida.
            foreach (var exe in App.Traffic.GetRunningExes())
            {
                if (!seen.Add(exe)) continue;
                string name = Path.GetFileNameWithoutExtension(exe);
                if (f.Length > 0 && !Matches(exe, name, f)) continue;
                list.Add(new InventoryEntry
                {
                    ExePath = exe,
                    Name = name,
                    Folder = FolderOf(exe),
                    App = null,
                    Running = true,
                    IsSystem = IsSystemPath(exe),
                });
            }

            // En marcha primero, luego por tráfico y alfabéticamente: lo que el usuario tiene
            // abierto es lo que necesita revisar.
            return list
                .OrderByDescending(e => e.Running)
                .ThenByDescending(e => e.Bps)
                .ThenByDescending(e => e.TotalRx + e.TotalTx)
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static bool Matches(string exe, string name, string filter)
            => name.ToLowerInvariant().Contains(filter) ||
               exe.ToLowerInvariant().Contains(filter);

        // Navegadores conocidos: es lo primero que se nota cortado, así que el permiso
        // automático se los da sin preguntar.
        public static bool IsBrowserName(string exePath)
        {
            string n = (Path.GetFileNameWithoutExtension(exePath) ?? "").ToLowerInvariant();
            return n == "chrome" || n == "msedge" || n == "firefox" || n == "opera" ||
                   n == "brave" || n == "vivaldi" || n == "opera_gui";
        }

        // Qué se marca solo al entrar en bloqueo total: lo esencial para que haya red, el
        // navegador y lo que ya tuviera permiso de salida. Es la misma regla que usa el
        // panel de «Elegir apps», para que activar el corte y luego repasarlo no se contradigan.
        public static bool AutoAllowed(string exePath, Decision decision, RiskInfo risk)
            => decision == Decision.Permitido || IsBrowserName(exePath) ||
               risk.Level == RiskLevel.EsencialRed;

        // La lista de salida con la que se activa el bloqueo total de un solo toque.
        public static List<string> SuggestedAllowed()
        {
            var list = new List<string>();
            foreach (var e in Collect())
            {
                var risk = AppRisk.Analyze(e.ExePath);
                var d = App.Firewall.GetDecision(e.ExePath, Direction.Out);
                if (AutoAllowed(e.ExePath, d, risk)) list.Add(e.ExePath);
            }
            return list;
        }
    }
}
