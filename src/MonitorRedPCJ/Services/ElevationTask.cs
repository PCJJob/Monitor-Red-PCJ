using System;
using System.Diagnostics;

namespace MonitorRedPCJ.Services
{
    // Arranque siempre como administrador SIN volver a pedir UAC en cada arranque.
    //
    // Truco estándar: una tarea programada propia con "máximos privilegios". Cuando el usuario
    // abre el .exe (que por manifiesto corre sin permisos), este le pide al Programador de
    // tareas que lance la tarea —y esa copia sí arranca elevada y sin ventana de confirmación—
    // y él se cierra. La tarea se crea una sola vez: en la instalación, y si falta, la primera
    // vez que el programa corre elevado (auto-reparación).
    //
    // El argumento --via-task evita bucles: la copia lanzada por la tarea lo lleva, así que si
    // por lo que sea sigue sin permisos no vuelve a dispararse a sí misma.
    public static class ElevationTask
    {
        public const string TaskName = "MonitorRedPCJ";

        private static int RunHidden(string file, string args, int ms)
        {
            try
            {
                var psi = new ProcessStartInfo(file, args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var p = Process.Start(psi);
                if (p == null) return -1;
                // Se descarta la salida para que el buffer lleno no bloquee al proceso hijo.
                try { p.StandardOutput.ReadToEnd(); } catch { }
                if (!p.WaitForExit(ms)) { try { p.Kill(); } catch { } return -1; }
                return p.ExitCode;
            }
            catch { return -1; }
        }

        public static bool TaskExists()
            => RunHidden("schtasks.exe", "/Query /TN \"" + TaskName + "\" /FO LIST", 8000) == 0;

        // Pide al Programador de tareas que arranque la copia elevada. True si la lanzó.
        public static bool LaunchElevated()
            => RunHidden("schtasks.exe", "/Run /TN \"" + TaskName + "\"", 8000) == 0;

        // Registra la tarea. Hay que correr ya como administrador para poder hacerlo.
        public static bool EnsureRegistered()
        {
            if (TaskExists()) return true;
            string exe = Environment.ProcessPath ?? "";
            if (string.IsNullOrEmpty(exe)) return false;

            // Solo comillas simples dentro del guion de PowerShell, para no pelear con el
            // escapado del nivel de comandos.
            string script =
                "$a=New-ScheduledTaskAction -Execute '" + exe + "' -Argument '--via-task';" +
                "$s=New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries " +
                "-ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew;" +
                "Register-ScheduledTask -TaskName '" + TaskName + "' -Action $a -Settings $s " +
                "-RunLevel Highest -Force | Out-Null";
            RunHidden("powershell.exe", "-NoProfile -NonInteractive -Command \"" + script + "\"", 20000);
            return TaskExists();
        }
    }
}
