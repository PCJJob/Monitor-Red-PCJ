using Microsoft.Win32;
using System;

namespace MonitorRedPCJ.Services
{
    // Arranjo en inicio vía clave Run del usuario (no requiere admin)
    public static class StartupHelper
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ValueName = "MonitorRedPCJ";

        public static bool IsEnabled()
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(RunKey);
                return k?.GetValue(ValueName) != null;
            }
            catch { return false; }
        }

        public static void SetEnabled(bool enabled)
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                    ?? Registry.CurrentUser.CreateSubKey(RunKey);
                if (enabled && Environment.ProcessPath != null)
                    k.SetValue(ValueName, $"\"{Environment.ProcessPath}\" --minimized");
                else
                    k.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            catch { }
        }
    }
}
