// http.sys's shared ports. On Windows a port belongs to http.sys, not to a process: every process registers its URL
// prefixes there (http://+:80/one/, http://+:80/two/), and the kernel hands each request to the process whose prefix
// claims it. Here each process serves its own pipeline on a loopback port of its own and registers its prefixes
// (with that port) in a folder every process of the machine shares; on each port a prefix names, one of them --
// whichever binds it first -- is the port's front: it takes the connections (TLS ended there, as http.sys ends it
// in the kernel) and forwards each request, WebSockets too, to the process whose prefix claims it, by http.sys's
// precedence. The others try the port every few seconds, so when the front's process ends another takes over, as
// the port outlives any one process under http.sys. A request no prefix claims is answered 404; one whose process is
// gone, 503 -- http.sys's answers.
//
// The forwarded request carries what http.sys would have told the process (the scheme, the addresses and ports of
// the connection) in X-WpfWebGpu-HttpSys, which the process's routing (PrefixServer.Route) takes for its own.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Yarp.ReverseProxy.Forwarder;

namespace Microsoft.AspNetCore.Server.HttpSys
{
    internal sealed class HttpSysPort : IAsyncDisposable
    {
        internal const string ConnectionHeader = "X-WpfWebGpu-HttpSys";
        private static readonly TimeSpan TakeOver = TimeSpan.FromSeconds(3);

        private readonly UrlPrefix[] _prefixes;
        private readonly Func<string?, int, X509Certificate2> _certificate;
        private readonly ILogger _logger;
        private readonly string _registration;
        private readonly Dictionary<int, WebApplication> _fronts = new();
        private readonly Timer _timer;
        private bool _disposed;

        private HttpSysPort(UrlPrefix[] prefixes, int localPort, Func<string?, int, X509Certificate2> certificate, ILogger logger)
        {
            _prefixes = prefixes;
            _certificate = certificate;
            _logger = logger;
            Directory.CreateDirectory(Folder);
            try { File.SetUnixFileMode(Folder, (UnixFileMode)Convert.ToInt32("1777", 8)); } catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { }
            _registration = Path.Combine(Folder, $"{Environment.ProcessId}-{localPort}.prefixes");
            File.WriteAllLines(_registration, new[] { localPort.ToString(CultureInfo.InvariantCulture) }.Concat(prefixes.Select(p => p.FullPrefix)));
            try { File.SetUnixFileMode(_registration, (UnixFileMode)Convert.ToInt32("644", 8)); } catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { }
            // the ports taken now where they are free, so the prefixes answer once the server has started, as
            // http.sys's do; then tried again, to take over a port whose front has gone
            TryFronts();
            _timer = new Timer(_ => TryFronts(), null, TakeOver, TakeOver);
        }

        /// <summary>The folder the machine's processes register their prefixes in.</summary>
        internal static string Folder => Environment.GetEnvironmentVariable("WPFWEBGPU_HTTPSYS_DIR") is { Length: > 0 } d ? d
            : OperatingSystem.IsWindows() ? Path.Combine(Path.GetTempPath(), "wpfwebgpu-httpsys") : "/tmp/wpfwebgpu-httpsys";

        /// <summary>The prefixes registered, served by this process on <paramref name="localPort"/>; the ports fronted
        /// from now on wherever this process can.</summary>
        public static HttpSysPort Register(UrlPrefix[] prefixes, int localPort, Func<string?, int, X509Certificate2> certificate, ILogger logger)
            => new(prefixes, localPort, certificate, logger);

        // ---- the fronts -------------------------------------------------------------------------------------

        private void TryFronts()
        {
            foreach (var port in _prefixes.GroupBy(p => p.PortValue))
            {
                lock (_fronts)
                {
                    if (_disposed || _fronts.ContainsKey(port.Key) || !Free(port.Key)) continue;
                    bool https = port.First().IsHttps;
                    try
                    {
                        var front = Front(port.Key, https);
                        front.StartAsync().GetAwaiter().GetResult();
                        _fronts[port.Key] = front;
                        _logger.LogInformation("port {Port} is served here for every process's prefixes on it", port.Key);
                    }
                    catch (IOException) { }                     // another process bound it first
                }
            }
        }

        // whether nothing listens on the port (every address)
        private static bool Free(int port)
        {
            try
            {
                using var probe = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp) { DualMode = true };
                probe.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
                return true;
            }
            catch (SocketException) { return false; }
        }

        private WebApplication Front(int port, bool https)
        {
            var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
            builder.WebHost.UseKestrelCore();
            if (https) builder.WebHost.UseKestrelHttpsConfiguration();
            builder.WebHost.ConfigureKestrel(k =>
            {
                k.Limits.MaxRequestBodySize = null;
                k.Limits.MinRequestBodyDataRate = null;
                k.Limits.MinResponseDataRate = null;
                k.ListenAnyIP(port, listen =>
                {
                    if (https) listen.UseHttps(new HttpsConnectionAdapterOptions { ServerCertificateSelector = (_, name) => _certificate(name, port) });
                });
            });
            builder.Services.AddHttpForwarder();
            var app = builder.Build();
            var forwarder = app.Services.GetRequiredService<IHttpForwarder>();
            app.Run(context => Forward(context, forwarder));
            return app;
        }

        // ---- a request on a fronted port ----------------------------------------------------------------------

        private static readonly ConcurrentDictionary<int, HttpMessageInvoker> s_clients = new();
        private static readonly ForwarderRequestConfig s_config = new() { ActivityTimeout = TimeSpan.FromDays(1) };

        private static async Task Forward(HttpContext context, IHttpForwarder forwarder)
        {
            var request = context.Request;
            int port = context.Connection.LocalPort;
            string path = (request.PathBase + request.Path).Value is { Length: > 0 } p ? p : "/";
            var owner = Registered().SelectMany(r => r.Prefixes.Select(x => (Prefix: x, r.LocalPort))).ToList();
            var match = PrefixServer.Best(owner.Select(o => o.Prefix), request.IsHttps, request.Host.Host, context.Connection.LocalIpAddress, port, path);
            if (match == null)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            int target = owner.First(o => ReferenceEquals(o.Prefix, match)).LocalPort;
            var client = s_clients.GetOrAdd(target, static _ => new HttpMessageInvoker(new SocketsHttpHandler
            {
                UseProxy = false, AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None, UseCookies = false,
                ActivityHeadersPropagator = null, ConnectTimeout = TimeSpan.FromSeconds(10),
            }));
            var c = context.Connection;
            // an IPv4 peer as http.sys names it (127.0.0.1), not as the dual-mode socket does (::ffff:127.0.0.1)
            static IPAddress? Plain(IPAddress? a) => a is { IsIPv4MappedToIPv6: true } ? a.MapToIPv4() : a;
            string facts = string.Join(';', request.Scheme, Plain(c.LocalIpAddress), c.LocalPort.ToString(CultureInfo.InvariantCulture),
                Plain(c.RemoteIpAddress), c.RemotePort.ToString(CultureInfo.InvariantCulture));
            var error = await forwarder.SendAsync(context, $"http://127.0.0.1:{target}", client, s_config, new Facts(facts));
            if (error != ForwarderError.None && !context.Response.HasStarted)
                context.Response.StatusCode = error == ForwarderError.Request ? StatusCodes.Status400BadRequest : StatusCodes.Status503ServiceUnavailable;
        }

        /// <summary>The request as it came, with what http.sys would have told the process about its connection.</summary>
        private sealed class Facts : HttpTransformer
        {
            private readonly string _facts;
            public Facts(string facts) => _facts = facts;

            public override async ValueTask TransformRequestAsync(HttpContext context, HttpRequestMessage proxy, string destinationPrefix, CancellationToken cancellation)
            {
                await base.TransformRequestAsync(context, proxy, destinationPrefix, cancellation);
                proxy.Headers.Host = context.Request.Host.Value;
                proxy.Headers.Remove(ConnectionHeader);
                proxy.Headers.TryAddWithoutValidation(ConnectionHeader, _facts);
            }
        }

        // ---- the registrations ----------------------------------------------------------------------------------

        private sealed record Registration(int LocalPort, UrlPrefix[] Prefixes);

        private static (DateTime At, Registration[] Registrations) s_cache = (DateTime.MinValue, Array.Empty<Registration>());

        // every live process's prefixes, read again at most once a second
        private static Registration[] Registered()
        {
            var cache = s_cache;
            if (DateTime.UtcNow - cache.At < TimeSpan.FromSeconds(1)) return cache.Registrations;
            var list = new List<Registration>();
            try
            {
                foreach (string file in Directory.EnumerateFiles(Folder, "*.prefixes"))
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    if (!int.TryParse(name.Split('-')[0], out int pid) || !Alive(pid))
                    {
                        try { File.Delete(file); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                        continue;
                    }
                    try
                    {
                        string[] lines = File.ReadAllLines(file);
                        if (lines.Length < 2 || !int.TryParse(lines[0], out int local)) continue;
                        list.Add(new Registration(local, lines.Skip(1).Where(l => l.Length > 0).Select(UrlPrefix.Create).ToArray()));
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException or ArgumentException) { }
                }
            }
            catch (DirectoryNotFoundException) { }
            var registrations = list.ToArray();
            s_cache = (DateTime.UtcNow, registrations);
            return registrations;
        }

        private static bool Alive(int pid)
        {
            if (pid == Environment.ProcessId) return true;
            try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException) { return false; }
        }

        public async ValueTask DisposeAsync()
        {
            WebApplication[] fronts;
            lock (_fronts)
            {
                _disposed = true;
                fronts = _fronts.Values.ToArray();
                _fronts.Clear();
            }
            await _timer.DisposeAsync();
            try { File.Delete(_registration); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            foreach (var f in fronts)
            {
                try { await f.StopAsync(); await f.DisposeAsync(); }
                catch (Exception e) { _logger.LogDebug(e, "stopping a port's front"); }
            }
        }
    }
}
