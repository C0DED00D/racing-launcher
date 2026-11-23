using System.Runtime.InteropServices;

namespace Launcher.Core.Native;

/// <summary>Which process listens on a local TCP port (IPv4).</summary>
public static class TcpListeners
{
    const int AF_INET = 2;
    const int TCP_TABLE_OWNER_PID_LISTENER = 3;

    [DllImport("iphlpapi.dll")]
    static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool sort, int af, int tableClass, uint reserved);

    /// <summary>Process ids listening on <paramref name="port"/> (any local address).</summary>
    public static HashSet<int> Owners(int port)
    {
        var owners = new HashSet<int>();
        int size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_LISTENER, 0);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var buf = Marshal.AllocHGlobal(size);
            try
            {
                uint r = GetExtendedTcpTable(buf, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_LISTENER, 0);
                if (r == 122) continue; // ERROR_INSUFFICIENT_BUFFER: the table grew, size now holds the new size
                if (r != 0) return owners;
                // MIB_TCPTABLE_OWNER_PID: DWORD count, then rows of 6 DWORDs:
                // state, local addr, local port (network byte order in the low 16 bits), remote addr, remote port, pid.
                int count = Marshal.ReadInt32(buf);
                for (int i = 0; i < count; i++)
                {
                    var row = buf + 4 + i * 24;
                    int raw = Marshal.ReadInt32(row + 8);
                    int localPort = ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);
                    if (localPort == port) owners.Add(Marshal.ReadInt32(row + 20));
                }
                return owners;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        return owners;
    }
}
