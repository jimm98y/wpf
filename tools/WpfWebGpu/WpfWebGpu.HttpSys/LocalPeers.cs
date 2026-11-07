// Who is at the other end of a connection from this machine: the account its process runs as. Windows authenticates a
// local caller (Negotiate, NTLM over the loopback) as its own account -- a service as NETWORK SERVICE, a user's program
// as the user -- and asks nothing of it; there is no Windows authentication here, but the machine knows the same
// thing: the owner of the connection's other socket. The http.sys service (root, so it may look at every process's)
// finds it -- Linux's /proc/net/tcp gives the socket's owner directly, macOS's lsof its process's -- and hands it to the
// server with the connection's other facts, as http.sys hands a server the token it authenticated.

using System.Diagnostics;
using System.Globalization;
using System.Net;

namespace WpfWebGpu.HttpSys;

internal static class LocalPeers
{
    /// <summary>The uid of the process whose socket is <paramref name="remote"/>:<paramref name="remotePort"/>,
    /// connected to <paramref name="localPort"/>; null for a caller that is not on this machine, or not found.</summary>
    public static uint? UidOf(IPAddress? remote, int remotePort, int localPort)
    {
        if (remote == null || !IPAddress.IsLoopback(remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote)) return null;
        try
        {
            if (OperatingSystem.IsLinux()) return FromProc("/proc/net/tcp", remotePort, localPort) ?? FromProc("/proc/net/tcp6", remotePort, localPort);
            if (OperatingSystem.IsMacOS()) return FromLsof(remotePort, localPort);
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
