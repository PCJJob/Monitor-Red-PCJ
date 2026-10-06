using System;
using System.Collections.Generic;
using System.Net;
using System.Runtime.InteropServices;

namespace MonitorRedPCJ.Native
{
    // P/Invoke a iphlpapi: conexiones por PID, contadores de interfaz, ARP y adaptadores
    public static class NativeNet
    {
        const int AF_INET = 2;
        const int TCP_TABLE_OWNER_PID_ALL = 5;
        const int UDP_TABLE_OWNER_PID = 2;

        [DllImport("iphlpapi.dll", SetLastError = true)]
        static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int dwOutBufLen, bool sort,
            int ipVersion, int tblClass, int reserved);

        [DllImport("iphlpapi.dll", SetLastError = true)]
        static extern uint GetExtendedUdpTable(IntPtr pUdpTable, ref int dwOutBufLen, bool sort,
            int ipVersion, int tblClass, int reserved);

        [DllImport("iphlpapi.dll", SetLastError = true)]
        static extern uint SendARP(uint destIp, uint srcIp, byte[] pMacAddr, ref uint phyAddrLen);

        [StructLayout(LayoutKind.Sequential)]
        struct MIB_TCPROW_OWNER_PID
        {
            public uint state;
            public uint localAddr;
            public uint localPort;
            public uint remoteAddr;
            public uint remotePort;
            public uint owningPid;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MIB_UDPROW_OWNER_PID
        {
            public uint localAddr;
            public uint localPort;
            public uint owningPid;
        }

        public static List<Models.ConnectionInfo> GetConnections()
        {
            var list = new List<Models.ConnectionInfo>();
            list.AddRange(GetTcpConnections());
            list.AddRange(GetUdpConnections());
            return list;
        }

        public static List<Models.ConnectionInfo> GetTcpConnections()
        {
            var list = new List<Models.ConnectionInfo>();
            int size = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref size, true, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
            if (size <= 0) return list;
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedTcpTable(buf, ref size, true, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0) == 0)
                {
                    int count = Marshal.ReadInt32(buf);
                    int rowSize = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();
                    for (int i = 0; i < count; i++)
                    {
                        var row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(buf + 4 + i * rowSize);
                        list.Add(new Models.ConnectionInfo
                        {
                            IsTcp = true,
                            LocalAddress = Ipv4(row.localAddr),
                            LocalPort = ntohs(row.localPort),
                            RemoteAddress = Ipv4(row.remoteAddr),
                            RemotePort = ntohs(row.remotePort),
                            Pid = (int)row.owningPid,
                            State = (int)row.state,
                        });
                    }
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
            return list;
        }

        public static List<Models.ConnectionInfo> GetUdpConnections()
        {
            var list = new List<Models.ConnectionInfo>();
            int size = 0;
            GetExtendedUdpTable(IntPtr.Zero, ref size, true, AF_INET, UDP_TABLE_OWNER_PID, 0);
            if (size <= 0) return list;
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedUdpTable(buf, ref size, true, AF_INET, UDP_TABLE_OWNER_PID, 0) == 0)
                {
                    int count = Marshal.ReadInt32(buf);
                    int rowSize = Marshal.SizeOf<MIB_UDPROW_OWNER_PID>();
                    for (int i = 0; i < count; i++)
                    {
                        var row = Marshal.PtrToStructure<MIB_UDPROW_OWNER_PID>(buf + 4 + i * rowSize);
                        list.Add(new Models.ConnectionInfo
                        {
                            IsTcp = false,
                            LocalAddress = Ipv4(row.localAddr),
                            LocalPort = ntohs(row.localPort),
                            RemoteAddress = "",
                            RemotePort = 0,
                            Pid = (int)row.owningPid,
                        });
                    }
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
            return list;
        }

        // Contadores agregados (32 bits, con wrap) de interfaces operativas que no son loopback
        // Suma los contadores de 64 bits de todas las interfaces activas (menos loopback).
        // Se mide en la capa IPv4: es lo que entra y sale por la red, sin contar broadcast interno.
        public static (long rx, long tx) GetInterfaceOctets()
        {
            long rx = 0, tx = 0;
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                try
                {
                    var s = ni.GetIPv4Statistics();
                    rx += s.BytesReceived;
                    tx += s.BytesSent;
                }
                catch { /* interfaz desapareciendo durante el sondeo */ }
            }
            return (rx, tx);
        }

        public class AdapterInfo
        {
            public string Name = "";
            public string Ip = "";
            public string Mask = "";
            public string Gateway = "";
            public bool Up;
        }

        // Enumeración gestionada (sin punteros): nombre, estado e IP de cada interfaz
        public static List<AdapterInfo> GetAdapters()
        {
            var list = new List<AdapterInfo>();
            try
            {
                foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                    var info = new AdapterInfo
                    {
                        Name = ni.Name,
                        Up = ni.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up,
                    };
                    if (info.Up)
                    {
                        foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                        {
                            if (ua.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                                !IPAddress.IsLoopback(ua.Address))
                            {
                                info.Ip = ua.Address.ToString();
                                break;
                            }
                        }
                    }
                    list.Add(info);
                }
            }
            catch { }
            return list;
        }

        public static string? GetMacForIp(string ip)
        {
            byte[] mac = new byte[32];
            uint len = 32;
            try
            {
                if (SendARP(IpToUint(ip), 0, mac, ref len) == 0 && len >= 6)
                    return MacToString(mac, 6);
            }
            catch { }
            return null;
        }

        public static string MacToString(byte[] b, int len)
        {
            var parts = new string[len];
            for (int i = 0; i < len; i++) parts[i] = b[i].ToString("X2");
            return string.Join(":", parts);
        }

        public static uint IpToUint(string ip)
        {
            var bytes = IPAddress.Parse(ip).GetAddressBytes();
            return BitConverter.ToUInt32(bytes, 0);
        }

        static string Ipv4(uint v) => new IPAddress(BitConverter.GetBytes(v)).ToString();
        static int ntohs(uint v) => (int)(((v & 0xFF) << 8) | ((v >> 8) & 0xFF));
    }
}
