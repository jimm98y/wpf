// wpfrun sqlbrowser --instances <folder>: Windows' SQL Server Browser for the SQL Servers the installations bring off
// Windows (a container's: Apple's container on macOS, Podman on Linux). A client that names an instance
// ((local)\SQLEXPRESS, .\SQLEXPRESS) asks the browser on UDP 1434 which port the instance listens on -- SqlClient's
// SsrpClient -- and nothing answers there off Windows: this does, as [MC-SQLR] describes it.
//
//   CLNT_UCAST_INST (4) and an instance's name   that instance: ServerName;<host>;InstanceName;<name>;IsClustered;No;
//                                                Version;<version>;tcp;<port>;;  (none known: no answer, as the
//                                                browser gives none)
//   CLNT_BCAST_EX (2), CLNT_UCAST_EX (3)         every instance, one after another
//   CLNT_UCAST_DAC (15)                          no answer: the container's SQL Server takes no dedicated admin
//                                                connection from outside
//
// The instances are the folder's files, one per installation, each line "<instance> <port>" (the installer writes
// its file and the uninstall removes it); read at each request, so an installation added or removed is answered for
// at once. Every installation's daemon runs this; the one that has the port answers for all, the others wait for it.
// On the loopback addresses only: the instances' SQL Servers listen on no other.

using System.Net;
using System.Net.Sockets;
using System.Text;

namespace WpfWebGpu;

internal static class SqlBrowser
{
    private const int Port = 1434;
    private const byte CLNT_BCAST_EX = 2, CLNT_UCAST_EX = 3, CLNT_UCAST_INST = 4, SVR_RESP = 5;

    public static int Run(string[] args)
    {
        string? folder = null;
        for (int i = 0; i < args.Length; i++)
            if (args[i] == "--instances" && i + 1 < args.Length) folder = args[++i];
        if (folder == null) throw new UserError("usage: wpfrun sqlbrowser --instances <folder>");
        var listeners = new List<Task>
        {
            Task.Run(() => Serve(folder, IPAddress.Loopback)),
            Task.Run(() => Serve(folder, IPAddress.IPv6Loopback)),
        };
        Task.WaitAll(listeners.ToArray());
        return 0;
    }

    private static void Serve(string folder, IPAddress address)
    {
        while (true)
        {
            UdpClient udp;
            try { udp = new UdpClient(new IPEndPoint(address, Port)); }
            catch (SocketException e) when (e.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied)
            {
                Thread.Sleep(30_000);           // another installation's browser answers meanwhile
                continue;
            }
            catch (SocketException e) when (e.SocketErrorCode is SocketError.AddressNotAvailable or SocketError.AddressFamilyNotSupported)
            {
                return;                         // (no IPv6 loopback here)
            }
            Console.WriteLine($"sqlbrowser: answering on {address}:{Port} for the instances in {folder}");
            using (udp)
            {
                while (true)
                {
                    IPEndPoint from = new(address, 0);
                    byte[] request;
                    try { request = udp.Receive(ref from); }
                    catch (SocketException) { break; }
                    byte[]? answer = Answer(request, Instances(folder));
                    if (answer != null)
                        try { udp.Send(answer, answer.Length, from); } catch (SocketException) { }
                }
            }
        }
    }

    /// <summary>The answer to a request, or null where the browser gives none.</summary>
    internal static byte[]? Answer(byte[] request, IReadOnlyList<(string Name, int Port)> instances)
    {
        if (request.Length == 0) return null;
        IEnumerable<(string Name, int Port)> which;
        switch (request[0])
        {
            case CLNT_BCAST_EX or CLNT_UCAST_EX:
                which = instances;
                break;
            case CLNT_UCAST_INST:
            {
                int end = Array.IndexOf(request, (byte)0, 1);
                string name = Encoding.ASCII.GetString(request, 1, (end < 0 ? request.Length : end) - 1);
                which = instances.Where(i => i.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Take(1);
                break;
            }
            default:
                return null;
        }
        string host = Environment.MachineName.Split('.')[0].ToUpperInvariant();
        string text = string.Concat(which.Select(i => $"ServerName;{host};InstanceName;{i.Name.ToUpperInvariant()};IsClustered;No;Version;17.0.1000.7;tcp;{i.Port};;"));
        if (text.Length == 0) return null;
        byte[] body = Encoding.ASCII.GetBytes(text);
        var answer = new byte[3 + body.Length];
        answer[0] = SVR_RESP;
        answer[1] = (byte)body.Length;
        answer[2] = (byte)(body.Length >> 8);
        body.CopyTo(answer, 3);
        return answer;
    }

    /// <summary>The instances the folder's files name ("instance port" a line).</summary>
    internal static List<(string Name, int Port)> Instances(string folder)
    {
        var all = new List<(string, int)>();
        try
        {
            foreach (string file in Directory.EnumerateFiles(folder).Order(StringComparer.Ordinal))
                foreach (string line in File.ReadAllLines(file))
                {
                    string[] parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 2 && int.TryParse(parts[1], out int port) && port is > 0 and < 65536) all.Add((parts[0], port));
                }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return all;
    }
}
