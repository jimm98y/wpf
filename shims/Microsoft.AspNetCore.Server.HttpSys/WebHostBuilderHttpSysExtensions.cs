// UseHttpSys, served by Kestrel. http.sys takes URL prefixes (http://+:80/app/) on ports it shares among every
// process of the machine, and routes each request to the prefix that claims it. Here the process serves its own
// pipeline on a loopback port of its own, and its prefixes are registered with the machine's (HttpSysPort), whose
// front on each port forwards to it what its prefixes claim; a middleware ahead of the app's does http.sys's routing
// in the process: the connection taken as the front took it, the prefix with the strongest host (+, then a name,
// then *) and the longest path chosen, its path the PathBase and the rest the Path, and a request no prefix claims
// answered 404 as http.sys answers it. HTTPS -- ended at the front -- takes the certificate http.sys's SSL binding
// for the port names (netsh's, in the registry); with none, one made for this machine.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.HttpSys;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Hosting.Server.Features;
using AuthenticationSchemes = Microsoft.AspNetCore.Server.HttpSys.AuthenticationSchemes;

namespace Microsoft.AspNetCore.Hosting
{
    public static class WebHostBuilderHttpSysExtensions
    {
        public static IWebHostBuilder UseHttpSys(this IWebHostBuilder hostBuilder)
        {
            ArgumentNullException.ThrowIfNull(hostBuilder);
            hostBuilder.UseKestrel();
            return hostBuilder.ConfigureServices(services =>
            {
                services.AddSingleton<PrefixServer>();
                services.AddSingleton<IConfigureOptions<KestrelServerOptions>>(sp => sp.GetRequiredService<PrefixServer>());
                services.AddSingleton<IStartupFilter>(sp => sp.GetRequiredService<PrefixServer>());
                services.AddSingleton<IServerIntegratedAuth>(sp => new ServerIntegratedAuth
                {
                    IsEnabled = sp.GetRequiredService<IOptions<HttpSysOptions>>().Value.Authentication.Schemes != AuthenticationSchemes.None,
                    AuthenticationScheme = HttpSysDefaults.AuthenticationScheme,
                });
                services.AddTransient<WindowsAuthenticationHandler>();
                services.AddAuthenticationCore();
            });
        }

        public static IWebHostBuilder UseHttpSys(this IWebHostBuilder hostBuilder, Action<HttpSysOptions> options) =>
            hostBuilder.UseHttpSys().ConfigureServices(services => services.Configure(options));
    }

    internal sealed class ServerIntegratedAuth : IServerIntegratedAuth
    {
        public bool IsEnabled { get; set; }
        public string AuthenticationScheme { get; set; } = HttpSysDefaults.AuthenticationScheme;
    }

    /// <summary>The prefixes as Kestrel's endpoints, and http.sys's routing ahead of the app.</summary>
    internal sealed class PrefixServer : IConfigureOptions<KestrelServerOptions>, IStartupFilter
    {
        private readonly HttpSysOptions _options;
        private readonly IConfiguration _configuration;
        private readonly ILogger _logger;
        private readonly IServiceProvider _services;
        private UrlPrefix[]? _prefixes;
        private X509Certificate2? _ownCertificate;

        public PrefixServer(IOptions<HttpSysOptions> options, IConfiguration configuration, ILoggerFactory loggers, IServiceProvider services)
        {
            _options = options.Value;
            _configuration = configuration;
            _logger = loggers.CreateLogger("Microsoft.AspNetCore.Server.HttpSys.HttpSysListener");
            _services = services;
        }

        /// <summary>
        /// The prefixes registered: the options', or, with none (or the host preferring its own), the host's URLs,
        /// or http.sys's default.
        /// </summary>
        private UrlPrefix[] Prefixes()
        {
            if (_prefixes != null) return _prefixes;
            var urls = (_configuration[WebHostDefaults.ServerUrlsKey] ?? string.Empty)
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            bool preferHosting = string.Equals(_configuration[WebHostDefaults.PreferHostingUrlsKey], "true", StringComparison.OrdinalIgnoreCase);
            UrlPrefix[] own = _options.UrlPrefixes.Snapshot();
            if (own.Length == 0 || (preferHosting && urls.Length > 0))
            {
                own = urls.Length > 0 ? urls.Select(UrlPrefix.Create).ToArray() : new[] { UrlPrefix.Create("http://localhost:5000") };
                foreach (var p in own) _options.UrlPrefixes.Add(p);
            }
            return _prefixes = own;
        }

        public void Configure(KestrelServerOptions kestrel)
        {
            // http.sys's server reads none of Kestrel's configuration: only the prefixes are endpoints
            kestrel.Configure(new ConfigurationBuilder().Build());

            if (_options.MaxRequestBodySize is { } body) kestrel.Limits.MaxRequestBodySize = body;
            else kestrel.Limits.MaxRequestBodySize = null;
            if (_options.MaxConnections is { } connections) kestrel.Limits.MaxConcurrentConnections = connections < 0 ? null : connections;
            kestrel.AllowSynchronousIO = _options.AllowSynchronousIO;
            if (_options.UseLatin1RequestHeaders) kestrel.RequestHeaderEncodingSelector = _ => Encoding.Latin1;
            var timeouts = _options.Timeouts;
            if (timeouts.IdleConnection > TimeSpan.Zero) kestrel.Limits.KeepAliveTimeout = timeouts.IdleConnection;
            if (timeouts.HeaderWait > TimeSpan.Zero) kestrel.Limits.RequestHeadersTimeout = timeouts.HeaderWait;
            if (timeouts.MinSendBytesPerSecond > 0) kestrel.Limits.MinResponseDataRate = new MinDataRate(timeouts.MinSendBytesPerSecond, TimeSpan.FromSeconds(5));
            else kestrel.Limits.MinResponseDataRate = null;

            // this process's own endpoint: a loopback port of its own, which the port's front (HttpSysPort) forwards
            // the requests of this process's prefixes to
            kestrel.Listen(IPAddress.Loopback, 0);
            foreach (var p in Prefixes()) _logger.LogInformation("Listening on prefix: {Prefix}", p.FullPrefix);
        }

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            if (_options.Authentication.Schemes != AuthenticationSchemes.None)
            {
                var schemes = app.ApplicationServices.GetRequiredService<IAuthenticationSchemeProvider>();
                schemes.AddScheme(new AuthenticationScheme(HttpSysDefaults.AuthenticationScheme, _options.Authentication.AuthenticationDisplayName, typeof(WindowsAuthenticationHandler)));
                if (!_options.Authentication.AllowAnonymous)
                    _logger.LogWarning("Windows authentication is not available here: requests to a host that allows no anonymous ones are answered 401");
            }
            // once the server listens: the prefixes registered with its port, and the ports they name fronted
            if (app.ApplicationServices.GetService<IHostApplicationLifetime>() is { } lifetime)
            {
                lifetime.ApplicationStarted.Register(() =>
                {
                    var addresses = app.ApplicationServices.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses;
                    int local = addresses?.Select(a => Uri.TryCreate(a, UriKind.Absolute, out var u) ? u.Port : 0).FirstOrDefault(p => p > 0) ?? 0;
                    if (local == 0) { _logger.LogError("the server's own port is unknown: its prefixes are not registered"); return; }
                    _port = HttpSysPort.Register(Prefixes(), local, Certificate, _logger);
                });
                lifetime.ApplicationStopping.Register(() => _port?.DisposeAsync().AsTask().GetAwaiter().GetResult());
            }
            app.Use(Route);
            next(app);
        };

        private HttpSysPort? _port;

        /// <summary>http.sys's choice among prefixes: the strong wildcard's first, then a name's, then the weak
        /// wildcard's; within each, the longest path. Null where none claims the request.</summary>
        internal static UrlPrefix? Best(IEnumerable<UrlPrefix> prefixes, bool https, string host, IPAddress? localAddress, int port, string path)
        {
            string? local = localAddress?.ToString();
            UrlPrefix? match = null;
            int rank = int.MaxValue;
            foreach (var p in prefixes)
            {
                if (p.IsHttps != https || (p.PortValue != 0 && p.PortValue != port)) continue;
                int r = p.Host == "+" ? 0 : p.Host == "*" ? 2 : 1;
                if (r == 1)
                {
                    string h = p.Host.StartsWith('[') && p.Host.EndsWith(']') ? p.Host[1..^1] : p.Host;
                    if (!string.Equals(h, host.Trim('[', ']'), StringComparison.OrdinalIgnoreCase)
                        && !(local != null && IPAddress.TryParse(h, out var ip) && ip.ToString() == local)) continue;
                }
                if (!(path + "/").StartsWith(p.Path, StringComparison.OrdinalIgnoreCase)) continue;
                if (match == null || r < rank || (r == rank && p.Path.Length > match.Path.Length)) { match = p; rank = r; }
            }
            return match;
        }

        private Task Route(HttpContext context, RequestDelegate next)
        {
            var request = context.Request;
            // the connection as the port's front took it (what http.sys would have told this process)
            if (request.Headers.TryGetValue(HttpSysPort.ConnectionHeader, out var facts) && facts.ToString().Split(';') is { Length: 5 } f)
            {
                request.Headers.Remove(HttpSysPort.ConnectionHeader);
                request.Scheme = f[0];
                var c = context.Connection;
                if (IPAddress.TryParse(f[1], out var localIp)) c.LocalIpAddress = localIp;
                if (int.TryParse(f[2], out int localPort)) c.LocalPort = localPort;
                if (IPAddress.TryParse(f[3], out var remoteIp)) c.RemoteIpAddress = remoteIp;
                if (int.TryParse(f[4], out int remotePort)) c.RemotePort = remotePort;
            }
            string full = (request.PathBase + request.Path).Value ?? string.Empty;
            if (full.Length == 0) full = "/";
            var match = Best(Prefixes(), request.IsHttps, request.Host.Host, context.Connection.LocalIpAddress, context.Connection.LocalPort, full);
            if (match == null)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return Task.CompletedTask;
            }
            int baseLength = match.PathWithoutTrailingSlash.Length;
            request.PathBase = new PathString(full[..baseLength]);
            request.Path = baseLength < full.Length ? new PathString(full[baseLength..]) : PathString.Empty;

            var auth = _options.Authentication;
            if (auth.Schemes != AuthenticationSchemes.None && !auth.AllowAnonymous)
            {
                WindowsAuthenticationHandler.Challenge(context.Response, auth.Schemes);
                return Task.CompletedTask;
            }
            return next(context);
        }

        /// <summary>The certificate http.sys's SSL binding names for the name and port, else this machine's own.</summary>
        internal X509Certificate2 Certificate(string? name, int port)
        {
            if (Bound(name, port) is { } bound) return bound;
            lock (this)
            {
                if (_ownCertificate != null) return _ownCertificate;
                _logger.LogWarning(
                    "no certificate is bound to port {Port} (HKLM\\SYSTEM\\CurrentControlSet\\Services\\HTTP\\Parameters\\SslBindingInfo\\0.0.0.0:{Port}, as netsh http add sslcert binds one): a certificate made for this machine is used",
                    port, port);
                return _ownCertificate = MachineCertificate();
            }
        }

        private static X509Certificate2? Bound(string? name, int port)
        {
            const string Parameters = @"SYSTEM\CurrentControlSet\Services\HTTP\Parameters\";
            var keys = new List<string>();
            if (!string.IsNullOrEmpty(name)) keys.Add($@"{Parameters}SslSniBindingInfo\{name}:{port}");
            keys.Add($@"{Parameters}SslCcsBindingInfo\0.0.0.0:{port}");
            keys.Add($@"{Parameters}SslBindingInfo\0.0.0.0:{port}");
            keys.Add($@"{Parameters}SslBindingInfo\[::]:{port}");
            foreach (string path in keys)
            {
                try
                {
                    using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(path);
                    if (key?.GetValue("SslCertHash") is not byte[] hash) continue;
                    string storeName = key.GetValue("SslCertStoreName") as string ?? "MY";
                    using var store = new X509Store(storeName, StoreLocation.LocalMachine);
                    store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
                    var found = store.Certificates.Find(X509FindType.FindByThumbprint, Convert.ToHexString(hash), validOnly: false);
                    if (found.Count > 0 && found[0].HasPrivateKey) return found[0];
                }
                catch (Exception) { }
            }
            return null;
        }

        private static X509Certificate2 MachineCertificate()
        {
            using var rsa = RSA.Create(2048);
            string machine = Environment.MachineName;
            var request = new CertificateRequest($"CN={machine}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName(machine);
            names.AddDnsName("localhost");
            names.AddIpAddress(IPAddress.Loopback);
            names.AddIpAddress(IPAddress.IPv6Loopback);
            request.CertificateExtensions.Add(names.Build());
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, critical: false));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));
            using var made = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
            // the key a TLS server can use on every platform: the certificate exported and loaded again
            return X509CertificateLoader.LoadPkcs12(made.Export(X509ContentType.Pkcs12), null);
        }
    }

    /// <summary>
    /// http.sys's "Windows" scheme: who a request authenticated as with Negotiate, NTLM or Basic. There is no Windows
    /// authentication here, so a request is anonymous; a challenge asks for the schemes as http.sys asks.
    /// </summary>
    internal sealed class WindowsAuthenticationHandler : IAuthenticationHandler
    {
        private readonly IOptions<HttpSysOptions> _options;
        private HttpContext? _context;

        public WindowsAuthenticationHandler(IOptions<HttpSysOptions> options) => _options = options;

        public Task InitializeAsync(AuthenticationScheme scheme, HttpContext context)
        {
            _context = context;
            return Task.CompletedTask;
        }

        public Task<AuthenticateResult> AuthenticateAsync() => Task.FromResult(AuthenticateResult.NoResult());

        public Task ChallengeAsync(AuthenticationProperties? properties)
        {
            Challenge(_context!.Response, _options.Value.Authentication.Schemes);
            return Task.CompletedTask;
        }

        public Task ForbidAsync(AuthenticationProperties? properties)
        {
            _context!.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        }

        internal static void Challenge(HttpResponse response, AuthenticationSchemes schemes)
        {
            response.StatusCode = StatusCodes.Status401Unauthorized;
            if (schemes.HasFlag(AuthenticationSchemes.Negotiate)) response.Headers.Append("WWW-Authenticate", "Negotiate");
            if (schemes.HasFlag(AuthenticationSchemes.NTLM)) response.Headers.Append("WWW-Authenticate", "NTLM");
            if (schemes.HasFlag(AuthenticationSchemes.Basic)) response.Headers.Append("WWW-Authenticate", "Basic realm=\"\"");
        }
    }
}
