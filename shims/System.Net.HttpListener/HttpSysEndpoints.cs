// HttpListener as an http.sys client, as it is on Windows. The managed implementation (Runtime/) binds a socket per
// port; here each public port's endpoint is a loopback port of this process's own (HttpEndPointManager), and the
// prefixes on it are registered with the machine's http.sys service (WpfWebGpu.HttpSys), which owns the public port --
// sharing it with every other process's prefixes, ending its TLS -- and forwards to this process what its prefixes
// claim. A prefix is registered only where this account may (a URL reservation, or root), else "Access is denied" as
// from http.sys; and each request takes on the connection the service took (its scheme, addresses and ports), as
// http.sys hands a request over.

using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using WpfWebGpu.HttpSys;

namespace System.Net
{
    internal static partial class HttpSysEndpoints
    {
        private sealed class Port
        {
            public readonly List<SharedPrefix> Prefixes = new();
            public IDisposable? Registration;
            public int Local;
        }

        private static readonly Dictionary<int, Port> s_ports = new();
        private static Timer? s_watch;

        private static SharedPrefix Shared(ListenerPrefix lp)
        {
            string path = lp.Path!.EndsWith('/') ? lp.Path : lp.Path + "/";
            return new SharedPrefix(lp.Secure, lp.Host!, lp.Port, path);
        }

        /// <summary>Throws as http.sys refuses a prefix this account has no reservation for.</summary>
        public static void Check(ListenerPrefix lp)
        {
            if (!UrlAcl.MayRegister(UrlAcl.CurrentUid(), Shared(lp), out string why))
            {
                Console.Error.WriteLine($"HttpListener: {why}");
                throw new HttpListenerException(5, "Access is denied.");
            }
        }

        public static void Added(ListenerPrefix lp, HttpEndPointListener epl)
        {
            lock (s_ports)
            {
                if (!s_ports.TryGetValue(lp.Port, out Port? port)) s_ports[lp.Port] = port = new Port();
                port.Local = epl.BoundPort;
                var shared = Shared(lp);
                if (!port.Prefixes.Contains(shared)) port.Prefixes.Add(shared);
                Register(port);
            }
            HttpSysClient.EnsureRunning(new[] { lp.Port }, m => Console.Error.WriteLine("HttpListener: " + m));
            Watch();
        }

        public static void Removed(ListenerPrefix lp, HttpEndPointListener epl)
        {
            lock (s_ports)
            {
                if (!s_ports.TryGetValue(lp.Port, out Port? port)) return;
                port.Prefixes.Remove(Shared(lp));
                if (port.Prefixes.Count > 0) Register(port);
                else Closed(lp.Port);
            }
        }

        public static void Closed(int publicPort)
        {
            lock (s_ports)
            {
                if (!s_ports.Remove(publicPort, out Port? port)) return;
                port.Registration?.Dispose();
                if (s_ports.Count == 0) { s_watch?.Dispose(); s_watch = null; }
            }
        }

        // the registration rewritten with the port's prefixes as they are now (its file is the same one)
        private static void Register(Port port) => port.Registration = HttpSysRegistry.Register(port.Local, port.Prefixes);

        // a service started on demand that has gone: started again, as long as anything is registered
        private static void Watch()
        {
            lock (s_ports)
            {
                s_watch ??= new Timer(_ =>
                {
                    int[] ports;
                    lock (s_ports) ports = new List<int>(s_ports.Keys).ToArray();
                    if (ports.Length > 0 && !HttpSysClient.IsRunning())
                        HttpSysClient.EnsureRunning(ports, m => Console.Error.WriteLine("HttpListener: " + m));
                }, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
            }
        }
    }

    internal static partial class HttpSysEndpoints
    {
        /// <summary>
        /// Windows authentication (Negotiate, NTLM) of a caller on this machine: the account its process runs as, which
        /// the machine's http.sys service learned from the connection (its other end's owner), as Windows authenticates
        /// a local caller -- a service as its account, a user's program as the user -- without asking anything of it.
        /// Null where the request is not Windows-authenticated so (a remote caller, Windows authentication not asked).
        /// </summary>
        public static System.Security.Principal.IPrincipal? LocalWindowsUser(AuthenticationSchemes schemes, HttpListenerRequest request)
        {
            if ((schemes & (AuthenticationSchemes.Negotiate | AuthenticationSchemes.Ntlm)) == 0 || request.LocalPeerUid is not uint uid) return null;
            string? user = WpfWebGpu.Services.WindowsAccounts.UserName(uid);
            if (user == null) return null;
            try { return new System.Security.Principal.WindowsPrincipal(new System.Security.Principal.WindowsIdentity(user + "@" + Environment.MachineName)); }
            catch (System.Security.SecurityException) { return null; }
        }
    }

    public sealed partial class HttpListenerRequest
    {
        private sealed record ConnectionFacts(bool Secure, IPEndPoint? Local, IPEndPoint? Remote, uint? PeerUid);

        /// <summary>The account of the process at the connection's other end, where it is on this machine.</summary>
        internal uint? LocalPeerUid => _facts?.PeerUid;

        private ConnectionFacts? _facts;

        // scheme;local address;local port;remote address;remote port[;local caller's uid], as the machine's http.sys
        // service forwards them
        private void TakeConnectionFacts()
        {
            string? header = _headers[HttpSysClient.ConnectionHeader];
            string? key = _headers[HttpSysClient.KeyHeader];
            _headers.Remove(HttpSysClient.ConnectionHeader);
            _headers.Remove(HttpSysClient.KeyHeader);
            // vouched for with this process's key: anything else at the loopback port could have written it
            if (HttpSysClient.VouchedFacts(header, key) is not { } f) return;
            static IPEndPoint? EndPoint(string address, string port) =>
                IPAddress.TryParse(address, out var a) && int.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out int p) ? new IPEndPoint(a, p) : null;
            uint? peer = f.Length > 5 && uint.TryParse(f[5], NumberStyles.None, CultureInfo.InvariantCulture, out uint u) ? u : null;
            _facts = new ConnectionFacts(f[0].Equals("https", StringComparison.OrdinalIgnoreCase), EndPoint(f[1], f[2]), EndPoint(f[3], f[4]), peer);
        }
    }
}
