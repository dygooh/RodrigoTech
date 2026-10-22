using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ResetExplode;

public static class ConnectionService
{
    private const int AfInet = 2;
    private const int AfInet6 = 23;
    private const int TcpTableOwnerPidConnections = 4;
    private const int ErrorInsufficientBuffer = 122;
    private const int MibTcpStateDeleteTcb = 12;

    public static HashSet<int> GetActivePids()
    {
        var pids = new HashSet<int>();
        CollectTcp(AfInet, pids);
        CollectTcp(AfInet6, pids);
        return pids;
    }

    public static HashSet<string> GetActiveExePaths()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pid in GetActivePids())
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                var path = process.MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(path))
                    paths.Add(path);
            }
            catch
            {
                // processo encerrado ou sem permissão
            }
        }
        return paths;
    }

    public static void DropTcpConnections(string exePath)
    {
        var pids = new HashSet<int>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var path = process.MainModule?.FileName;
                if (path is not null && path.Equals(exePath, StringComparison.OrdinalIgnoreCase))
                    pids.Add(process.Id);
            }
            catch
            {
                // ignore
            }
            finally
            {
                process.Dispose();
            }
        }

        if (pids.Count == 0) return;
        DropRows(AfInet, pids);
    }

    private static void CollectTcp(int family, HashSet<int> pids)
    {
        foreach (var row in ReadTable(family))
            pids.Add((int)row.OwningPid);
    }

    private static void DropRows(int family, HashSet<int> pids)
    {
        foreach (var row in ReadTable(family))
        {
            if (!pids.Contains((int)row.OwningPid)) continue;
            if (family != AfInet) continue;

            var entry = new MibTcpRow
            {
                State = MibTcpStateDeleteTcb,
                LocalAddr = row.LocalAddr,
                LocalPort = row.LocalPort,
                RemoteAddr = row.RemoteAddr,
                RemotePort = row.RemotePort
            };
            SetTcpEntry(ref entry);
        }
    }

    private static List<TcpRow> ReadTable(int family)
    {
        var result = new List<TcpRow>();
        int size = 0;
        var ret = GetExtendedTcpTable(IntPtr.Zero, ref size, true, family, TcpTableOwnerPidConnections, 0);
        if (ret != 0 && ret != ErrorInsufficientBuffer) return result;

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            ret = GetExtendedTcpTable(buffer, ref size, true, family, TcpTableOwnerPidConnections, 0);
            if (ret != 0) return result;

            int count = Marshal.ReadInt32(buffer);
            int offset = 4;
            int rowSize = family == AfInet ? 24 : 56;
            for (int i = 0; i < count; i++)
            {
                var rowPtr = buffer + offset + (i * rowSize);
                if (family == AfInet)
                {
                    result.Add(new TcpRow
                    {
                        LocalAddr = (uint)Marshal.ReadInt32(rowPtr, 4),
                        LocalPort = (uint)Marshal.ReadInt32(rowPtr, 8),
                        RemoteAddr = (uint)Marshal.ReadInt32(rowPtr, 12),
                        RemotePort = (uint)Marshal.ReadInt32(rowPtr, 16),
                        OwningPid = (uint)Marshal.ReadInt32(rowPtr, 20)
                    });
                }
                else
                {
                    result.Add(new TcpRow
                    {
                        OwningPid = (uint)Marshal.ReadInt32(rowPtr, 52)
                    });
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return result;
    }

    [DllImport("iphlpapi.dll")]
    private static extern int GetExtendedTcpTable(
        IntPtr pTcpTable,
        ref int dwOutBufLen,
        bool sort,
        int ipVersion,
        int tableClass,
        int reserved);

    [DllImport("iphlpapi.dll")]
    private static extern int SetTcpEntry(ref MibTcpRow pTcpRow);

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRow
    {
        public int State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
    }

    private readonly struct TcpRow
    {
        public uint LocalAddr { get; init; }
        public uint LocalPort { get; init; }
        public uint RemoteAddr { get; init; }
        public uint RemotePort { get; init; }
        public uint OwningPid { get; init; }
    }
}
