using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace IISMonitor.Collectors.Blocking;

/// <summary>
/// Drops TCP connections that already exist from a remote address (for example HTTP keep-alive connections held by
/// HTTP.sys) after it has been blocked, with the documented SetTcpEntry(MIB_TCP_STATE_DELETE_TCB). IPv4 only: there
/// is no documented way to do this for IPv6. Requires administrator rights.
/// </summary>
internal static class TcpConnectionDropper
{
    private const int AfInet = 2;
    private const int TcpTableOwnerPidConnections = 4;
    private const uint ErrorInsufficientBuffer = 122;
    private const uint MibTcpStateDeleteTcb = 12;
    private const int StateClosed = 1;
    private const int StateListen = 2;
    private const int StateTimeWait = 11;

    // MIB_TCPROW_OWNER_PID: state, localAddr, localPort, remoteAddr, remotePort, pid.
    private const int Ipv4RowSize = 24;

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRow
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
    }

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int pdwSize, [MarshalAs(UnmanagedType.Bool)] bool bOrder, int ulAf, int tableClass, uint reserved);

    [DllImport("iphlpapi.dll")]
    private static extern uint SetTcpEntry(ref MibTcpRow pTcpRow);

    /// <param name="localPorts">Only connections to these local ports (e.g. 80, 443); null for all.</param>
    /// <returns>Connections dropped, and connections found but not dropped (last error code for logging).</returns>
    public static (int Dropped, int Failed, uint LastError) Drop(IPAddress remote, IReadOnlySet<int>? localPorts = null)
    {
        if (remote.IsIPv4MappedToIPv6)
            remote = remote.MapToIPv4();
        var dropped = 0;
        var failed = 0;
        uint lastError = 0;

        if (remote.AddressFamily == AddressFamily.InterNetwork)
        {
            Span<byte> bytes = stackalloc byte[4];
            remote.TryWriteBytes(bytes, out _);
            var wanted = BitConverter.ToUInt32(bytes); // same in-memory layout as dwRemoteAddr
            ForEachRow(AfInet, Ipv4RowSize, row =>
            {
                // Listening, closed and TIME_WAIT rows have nothing to close.
                var state = Marshal.ReadInt32(row, 0);
                if (state is StateClosed or StateListen or StateTimeWait)
                    return;
                if ((uint)Marshal.ReadInt32(row, 12) != wanted || !PortWanted(Marshal.ReadInt32(row, 8), localPorts))
                    return;
                var entry = new MibTcpRow
                {
                    State = MibTcpStateDeleteTcb,
                    LocalAddr = (uint)Marshal.ReadInt32(row, 4),
                    // The upper 16 bits of the port fields may hold uninitialized data.
                    LocalPort = (uint)Marshal.ReadInt32(row, 8) & 0xFFFF,
                    RemoteAddr = (uint)Marshal.ReadInt32(row, 12),
                    RemotePort = (uint)Marshal.ReadInt32(row, 16) & 0xFFFF,
                };
                var status = SetTcpEntry(ref entry);
                if (status == 0) dropped++; else { failed++; lastError = status; }
            });
        }

        return (dropped, failed, lastError);
    }

    /// <summary>Ports are stored in network byte order in the low 16 bits.</summary>
    private static bool PortWanted(int raw, IReadOnlySet<int>? ports) =>
        ports is null || ports.Contains(((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF));

    private static void ForEachRow(int family, int rowSize, Action<IntPtr> visit)
    {
        var size = 0;
        var buffer = IntPtr.Zero;
        try
        {
            var status = ErrorInsufficientBuffer;
            for (var attempt = 0; attempt < 5; attempt++)
            {
                status = GetExtendedTcpTable(buffer, ref size, false, family, TcpTableOwnerPidConnections, 0);
                if (status != ErrorInsufficientBuffer)
                    break;
                if (buffer != IntPtr.Zero)
                    Marshal.FreeHGlobal(buffer);
                size += 4096; // the table can grow between calls
                buffer = Marshal.AllocHGlobal(size);
            }

            if (status != 0 || buffer == IntPtr.Zero)
                return;
            var count = Marshal.ReadInt32(buffer);
            for (var i = 0; i < count; i++)
                visit(buffer + 4 + i * rowSize);
        }
        finally
        {
            if (buffer != IntPtr.Zero)
                Marshal.FreeHGlobal(buffer);
        }
    }
}
