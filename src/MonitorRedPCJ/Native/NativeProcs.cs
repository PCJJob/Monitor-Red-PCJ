using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace MonitorRedPCJ.Native
{
    // ============================================================
    // Quién engendró a quién. Process.GetProcesses() da los procesos
    // pero no su ascendencia, y sin ella no se puede heredar la
    // configuración de un programa a los procesos que abre (un
    // instalador que lanza su descargador, un navegador que abre un
    // proceso por pestaña). La instantánea de Toolhelp sí la da, y de
    // una sola pasada: por eso se lee aquí y no con WMI, que tardaría
    // segundos en cada repaso.
    // ============================================================
    public static class NativeProcs
    {
        private const uint TH32CS_SNAPPROCESS = 0x00000002;
        private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PROCESSENTRY32W
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool Process32First(IntPtr snapshot, ref PROCESSENTRY32W entry);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool Process32Next(IntPtr snapshot, ref PROCESSENTRY32W entry);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        // pid → pid del padre. Diccionario nuevo en cada llamada; si la instantánea falla
        // (pasa al apagar el equipo o con políticas restrictivas) sale vacío y el llamador
        // simplemente no hereda nada esa vuelta.
        public static Dictionary<int, int> ParentMap()
        {
            var mapa = new Dictionary<int, int>();
            IntPtr snap = INVALID_HANDLE_VALUE;
            try
            {
                snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
                if (snap == INVALID_HANDLE_VALUE) return mapa;

                var e = new PROCESSENTRY32W();
                e.dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>();
                if (!Process32First(snap, ref e)) return mapa;

                do
                {
                    int pid = (int)e.th32ProcessID;
                    // El 0 y el propio System (4) no tienen padre útil: se dejan fuera para que
                    // nadie herede de ellos.
                    if (pid > 0 && e.th32ParentProcessID > 0 && pid != (int)e.th32ParentProcessID)
                        mapa[pid] = (int)e.th32ParentProcessID;
                }
                while (Process32Next(snap, ref e));
            }
            catch { }
            finally
            {
                if (snap != INVALID_HANDLE_VALUE) { try { CloseHandle(snap); } catch { } }
            }
            return mapa;
        }
    }
}
