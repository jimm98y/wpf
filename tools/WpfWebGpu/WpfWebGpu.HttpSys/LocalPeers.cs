// Who is at the other end of a connection from this machine: the account its process runs as. Windows authenticates a
// local caller (Negotiate, NTLM over the loopback) as its own account -- a service as NETWORK SERVICE, a user's program
// as the user -- and asks nothing of it; there is no Windows authentication here, but the machine knows the same
// thing: the owner of the connection's other socket. The http.sys service (root, so it may look at every process's)
// finds it -- Linux's /proc/net/tcp gives the socket's owner directly; on macOS the kernel lists each process's sockets
// (libproc) -- and hands it to the server with the connection's other facts, as http.sys hands a server the token it
// authenticated.

using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;

namespace WpfWebGpu.HttpSys;

internal static partial class LocalPeers
{
    /// <summary>The uid of the process whose socket is <paramref name="remote"/>:<paramref name="remotePort"/>,
    /// connected to <paramref name="localPort"/>; null for a caller that is not on this machine, or not found.</summary>
    public static uint? UidOf(IPAddress? remote, int remotePort, int localPort)
    {
        if (remote == null || !IPAddress.IsLoopback(remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote)) return null;
        try
        {
            if (OperatingSystem.IsLinux()) return FromProc("/proc/net/tcp", remotePort, localPort) ?? FromProc("/proc/net/tcp6", remotePort, localPort);
            if (OperatingSystem.IsMacOS()) return FromKernel(remotePort, localPort, out bool listed) ?? (listed ? null : FromLsof(remotePort, localPort));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { }
        return null;
    }

    // sl local_address rem_address st tx_queue:rx_queue tr:tm->when retrnsmt uid ...; addresses as hex address:port
    private static uint? FromProc(string table, int remotePort, int localPort)
    {
        if (!File.Exists(table)) return null;
        foreach (string line in File.ReadLines(table).Skip(1))
        {
            string[] f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (f.Length < 8) continue;
            if (Port(f[1]) == remotePort && Port(f[2]) == localPort && uint.TryParse(f[7], NumberStyles.None, CultureInfo.InvariantCulture, out uint uid))
                return uid;
        }
        return null;

        static int Port(string endpoint) =>
            endpoint.LastIndexOf(':') is int c && c > 0 && int.TryParse(endpoint[(c + 1)..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int p) ? p : -1;
    }

    // ---- macOS: the kernel's own list of each process's sockets --------------------------------------------------
    //
    // Asked once per new local connection. It used to be lsof, a process each time: with forty services starting and
    // connecting at once, fifty of them ran together, each slow because of the others, and a connection waited for
    // its caller's name longer than the caller waited for the connection. Now one look at every process's TCP sockets
    // serves every connection made before it; a connection newer than the last look takes a new one.

    private static readonly object s_gate = new();
    private static Dictionary<(int Local, int Foreign), uint>? s_sockets;
    private static long s_taken;

    /// <summary>The uid of the process with a TCP socket from <paramref name="remotePort"/> to
    /// <paramref name="localPort"/>; <paramref name="listed"/> false where the kernel would not list the sockets.</summary>
    internal static uint? FromKernel(int remotePort, int localPort, out bool listed)
    {
        listed = true;
        lock (s_gate)
        {
            // the caller's socket: its local port the connection's remote one, its peer our listening port
            if (s_sockets != null && Stopwatch.GetElapsedTime(s_taken).TotalMilliseconds < 100 && s_sockets.TryGetValue((remotePort, localPort), out uint known))
                return known;
            s_sockets = TcpSockets();
            s_taken = Stopwatch.GetTimestamp();
            listed = s_sockets != null;
            return s_sockets != null && s_sockets.TryGetValue((remotePort, localPort), out uint uid) ? uid : null;
        }
    }

    // sys/proc_info.h, as the SDK lays them out (64-bit): proc_fdinfo is { int32 fd; uint32 type }, a socket's
    // socket_fdinfo has its TCP endpoints' ports (network order, in an int each) at 264 (foreign) and 268 (local),
    // proc_bsdinfo the process's effective uid at 20
    private const int PROC_ALL_PIDS = 1, PROC_PIDLISTFDS = 1, PROC_PIDTBSDINFO = 3, PROC_PIDFDSOCKETINFO = 3, PROX_FDTYPE_SOCKET = 2;
    private const int SocketFdInfoSize = 792, SoiKind = 256, SockInfoTcp = 2, ForeignPort = 264, LocalPort = 268;
    private const int BsdInfoSize = 136, BsdUid = 20;

    private static unsafe Dictionary<(int, int), uint>? TcpSockets()
    {
        int count = proc_listpids(PROC_ALL_PIDS, 0, null, 0) / sizeof(int);
        if (count <= 0) return null;
        int[] pids = new int[count + 64];
        fixed (int* p = pids) count = proc_listpids(PROC_ALL_PIDS, 0, p, pids.Length * sizeof(int)) / sizeof(int);
        var sockets = new Dictionary<(int, int), uint>();
        byte* bsd = stackalloc byte[BsdInfoSize];
        byte* socket = stackalloc byte[SocketFdInfoSize];
        long[] fds = new long[256];
        for (int i = 0; i < count; i++)
        {
            int pid = pids[i];
            if (pid <= 0) continue;
            if (proc_pidinfo(pid, PROC_PIDTBSDINFO, 0, bsd, BsdInfoSize) != BsdInfoSize) continue;
            uint uid = *(uint*)(bsd + BsdUid);
            int bytes;
            while (true)
            {
                fixed (long* f = fds) bytes = proc_pidinfo(pid, PROC_PIDLISTFDS, 0, f, fds.Length * 8);
                if (bytes < fds.Length * 8) break;
                fds = new long[fds.Length * 2];
            }
            for (int k = 0; k < bytes / 8; k++)
            {
                long entry = fds[k];
                int fd = (int)(entry & 0xFFFFFFFF), type = (int)(entry >> 32);
                if (type != PROX_FDTYPE_SOCKET) continue;
                if (proc_pidfdinfo(pid, fd, PROC_PIDFDSOCKETINFO, socket, SocketFdInfoSize) != SocketFdInfoSize) continue;
                if (*(int*)(socket + SoiKind) != SockInfoTcp) continue;
                int local = BinaryPrimitives.ReverseEndianness((ushort)*(int*)(socket + LocalPort));
                int foreign = BinaryPrimitives.ReverseEndianness((ushort)*(int*)(socket + ForeignPort));
                if (local != 0 && foreign != 0) sockets[(local, foreign)] = uid;
            }
        }
        return sockets;
    }

    [LibraryImport("libproc")]
    private static unsafe partial int proc_listpids(int type, uint typeinfo, int* buffer, int size);

    [LibraryImport("libproc")]
    private static unsafe partial int proc_pidinfo(int pid, int flavor, ulong arg, void* buffer, int size);

    [LibraryImport("libproc")]
    private static unsafe partial int proc_pidfdinfo(int pid, int fd, int flavor, void* buffer, int size);

    // lsof -F: a process's p<pid> and u<uid>, then each of its matching sockets' n<local>-><remote>
    private static uint? FromLsof(int remotePort, int localPort)
    {
        var psi = new ProcessStartInfo("/usr/sbin/lsof") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in new[] { "-nP", $"-iTCP:{remotePort}", "-sTCP:ESTABLISHED", "-F", "pun" }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi);
        if (p == null) return null;
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(5000);
        uint? uid = null;
        foreach (string line in output.Split('\n'))
        {
            if (line.Length < 2) continue;
            if (line[0] == 'u') uid = uint.TryParse(line[1..], NumberStyles.None, CultureInfo.InvariantCulture, out uint u) ? u : null;
            else if (line[0] == 'n')
            {
                // this side of the connection: <address>:<remotePort>-><address>:<localPort>
                string[] ends = line[1..].Split("->");
                if (ends.Length == 2 && ends[0].EndsWith(":" + remotePort.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                    && ends[1].EndsWith(":" + localPort.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
                    return uid;
            }
        }
        return null;
    }
}
