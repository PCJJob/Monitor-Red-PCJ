using System;
using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MonitorRedPCJ.Native
{
    // Icono real de cada ejecutable, para que la lista de Protección se parezca a lo que el
    // usuario ve en Windows (el programa con su cara) en vez de un punto de color.
    //
    // Extraer el icono toca el disco, así que NUNCA se hace en el hilo de la interfaz: la
    // primera pasada pide la carga en segundo plano y la lista se vuelve a pintar cuando están
    // listos. El resultado se congela (Freeze) para poder usarlo desde el hilo de UI.
    public static class AppIcon
    {
        private static readonly ConcurrentDictionary<string, ImageSource?> Cache =
            new(StringComparer.OrdinalIgnoreCase);

        // ¿Ya se intentó cargar? (incluye los que no tenían icono: no se reintentan)
        public static bool Known(string exePath)
            => !string.IsNullOrEmpty(exePath) && Cache.ContainsKey(exePath);

        public static ImageSource? Get(string exePath)
        {
            if (string.IsNullOrEmpty(exePath)) return null;
            if (Cache.TryGetValue(exePath, out var cached)) return cached;
            var src = Load(exePath);
            Cache[exePath] = src;
            return src;
        }

        // Solo lo que ya está en memoria: es lo que usa la interfaz, para no tocar el disco
        // al reconstruir la lista cada segundo y medio.
        public static ImageSource? Cached(string exePath)
            => !string.IsNullOrEmpty(exePath) && Cache.TryGetValue(exePath, out var cached) ? cached : null;

        private static ImageSource? Load(string exePath)
        {
            try
            {
                if (!File.Exists(exePath)) return null;
                using var ico = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
                if (ico == null) return null;
                var bmp = Imaging.CreateBitmapSourceFromHIcon(
                    ico.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                bmp.Freeze();
                return bmp;
            }
            catch
            {
                // Muchos .exe legítimos no exponen icono asociado: se queda el glifo neutro.
                return null;
            }
        }
    }
}
