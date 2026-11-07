// wpfrun iis: the machine's web server, as IIS serves an installation's web applications -- its configuration taken
// from the installation's machine (iis.json in the machine's state: the sites, their bindings, applications and virtual
// directories, the application pools). It is IIS's two system parts in one daemon: the web publishing service, which
// claims each application's URLs from http.sys (the sites' bindings and the application's path, registered with the
// machine's http.sys service, so they share their ports with every other process's prefixes, as on Windows), and the
// process activation, which starts an application's worker under its pool's account.
//
// An application whose web.config hands its requests to the ASP.NET Core Module (handler aspNetCore) is run as the
// module runs one out of process: the program its <aspNetCore> element names (dotnet and the app's .dll, or its own
// executable), through the stack's runner, with ASPNETCORE_PORT, ASPNETCORE_APPL_PATH (the application's path, its
// requests' base) and ASPNETCORE_TOKEN, which the app's IIS integration (UseIISIntegration, in every default host
// builder) takes up -- Kestrel on that port, the path base, the pairing token it requires of every request -- and each
// request is forwarded to it with the token, the client's address and scheme (X-Forwarded-For, -Proto), as the module
// forwards it. A pool that starts with the server (autoStart) starts its workers with it; others on their first
// request; a worker that has ended is started again by the next. Any other application's files are served as IIS's
// static file handler serves them, its default documents for a folder.

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Xml.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;
using WpfWebGpu.HttpSys;
using WpfWebGpu.Services;
using Yarp.ReverseProxy.Forwarder;

namespace WpfWebGpu;

internal sealed class IisHost
{
    private sealed record Module(string Program, List<string> Args, Dictionary<string, string> Environment, int StartupSeconds);

    private sealed class Application
    {
        public required IisSite Site { get; init; }
        public required IisApplication Config { get; init; }
        public required IisPool Pool { get; init; }
        public string? Root { get; init; }
        public Module? Module { get; init; }
        public Worker? Worker { get; set; }
        public string Path => Config.Path.TrimEnd('/');
    }

    private sealed class Worker
    {
        public Process? Process;
        public int Port;
        public string Token = Guid.NewGuid().ToString("N");
        public readonly SemaphoreSlim Gate = new(1, 1);
    }

    private static readonly string[] s_defaultDocuments = { "Default.htm", "Default.asp", "index.htm", "index.html", "iisstart.htm", "default.aspx" };
    private static readonly FileExtensionContentTypeProvider s_types = new();
    private static readonly HttpMessageInvoker s_client = new(new SocketsHttpHandler
    {
        UseProxy = false, AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None, UseCookies = false,
        ActivityHeadersPropagator = null, ConnectTimeout = TimeSpan.FromSeconds(10),
    });
    private static readonly ForwarderRequestConfig s_config = new() { ActivityTimeout = TimeSpan.FromDays(1) };

    private readonly List<Application> _apps = new();
    private readonly Action<string> _log;

    private IisHost(IisConfiguration config, Action<string> log)
    {
        _log = log;
        foreach (var site in config.Sites)
            foreach (var app in site.Applications)
            {
                if (!app.VirtualDirectories.Any(v => v.PhysicalPath != null)) continue;    // nothing of the installation's to serve
                string? root = app.VirtualDirectories.FirstOrDefault(v => v.Path == "/")?.PhysicalPath;
                var pool = config.Pools.FirstOrDefault(p => p.Name.Equals(app.Pool, StringComparison.OrdinalIgnoreCase)) ?? new IisPool { Name = app.Pool };
                _apps.Add(new Application { Site = site, Config = app, Pool = pool, Root = root, Module = root != null ? ModuleOf(root) : null });
            }
    }

    public static async Task<int> Run(string configPath, Action<string> log, CancellationToken stop)
    {
        var host = new IisHost(IisConfiguration.Load(configPath), log);
        if (host._apps.Count == 0)
        {
            log($"iis: no application of the installation to serve ({configPath})");
            try { await Task.Delay(Timeout.Infinite, stop); } catch (OperationCanceledException) { }
            return 0;
        }
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.WebHost.UseKestrelCore();
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.Limits.MaxRequestBodySize = null;
            k.Limits.MinRequestBodyDataRate = null;
            k.Limits.MinResponseDataRate = null;
            k.Listen(IPAddress.Loopback, 0);
        });
        builder.Services.AddHttpForwarder();
        var web = builder.Build();
        var forwarder = web.Services.GetRequiredService<IHttpForwarder>();
        web.Run(context => host.Serve(context, forwarder));
        await web.StartAsync(stop);
        int local = new Uri(web.Urls.First()).Port;

        // the applications' URLs, claimed from http.sys: each binding's scheme, host and port, and the application's path
        var prefixes = new List<SharedPrefix>();
        foreach (var app in host._apps)
            foreach (var b in app.Site.Bindings)
            {
                var prefix = new SharedPrefix(b.Protocol == "https", b.Host.Length > 0 ? b.Host : "*", b.Port, app.Path + "/");
                prefixes.Add(prefix);
                log($"iis: {prefix.FullPrefix} -> {app.Root ?? "(its virtual directories)"}{(app.Module != null ? " (ASP.NET Core, pool " + app.Pool.Name + ")" : "")}");
            }
        using var registration = HttpSysRegistry.Register(local, prefixes);
        HttpSysClient.EnsureRunning(prefixes.Select(p => p.Port), m => log("iis: " + m));

        // the pools that start with the server
        foreach (var app in host._apps.Where(a => a.Module != null && a.Pool.AutoStart))
            _ = Task.Run(() => host.EnsureWorker(app, stop));

        try { await Task.Delay(Timeout.Infinite, stop); } catch (OperationCanceledException) { }
        foreach (var app in host._apps)
            try { if (app.Worker?.Process is { HasExited: false } p) p.Kill(entireProcessTree: true); } catch (Exception) { }
        await web.StopAsync();
        return 0;
    }

    // ---- requests ---------------------------------------------------------------------------------------------

    private async Task Serve(HttpContext context, IHttpForwarder forwarder)
    {
        var request = context.Request;
        // the connection http.sys took, as it hands a request over (scheme;local address;local port;remote address;remote port)
        string[] facts = HttpSysClient.VouchedFacts(request.Headers[HttpSysClient.ConnectionHeader].ToString(), request.Headers[HttpSysClient.KeyHeader].ToString()) ?? Array.Empty<string>();
        request.Headers.Remove(HttpSysClient.ConnectionHeader);
        request.Headers.Remove(HttpSysClient.KeyHeader);
        string scheme = facts.Length >= 5 ? facts[0] : request.Scheme;
        int port = facts.Length >= 5 && int.TryParse(facts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int p) ? p : request.Host.Port ?? 80;
        string remote = facts.Length >= 5 ? facts[3] : context.Connection.RemoteIpAddress?.ToString() ?? "";
        string path = (request.PathBase + request.Path).Value is { Length: > 0 } v ? v : "/";

        var app = _apps
            .Where(a => a.Site.Bindings.Any(b => b.Port == port && (b.Host.Length == 0 || b.Host.Equals(request.Host.Host, StringComparison.OrdinalIgnoreCase))))
            .Where(a => Under(path, a.Path))
            .OrderByDescending(a => a.Path.Length).FirstOrDefault();
        if (app == null) { context.Response.StatusCode = StatusCodes.Status404NotFound; return; }

        if (app.Module != null)
        {
            var worker = await EnsureWorker(app, context.RequestAborted);
            if (worker == null) { context.Response.StatusCode = StatusCodes.Status502BadGateway; return; }
            var error = await forwarder.SendAsync(context, $"http://127.0.0.1:{worker.Port}", s_client, s_config, new ModuleRequest(worker.Token, scheme, remote));
            if (error != ForwarderError.None && !context.Response.HasStarted) context.Response.StatusCode = StatusCodes.Status502BadGateway;
            return;
        }
        await ServeFile(context, app, path);
    }

    private static bool Under(string path, string appPath) =>
        appPath.Length == 0 || path.Equals(appPath, StringComparison.OrdinalIgnoreCase)
        || (path.StartsWith(appPath, StringComparison.OrdinalIgnoreCase) && path[appPath.Length] == '/');

    // the static file handler and default documents: the virtual directory the path lies in, the file under its folder
    private static async Task ServeFile(HttpContext context, Application app, string path)
    {
        string inApp = path[app.Path.Length..];
        if (inApp.Length == 0) { context.Response.Redirect(path + "/", permanent: true); return; }
        var dir = app.Config.VirtualDirectories.Where(d => d.PhysicalPath != null)
            .Where(d => Under(inApp, d.Path.TrimEnd('/')))
            .OrderByDescending(d => d.Path.Length).FirstOrDefault();
        if (dir == null) { context.Response.StatusCode = StatusCodes.Status404NotFound; return; }
        string rest = Uri.UnescapeDataString(inApp[dir.Path.TrimEnd('/').Length..]).TrimStart('/');
        string full = System.IO.Path.GetFullPath(System.IO.Path.Combine(dir.PhysicalPath!, rest));
        string folder = System.IO.Path.GetFullPath(dir.PhysicalPath!);
        if (!full.StartsWith(folder, StringComparison.Ordinal)) { context.Response.StatusCode = StatusCodes.Status404NotFound; return; }
        if (Directory.Exists(full))
        {
            if (!path.EndsWith('/')) { context.Response.Redirect(path + "/" + context.Request.QueryString, permanent: true); return; }
            full = s_defaultDocuments.Select(d => System.IO.Path.Combine(full, d)).FirstOrDefault(File.Exists) ?? "";
        }
        if (!File.Exists(full)) { context.Response.StatusCode = StatusCodes.Status404NotFound; return; }
        context.Response.ContentType = s_types.TryGetContentType(full, out string? type) ? type : "application/octet-stream";
        context.Response.ContentLength = new FileInfo(full).Length;
        if (HttpMethods.IsHead(context.Request.Method)) return;
        await context.Response.SendFileAsync(full, context.RequestAborted);
    }

    // a request as the ASP.NET Core Module forwards it: the host it was sent to, the pairing token, the client
    private sealed class ModuleRequest(string token, string scheme, string remote) : HttpTransformer
    {
        public override async ValueTask TransformRequestAsync(HttpContext context, HttpRequestMessage proxy, string destinationPrefix, CancellationToken cancellation)
        {
            await base.TransformRequestAsync(context, proxy, destinationPrefix, cancellation);
            proxy.Headers.Host = context.Request.Host.Value;
            proxy.Headers.Remove("MS-ASPNETCORE-TOKEN");
            proxy.Headers.TryAddWithoutValidation("MS-ASPNETCORE-TOKEN", token);
            proxy.Headers.Remove("X-Forwarded-For");
            if (remote.Length > 0) proxy.Headers.TryAddWithoutValidation("X-Forwarded-For", remote);
            proxy.Headers.Remove("X-Forwarded-Proto");
            proxy.Headers.TryAddWithoutValidation("X-Forwarded-Proto", scheme);
        }
    }

    // ---- workers ----------------------------------------------------------------------------------------------

    // the application's <aspNetCore> element, where its web.config hands requests to the module
    private static Module? ModuleOf(string root)
    {
        string webConfig = System.IO.Path.Combine(root, "web.config");
        if (!File.Exists(webConfig)) return null;
        XElement? server;
        try { server = XDocument.Load(webConfig).Root?.Element("system.webServer") ?? XDocument.Load(webConfig).Root?.Element("location")?.Element("system.webServer"); }
        catch (Exception e) when (e is System.Xml.XmlException or IOException) { return null; }
        bool handled = server?.Element("handlers")?.Elements("add").Any(h => ((string?)h.Attribute("modules") ?? "").StartsWith("AspNetCoreModule", StringComparison.OrdinalIgnoreCase)) == true;
        var core = server?.Element("aspNetCore");
        if (!handled || core == null) return null;
        string processPath = (string?)core.Attribute("processPath") ?? "dotnet";
        var args = SplitArguments((string?)core.Attribute("arguments") ?? "");
        string Local(string p) => System.IO.Path.GetFullPath(System.IO.Path.Combine(root, p.Replace('\\', '/')));
        string program;
        if (System.IO.Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            // dotnet <app>.dll: the app, through the stack's runner
            if (args.Count == 0) return null;
            program = Local(args[0]);
            args.RemoveAt(0);
        }
        else program = Local(processPath);
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in core.Element("environmentVariables")?.Elements("environmentVariable") ?? Enumerable.Empty<XElement>())
            if ((string?)e.Attribute("name") is { Length: > 0 } name) env[name] = (string?)e.Attribute("value") ?? "";
        int startup = int.TryParse((string?)core.Attribute("startupTimeLimit"), out int s) ? s : 120;
        return new Module(program, args, env, startup);
    }

    private static List<string> SplitArguments(string text)
    {
        var args = new List<string>();
        var current = new System.Text.StringBuilder();
        bool quoted = false, any = false;
        foreach (char c in text)
        {
            if (c == '"') { quoted = !quoted; any = true; continue; }
            if (char.IsWhiteSpace(c) && !quoted) { if (any) { args.Add(current.ToString()); current.Clear(); any = false; } continue; }
            current.Append(c); any = true;
        }
        if (any) args.Add(current.ToString());
        return args;
    }

    private async Task<Worker?> EnsureWorker(Application app, CancellationToken cancel)
    {
        var worker = app.Worker ??= new Worker();
        await worker.Gate.WaitAsync(cancel);
        try
        {
            if (worker.Process is { HasExited: false } && Accepts(worker.Port)) return worker;
            if (worker.Process is { HasExited: false } running) { await WaitForPort(worker, running, app.Module!.StartupSeconds, cancel); return Accepts(worker.Port) ? worker : null; }
            worker.Port = FreePort();
            worker.Token = Guid.NewGuid().ToString("N");
            var start = StartInfo(app, worker);
            _log($"iis: starting {app.Path}/ ({app.Module!.Program}) on 127.0.0.1:{worker.Port} as {app.Pool.Identity}");
            worker.Process = Process.Start(start);
            if (worker.Process == null) return null;
            Directory.CreateDirectory(Locations.LogRoot);
            string logFile = System.IO.Path.Combine(Locations.LogRoot, $"iis-{Segment(app.Pool.Name)}-{Segment(app.Path)}.log");
            _ = Pump(worker.Process.StandardOutput, logFile);
            _ = Pump(worker.Process.StandardError, logFile);
            await WaitForPort(worker, worker.Process, app.Module.StartupSeconds, cancel);
            if (Accepts(worker.Port)) return worker;
            _log($"iis: {app.Path}/ did not start listening within {app.Module.StartupSeconds} s{(worker.Process.HasExited ? $" (it exited with {worker.Process.ExitCode})" : "")}");
            return null;
        }
        finally { worker.Gate.Release(); }
    }

    // the worker under its pool's account (sudo, as root may), with the module's environment
    private static ProcessStartInfo StartInfo(Application app, Worker worker)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
            if (e.Key is string k && (k.StartsWith("WPFRUN_", StringComparison.Ordinal) || k.StartsWith("WPFWEBGPU_", StringComparison.Ordinal) || k.StartsWith("DOTNET_", StringComparison.Ordinal)))
                env[k] = e.Value as string ?? "";
        foreach (var (k, v) in app.Module!.Environment) env[k] = v;
        env["ASPNETCORE_PORT"] = worker.Port.ToString(CultureInfo.InvariantCulture);
        env["ASPNETCORE_APPL_PATH"] = app.Path.Length > 0 ? app.Path : "/";
        env["ASPNETCORE_TOKEN"] = worker.Token;
        env["ASPNETCORE_IIS_HTTPAUTH"] = "anonymous;";
        env["ASPNETCORE_IIS_PHYSICAL_PATH"] = app.Root! + "/";
        env["ASPNETCORE_IIS_WEBSOCKETS_SUPPORTED"] = "true";
        // IIS's worker process, which an application can tell it is hosted by
        env["WPFRUN_PROCESS_NAME"] = "w3wp";

        string dotnet = Environment.ProcessPath!;
        string launcher = typeof(IisHost).Assembly.Location;
        var command = new List<string> { dotnet, launcher, "run", app.Module.Program };
        command.AddRange(app.Module.Args);

        string? account = PoolAccount(app.Pool.Identity);
        var psi = new ProcessStartInfo { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = app.Root! };
        if (account != null && WindowsAccounts.CurrentUid() == 0 && WindowsAccounts.Uid(account) != null && File.Exists("/usr/bin/sudo"))
        {
            psi.FileName = "/usr/bin/sudo";
            foreach (string a in new[] { "-n", "-u", account, "--", "/usr/bin/env" }) psi.ArgumentList.Add(a);
            foreach (var (k, v) in env) psi.ArgumentList.Add($"{k}={v}");
            foreach (string a in command) psi.ArgumentList.Add(a);
        }
        else
        {
            psi.FileName = command[0];
            foreach (string a in command.Skip(1)) psi.ArgumentList.Add(a);
            foreach (var (k, v) in env) psi.Environment[k] = v;
        }
        return psi;
    }

    // the system account a pool's identity runs as here: its own identity (ApplicationPoolIdentity) reaches the network
    // as the machine does, as NETWORK SERVICE does; LocalSystem is root
    private static string? PoolAccount(string identity)
    {
        bool mac = OperatingSystem.IsMacOS();
        return identity switch
        {
            "LocalSystem" => null,
            "LocalService" => mac ? "_wpflocalservice" : "wpf-localservice",
            _ => mac ? "_wpfnetworkservice" : "wpf-networkservice",
        };
    }

    private static async Task WaitForPort(Worker worker, Process process, int seconds, CancellationToken cancel)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(seconds);
        while (DateTime.UtcNow < until && !process.HasExited && !Accepts(worker.Port))
            await Task.Delay(200, cancel);
    }

    private static bool Accepts(int port)
    {
        if (port == 0) return false;
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            s.Connect(IPAddress.Loopback, port);
            return true;
        }
        catch (SocketException) { return false; }
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static async Task Pump(StreamReader from, string logFile)
    {
        try
        {
            string? line;
            while ((line = await from.ReadLineAsync()) != null)
                lock (s_logGate) File.AppendAllText(logFile, line + "\n");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ObjectDisposedException) { }
    }

    private static readonly object s_logGate = new();

    private static string Segment(string s)
    {
        var chars = s.Where(char.IsLetterOrDigit).ToArray();
        return chars.Length == 0 ? "root" : new string(chars).ToLowerInvariant();
    }
}

internal static class IisCommand
{
    public static int Run(string[] args)
    {
        string? config = IisConfiguration.DefaultPath();
        if (config == null)
        {
            Console.Error.WriteLine("wpfrun iis: no web server configuration (WPFRUN_STATE or WPFWEBGPU_IIS)");
            return 2;
        }
        if (!HostInfo.IsWindows && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WPFWEBGPU_REGISTRY_DIR")))
            Environment.SetEnvironmentVariable("WPFWEBGPU_REGISTRY_DIR", Path.Combine(Locations.StateRoot, "registry"));
        using var stop = new CancellationTokenSource();
        using var term = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, c => { c.Cancel = true; stop.Cancel(); });
        using var intr = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGINT, c => { c.Cancel = true; stop.Cancel(); });
        return IisHost.Run(config, line => Console.Error.WriteLine($"{DateTime.Now:HH:mm:ss} {line}"), stop.Token).GetAwaiter().GetResult();
    }
}
