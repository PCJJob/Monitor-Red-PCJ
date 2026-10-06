using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MonitorRedPCJ.Native
{
    // Consulta si otro proceso propio corre con el token elevado.
    // Sirve para detectar "la copia sin permisos sigue abierta" al relanzar como administrador.
    public static class Elevation
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct TOKEN_ELEVATION
        {
            public uint TokenIsElevated;
        }

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass,
            IntPtr tokenInformation, int tokenInformationLength, out int returnLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const int TokenElevation = 20;

        public static bool IsProcessElevated(Process process)
        {
            try
            {
                if (!OpenProcessToken(process.Handle, PROCESS_QUERY_LIMITED_INFORMATION, out var token))
                    return false;
                try
                {
                    int size = Marshal.SizeOf<TOKEN_ELEVATION>();
                    IntPtr data = Marshal.AllocHGlobal(size);
                    try
                    {
                        if (!GetTokenInformation(token, TokenElevation, data, size, out _)) return false;
                        return Marshal.PtrToStructure<TOKEN_ELEVATION>(data).TokenIsElevated != 0;
                    }
                    finally { Marshal.FreeHGlobal(data); }
                }
                finally { CloseHandle(token); }
            }
            catch { return false; }
        }
    }
}
