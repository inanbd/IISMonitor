using System.ComponentModel;
using System.Runtime.InteropServices;
using IISMonitor.Collectors.Native;

namespace IISMonitor.Collectors.Database;

internal readonly record struct TcpConnection(int Pid, int State, int LocalPort, int RemotePort, bool RemoteIsLoopback = false);

/// <summary>Reads the system TCP tables (IPv4 and IPv6) with the owning process of each connection.</summary>
internal static class TcpTableReader
{
    // MIB_TCPROW_OWNER_PID: state, local addr, local port, remote addr, remote port, pid (6 DWORDs).
    private const int Ipv4RowSize = 24;

    // MIB_TCP6ROW_OWNER_PID: local addr[16], scope, port, remote addr[16], scope, port, state, pid.
    private const int Ipv6RowSize = 56;

    public static List<TcpConnection> ReadAll()
    {
        var result = new List<TcpConnection>(1024);
        Read(NativeMethods.AF_INET, result);
        Read(NativeMethods.AF_INET6, result);
        return result;
    }

    private static void Read(int family, List<TcpConnection> result)
    {
        var size = 0;
        var buffer = IntPtr.Zero;
        try
        {
            for (var attempt = 0; attempt < 5; attempt++)
            {
                var status = NativeMethods.GetExtendedTcpTable(buffer, ref size, false, family, NativeMethods.TCP_TABLE_OWNER_PID_ALL, 0);
                if (status == NativeMethods.NO_ERROR && buffer != IntPtr.Zero)
                    break;
                if (status != NativeMethods.ERROR_INSUFFICIENT_BUFFER && !(status == NativeMethods.NO_ERROR && buffer == IntPtr.Zero))
                    throw new Win32Exception((int)status, "GetExtendedTcpTable failed");

                if (buffer != IntPtr.Zero)
                    Marshal.FreeHGlobal(buffer);
                // The table can grow between calls; leave some headroom.
                size += 4096;
                buffer = Marshal.AllocHGlobal(size);
            }

            if (buffer == IntPtr.Zero)
                return;

            var count = Marshal.ReadInt32(buffer);
            var rowSize = family == NativeMethods.AF_INET ? Ipv4RowSize : Ipv6RowSize;
            for (var i = 0; i < count; i++)
            {
                var row = buffer + 4 + i * rowSize;
                if (family == NativeMethods.AF_INET)
                {
                    result.Add(new TcpConnection(
                        Pid: Marshal.ReadInt32(row, 20),
                        State: Marshal.ReadInt32(row, 0),
                        LocalPort: Port(Marshal.ReadInt32(row, 8)),
                        RemotePort: Port(Marshal.ReadInt32(row, 16)),
                        // Addresses are in network byte order: the first octet is the low byte.
                        RemoteIsLoopback: (Marshal.ReadInt32(row, 12) & 0xFF) == 127));
                }
                else
                {
                    result.Add(new TcpConnection(
                        Pid: Marshal.ReadInt32(row, 52),
                        State: Marshal.ReadInt32(row, 48),
                        LocalPort: Port(Marshal.ReadInt32(row, 20)),
                        RemotePort: Port(Marshal.ReadInt32(row, 44)),
                        RemoteIsLoopback: IsLoopbackV6(row + 24)));
                }
            }
        }
        finally
        {
            if (buffer != IntPtr.Zero)
                Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>::1, or an IPv4-mapped 127.x.x.x address.</summary>
    private static bool IsLoopbackV6(IntPtr address)
    {
        var bytes = new byte[16];
        Marshal.Copy(address, bytes, 0, 16);
        if (bytes.AsSpan(0, 15).IndexOfAnyExcept((byte)0) < 0 && bytes[15] == 1)
            return true;
        return bytes.AsSpan(0, 10).IndexOfAnyExcept((byte)0) < 0 && bytes[10] == 0xFF && bytes[11] == 0xFF && bytes[12] == 127;
    }

    /// <summary>Ports are stored in network byte order in the low 16 bits.</summary>
    private static int Port(int raw) => ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);
}
