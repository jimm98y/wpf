using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.HttpSys;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace WpfWebGpu.Tests;

// The web host stand-ins off Windows: UseHttpSys served by Kestrel with http.sys's routing
// (shims/Microsoft.AspNetCore.Server.HttpSys), and a key ring protected with DPAPI
// (shims/Microsoft.AspNetCore.Cryptography.Internal).
public class HttpSysShimTests
{
    [Fact]
    public void A_prefix_is_parsed_as_http_sys_parses_it()
    {
        var p = UrlPrefix.Create("http://+:80/webapi");
        Assert.Equal(("http", "+", "80", "/webapi/"), (p.Scheme, p.Host, p.Port, p.Path));
        Assert.Equal("http://+:80/webapi/", p.FullPrefix);
        Assert.Equal(443, UrlPrefix.Create("https://example.com").PortValue);
        Assert.Equal("[::1]", UrlPrefix.Create("http://[::1]:8080/").Host);
        Assert.Equal(UrlPrefix.Create("HTTP://localhost:5000/A/"), UrlPrefix.Create("http://localhost:5000/a/"));
        Assert.Throws<FormatException>(() => UrlPrefix.Create("localhost:5000"));
    }

    [Fact]
    public async Task Requests_are_routed_to_their_prefix_and_keys_are_protected_with_DPAPI()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "on Windows the framework's own");
        using var service = Service.Start("httpsys-routing");
        int http = FreePort(), https = FreePort();
        string keys = Path.Combine(Path.GetTempPath(), "wpfwebgpu-keys-" + Guid.NewGuid().ToString("N"));
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseHttpSys(o =>
        {
            o.UrlPrefixes.Add($"http://+:{http}/api/");
            o.UrlPrefixes.Add($"http://*:{http}/api/deep/");
            o.UrlPrefixes.Add($"https://localhost:{https}/");
        });
        builder.Services.AddDataProtection().SetApplicationName("tests").PersistKeysToFileSystem(new DirectoryInfo(keys)).ProtectKeysWithDpapi();
        await using var app = builder.Build();
        app.Run(async c =>
        {
            var protector = c.RequestServices.GetRequiredService<IDataProtectionProvider>().CreateProtector("t");
            await c.Response.WriteAsync($"{c.Request.PathBase}|{c.Request.Path}|{protector.Unprotect(protector.Protect("ok"))}");
        });
        await app.StartAsync();
        try
        {
            using var client = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true });
            Assert.Equal("/api|/a/b|ok", await client.GetStringAsync($"http://127.0.0.1:{http}/api/a/b"));
            Assert.Equal("/api||ok", await client.GetStringAsync($"http://127.0.0.1:{http}/api"));
            // the strong wildcard's prefix before the weak one's, however long the weak one's path
            Assert.Equal("/api|/deep/x|ok", await client.GetStringAsync($"http://127.0.0.1:{http}/api/deep/x"));
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"http://127.0.0.1:{http}/other")).StatusCode);
            Assert.Equal("|/|ok", await client.GetStringAsync($"https://localhost:{https}/"));

            string ring = string.Concat(Directory.GetFiles(keys, "*.xml").Select(File.ReadAllText));
            Assert.Contains("DpapiXmlDecryptor", ring);
            Assert.DoesNotContain("<masterKey", ring);
        }
        finally
        {
            await app.StopAsync();
            Directory.Delete(keys, recursive: true);
        }
    }

    [Fact]
    public async Task Hosts_share_a_port_through_the_machine_s_service_which_outlives_them()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "on Windows the framework's own");
        using var service = Service.Start("httpsys-shared");
        int port = FreePort();
        async Task<WebApplication> Host(string prefix, string answer)
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseHttpSys(o => o.UrlPrefixes.Add($"http://+:{port}/{prefix}/"));
            var app = builder.Build();
            app.Run(c => c.Response.WriteAsync($"{answer}|{c.Request.PathBase}|{c.Request.Path}|{c.Connection.LocalPort}|{c.Connection.RemoteIpAddress}"));
            await app.StartAsync();
            return app;
        }
        using var client = new HttpClient();
        async Task<string> Get(string path, int tries = 1)
        {
            for (int t = 0; ; t++)
            {
                try
                {
                    var r = await client.GetAsync($"http://127.0.0.1:{port}{path}");
                    if (r.IsSuccessStatusCode || t + 1 >= tries) return $"{(int)r.StatusCode} {await r.Content.ReadAsStringAsync()}";
                }
                catch (HttpRequestException) when (t + 1 < tries) { }
                await Task.Delay(500);
            }
        }
        var one = await Host("one", "1");
        var two = await Host("two", "2");
        try
        {
            // served once the hosts have started, as http.sys's prefixes are
            Assert.Equal($"200 1|/one|/a|{port}|127.0.0.1", await Get("/one/a"));
            Assert.Equal($"200 2|/two|/b|{port}|127.0.0.1", await Get("/two/b"));
            Assert.StartsWith("404", await Get("/three/"));
            // the host that started the service stops: the other is served as before
            await one.StopAsync();
            await one.DisposeAsync();
            Assert.Equal($"200 2|/two|/c|{port}|127.0.0.1", await Get("/two/c"));
            Assert.StartsWith("404", await Get("/one/a"));
            // the service itself ends: the hosts start it again
            service.Kill();
            Assert.Equal($"200 2|/two|/d|{port}|127.0.0.1", await Get("/two/d", tries: 60));
        }
        finally
        {
            await two.StopAsync();
            await two.DisposeAsync();
        }
    }

    /// <summary>The machine's http.sys service for a test: its own registry folder, started on demand through the
    /// launcher built beside the tests, and stopped at the end.</summary>
    private sealed class Service : IDisposable
    {
        private readonly string _folder;
        private Service(string folder) => _folder = folder;

        public static Service Start(string name)
        {
            string folder = TestEnvironment.NewDirectory(name);
            Environment.SetEnvironmentVariable("WPFWEBGPU_HTTPSYS_DIR", folder);
            string root = Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));
            Environment.SetEnvironmentVariable("WPFWEBGPU_LAUNCHER", Path.Combine(root, "dotnet"));
            Environment.SetEnvironmentVariable("WPFWEBGPU_LAUNCHER_DLL", Path.Combine(AppContext.BaseDirectory, "wpfrun.dll"));
            return new Service(folder);
        }

        public void Kill()
        {
            string pid = Path.Combine(_folder, "service.pid");
            if (File.Exists(pid) && int.TryParse(File.ReadAllText(pid), out int id))
                try { using var p = System.Diagnostics.Process.GetProcessById(id); p.Kill(); p.WaitForExit(5000); } catch (ArgumentException) { }
        }

        public void Dispose()
        {
            Kill();
            Environment.SetEnvironmentVariable("WPFWEBGPU_HTTPSYS_DIR", null);
            Environment.SetEnvironmentVariable("WPFWEBGPU_LAUNCHER", null);
            Environment.SetEnvironmentVariable("WPFWEBGPU_LAUNCHER_DLL", null);
        }
    }

    private static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }
}
