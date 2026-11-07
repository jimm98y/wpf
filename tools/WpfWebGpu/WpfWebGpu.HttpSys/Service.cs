// The machine's http.sys: the process that owns the ports the registered prefixes name, as http.sys owns them on
// Windows, apart from every app. It listens on each port some server has a prefix on (TLS ended here, as http.sys
// ends it in the kernel) and forwards each request, WebSockets too, to the server whose prefix claims it, on that
// server's loopback port -- so an app that stops, restarts or crashes takes only its own requests with it. A request
// no prefix claims is answered 404; one whose server is gone, 503. The forwarded request carries what http.sys would
// have told the server about its connection (the scheme, the addresses and ports) in X-WpfWebGpu-HttpSys.
//
// One runs per machine (a lock in the registry's folder): the installed service (`wpfrun httpsys`, a LaunchDaemon or
// a systemd unit), which waits to take over while another holds it; or, where none is installed, one an app starts
// when it registers (`wpfrun httpsys --on-demand`), which leaves once no server has been registered for a while.

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Yarp.ReverseProxy.Forwarder;

namespace WpfWebGpu.HttpSys;

public static partial class HttpSysService
{
    public const string ConnectionHeader = HttpSysClient.ConnectionHeader;
    private static readonly TimeSpan OnDemandIdle = TimeSpan.FromSeconds(15), PortIdle = TimeSpan.FromSeconds(10);

    private static volatile List<HttpSysRegistry.Server> s_servers = new();
    private static DateTime s_read;

    // ---- the service ----------------------------------------------------------------------------------------

    /// <summary>Serves the machine's registered prefixes until <paramref name="stop"/>; on demand, until no server
    /// has been registered for a while (and not at all when another instance already serves them).</summary>
    public static async Task<int> Run(bool onDemand, ILogger log, CancellationToken stop)
    {
        // on demand: out of the starting app's process group, so what stops the app does not stop the ports
        if (onDemand && !OperatingSystem.IsWindows()) setsid();
        string folder = HttpSysRegistry.EnsureFolder();
        FileStream? held = null;
        while (held == null)
        {
            held = HttpSysClient.TryLock(folder);
            if (held != null) break;
            if (onDemand) return 0;                       // another serves the ports
            try { await Task.Delay(2000, stop); } catch (OperationCanceledException) { return 0; }
        }
        Own(folder);
        Log = log;
        log.LogInformation("serving the registered prefixes ({Folder})", folder);
        var certificates = new Certificates(log);
        var fronts = new Dictionary<int, (WebApplication App, DateTime Wanted)>();
        var refused = new HashSet<int>();
        DateTime lastServer = DateTime.UtcNow;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var servers = Refresh(force: true);
                var now = DateTime.UtcNow;
                if (servers.Count > 0) lastServer = now;
                foreach (var port in servers.SelectMany(s => s.Prefixes).GroupBy(p => p.Port))
                {
                    if (fronts.TryGetValue(port.Key, out var f)) { fronts[port.Key] = (f.App, now); continue; }
                    bool https = port.First().Https;
                    try
                    {
                        var app = Front(port.Key, https, certificates);
                        await app.StartAsync(stop);
                        fronts[port.Key] = (app, now);
                        refused.Remove(port.Key);
                        log.LogInformation("port {Port} ({Scheme}) served", port.Key, https ? "https" : "http");
                    }
                    catch (IOException e)
                    {
                        if (refused.Add(port.Key)) log.LogWarning("port {Port} could not be listened on: {Reason}", port.Key, e.Message);
                    }
                }
                // a port no prefix names any more: let go, as http.sys does
                foreach (var (port, f) in fronts.Where(x => now - x.Value.Wanted > PortIdle).ToList())
                {
                    fronts.Remove(port);
                    await Stop(f.App);
                    log.LogInformation("port {Port} released", port);
                }
                if (onDemand && now - lastServer > OnDemandIdle) break;
                try { await Task.Delay(1000, stop); } catch (OperationCanceledException) { }
            }
        }
        finally
        {
            foreach (var f in fronts.Values) await Stop(f.App);
            held.Dispose();
        }
        return 0;
    }

    private static async Task Stop(WebApplication app)
    {
        try { await app.StopAsync(); await app.DisposeAsync(); } catch (Exception) { }
    }

    // the instance's process, beside its lock (what an administrator, or a test, stops)
    private static void Own(string folder)
    {
        string path = Path.Combine(folder, "service.pid");
        try
        {
            File.WriteAllText(path, Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            HttpSysRegistry.Mode(path, "644");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    [LibraryImport("libc")]
    private static partial int setsid();

    private static WebApplication Front(int port, bool https, Certificates certificates)
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
                if (https) listen.UseHttps(new HttpsConnectionAdapterOptions { ServerCertificateSelector = (_, name) => certificates.For(name, port) });
            });
        });
        builder.Services.AddHttpForwarder();
        var app = builder.Build();
        var forwarder = app.Services.GetRequiredService<IHttpForwarder>();
        app.Run(context => Forward(context, forwarder));
        return app;
    }

    // ---- a request ------------------------------------------------------------------------------------------

    private static readonly HttpMessageInvoker s_client = new(new SocketsHttpHandler
    {
        UseProxy = false, AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None, UseCookies = false,
        ActivityHeadersPropagator = null, ConnectTimeout = TimeSpan.FromSeconds(10),
    });
    private static readonly ForwarderRequestConfig s_config = new() { ActivityTimeout = TimeSpan.FromDays(1) };

    private static List<HttpSysRegistry.Server> Refresh(bool force)
    {
        if (force || DateTime.UtcNow - s_read > TimeSpan.FromSeconds(1))
        {
            s_servers = HttpSysRegistry.Read().Select(Permitted).Where(s => s.Prefixes.Length > 0).ToList();
            s_read = DateTime.UtcNow;
        }
        return s_servers;
    }

    private static readonly HashSet<string> s_refusedOnce = new();
    internal static ILogger? Log;

    // a registration's prefixes its account may register (URL reservations); the others refused, as http.sys
    // refuses them, whoever wrote the file
    private static HttpSysRegistry.Server Permitted(HttpSysRegistry.Server server)
    {
        var allowed = new List<SharedPrefix>();
        foreach (var p in server.Prefixes)
        {
            if (UrlAcl.MayRegister(server.Owner, p, out string why)) { allowed.Add(p); continue; }
            lock (s_refusedOnce)
                if (s_refusedOnce.Add($"{server.ProcessId}:{p.FullPrefix}")) Log?.LogWarning("process {Pid}: {Prefix} refused: {Why}", server.ProcessId, p.FullPrefix, why);
        }
        return allowed.Count == server.Prefixes.Length ? server : server with { Prefixes = allowed.ToArray() };
    }

    private static async Task Forward(HttpContext context, IHttpForwarder forwarder)
    {
        var request = context.Request;
        var c = context.Connection;
        string path = (request.PathBase + request.Path).Value is { Length: > 0 } p ? p : "/";
        (int Port, SharedPrefix Prefix)? Claim(List<HttpSysRegistry.Server> servers) =>
            HttpSysRouting.Best(servers.SelectMany(s => s.Prefixes.Select(x => ((int Port, SharedPrefix Prefix)?)(s.LocalPort, x))),
                x => x!.Value.Prefix, request.IsHttps, request.Host.Host, c.LocalIpAddress, c.LocalPort, path);
        // a server registered since the last look is looked for before the request is refused
        var claim = Claim(Refresh(force: false)) ?? Claim(Refresh(force: true));
        if (claim == null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        // an IPv4 peer as http.sys names it (127.0.0.1), not as the dual-mode socket does (::ffff:127.0.0.1)
        static IPAddress? Plain(IPAddress? a) => a is { IsIPv4MappedToIPv6: true } ? a.MapToIPv4() : a;
        string facts = string.Join(';', request.Scheme, Plain(c.LocalIpAddress), c.LocalPort.ToString(CultureInfo.InvariantCulture),
            Plain(c.RemoteIpAddress), c.RemotePort.ToString(CultureInfo.InvariantCulture));
        var error = await forwarder.SendAsync(context, $"http://127.0.0.1:{claim.Value.Port}", s_client, s_config, new Facts(facts));
        if (error == ForwarderError.None || context.Response.HasStarted) return;
        // its server unreachable: what the registry says now -- gone, it is answered as no prefix claimed it; its
        // prefix another's since, forwarded there; still registered, the server's queue is unavailable
        var now = Claim(Refresh(force: true));
        if (now == null) context.Response.StatusCode = StatusCodes.Status404NotFound;
        else if (now.Value.Port != claim.Value.Port && error == ForwarderError.Request)
            await forwarder.SendAsync(context, $"http://127.0.0.1:{now.Value.Port}", s_client, s_config, new Facts(facts));
        else context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
    }

    /// <summary>The request as it came, with what http.sys would have told the server about its connection.</summary>
    private sealed class Facts(string facts) : HttpTransformer
    {
        public override async ValueTask TransformRequestAsync(HttpContext context, HttpRequestMessage proxy, string destinationPrefix, CancellationToken cancellation)
        {
            await base.TransformRequestAsync(context, proxy, destinationPrefix, cancellation);
            proxy.Headers.Host = context.Request.Host.Value;
            proxy.Headers.Remove(ConnectionHeader);
            proxy.Headers.TryAddWithoutValidation(ConnectionHeader, facts);
        }
    }
}
