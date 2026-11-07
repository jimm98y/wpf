extern alias RegistryShim;

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
[Collection("http.sys")]
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
        Service.Reserve($"http://+:{http}/", "D:(A;;GX;;;WD)");
        Service.Reserve($"https://+:{https}/", "D:(A;;GX;;;WD)");
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
        Service.Reserve($"http://+:{port}/", "D:(A;;GX;;;WD)");
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

    [Fact]
    public async Task A_prefix_is_registered_only_under_a_reservation_that_lets_the_account_listen()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "on Windows the framework's own");
        Assert.SkipWhen(WpfWebGpu.HttpSys.UrlAcl.CurrentUid() == 0, "root registers any prefix, as an elevated administrator");
        using var service = Service.Start("httpsys-acl");
        int port = FreePort();
        Service.Reserve($"http://+:{port}/ok/", "D:(A;;GX;;;WD)");
        Service.Reserve($"http://+:{port}/denied/", "D:(D;;GX;;;WD)(A;;GX;;;WD)");
        async Task<WebApplication> Start(string prefix)
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseHttpSys(o => o.UrlPrefixes.Add($"http://+:{port}/{prefix}/"));
            var app = builder.Build();
            app.Run(c => c.Response.WriteAsync(prefix));
            await app.StartAsync();
            return app;
        }
        await using var ok = await Start("ok");
        // no reservation, and one that denies: "Access is denied" when the server starts, as from http.sys
        Assert.Equal(5, (await Assert.ThrowsAsync<HttpSysException>(() => Start("none"))).ErrorCode);
        Assert.Equal(5, (await Assert.ThrowsAsync<HttpSysException>(() => Start("denied"))).ErrorCode);

        // a registration written past the stand-in (pointing a prefix it may not have at this process's server):
        // the service refuses it too
        string forged = Path.Combine(service.Folder, $"{Environment.ProcessId}-1.prefixes");
        int okPort = new Uri(ok.Urls.First()).Port;
        File.WriteAllLines(forged, new[] { okPort.ToString(), $"http://+:{port}/forged/" });
        using var client = new HttpClient();
        Assert.Equal("ok", await client.GetStringAsync($"http://127.0.0.1:{port}/ok/"));
        await Task.Delay(1200);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"http://127.0.0.1:{port}/forged/")).StatusCode);
        Assert.Contains($"http://+:{port}/forged/ refused: no URL reservation covers", File.ReadAllText(Path.Combine(service.Folder, "service.log")));
        File.Delete(forged);
        await ok.StopAsync();
    }

    [Fact]
    public void A_reservation_netsh_wrote_is_read_from_its_binary_descriptor()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "on Windows the framework's own");
        // as a Windows machine's registry export has them: one for NETWORK SERVICE (GA), one for everyone (GX)
        int port = FreePort();
        Service.Reserve($"http://+:{port}/service/", Convert.FromHexString("010004800000000000000000000000001400000002001c00010000000000140000000010010100000000000514000000"));
        Service.Reserve($"http://+:{port}/everyone/", Convert.FromHexString("010004800000000000000000000000001400000002001c00010000000000140000000020010100000000000100000000"));
        var service = WpfWebGpu.HttpSys.SharedPrefix.Parse($"http://+:{port}/service/")!;
        var everyone = WpfWebGpu.HttpSys.SharedPrefix.Parse($"http://+:{port}/everyone/sub/")!;
        Assert.False(WpfWebGpu.HttpSys.UrlAcl.MayRegister(501, service, out string why));
        Assert.Contains("does not let", why);
        Assert.True(WpfWebGpu.HttpSys.UrlAcl.MayRegister(0, service, out _));          // an elevated administrator
        Assert.True(WpfWebGpu.HttpSys.UrlAcl.MayRegister(501, everyone, out _));       // under the reserved path
        // the machine's NETWORK SERVICE account, as the installers make it: what the reservation grants
        Assert.True(WpfWebGpu.HttpSys.UrlAcl.MayRegister(450, "_wpfnetworkservice", service, out _));
        Assert.True(WpfWebGpu.HttpSys.UrlAcl.MayRegister(120, "wpf-networkservice", service, out _));
        Assert.False(WpfWebGpu.HttpSys.UrlAcl.MayRegister(451, "_wpflocalservice", service, out _));
    }

    [Fact]
    public async Task An_HttpListener_shares_a_port_with_a_web_host_as_http_sys_clients()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "on Windows the framework's own");
        using var service = Service.Start("httpsys-listener");
        int port = FreePort();
        Service.Reserve($"http://+:{port}/", "D:(A;;GX;;;WD)");

        // a web host on UseHttpSys and an HttpListener (with a WebSocket endpoint), the same port
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseHttpSys(o => o.UrlPrefixes.Add($"http://+:{port}/web/"));
        await using var web = builder.Build();
        web.Run(c => c.Response.WriteAsync("web"));
        await web.StartAsync();

        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://+:{port}/hl/");
        listener.Start();
        var serving = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext c;
                try { c = await listener.GetContextAsync(); } catch (Exception) { return; }
                if (c.Request.IsWebSocketRequest)
                {
                    var ws = (await c.AcceptWebSocketAsync(null)).WebSocket;
                    var buffer = new byte[64];
                    var got = await ws.ReceiveAsync(buffer, default);
                    await ws.SendAsync(buffer.AsMemory(0, got.Count), System.Net.WebSockets.WebSocketMessageType.Text, true, default);
                    await ws.ReceiveAsync(buffer, default);          // the client's close
                    await ws.CloseOutputAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "", default);
                    continue;
                }
                byte[] body = System.Text.Encoding.UTF8.GetBytes($"hl|{c.Request.Url}|{c.Request.IsSecureConnection}|{c.Request.LocalEndPoint!.Port}|{c.Request.RemoteEndPoint!.Address}");
                c.Response.OutputStream.Write(body);
                c.Response.Close();
            }
        });

        using var client = new HttpClient();
        Assert.Equal("web", await client.GetStringAsync($"http://127.0.0.1:{port}/web/"));
        Assert.Equal($"hl|http://127.0.0.1:{port}/hl/x|False|{port}|127.0.0.1", await client.GetStringAsync($"http://127.0.0.1:{port}/hl/x"));
        using (var ws = new System.Net.WebSockets.ClientWebSocket())
        {
            await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/hl/ws"), default);
            await ws.SendAsync("echo"u8.ToArray(), System.Net.WebSockets.WebSocketMessageType.Text, true, default);
            var buffer = new byte[64];
            var got = await ws.ReceiveAsync(buffer, default);
            Assert.Equal("echo", System.Text.Encoding.UTF8.GetString(buffer, 0, got.Count));
            await ws.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "", default);
        }

        // a prefix with no reservation: refused, as by http.sys
        using var refused = new HttpListener();
        refused.Prefixes.Add($"http://+:{FreePort()}/nope/");
        Assert.Equal(5, Assert.Throws<HttpListenerException>(() => refused.Start()).ErrorCode);

        listener.Stop();
        await serving;
        await web.StopAsync();
    }

    /// <summary>The machine's http.sys service for a test: its own registry folder, started on demand through the
    /// launcher built beside the tests, and stopped at the end.</summary>
    private sealed class Service : IDisposable
    {
        private readonly string _folder;
        private readonly string? _was;
        private Service(string folder, string? was) { _folder = folder; _was = was; }
        public string Folder => _folder;

        /// <summary>A URL reservation, as netsh http add urlacl makes one (its descriptor as SDDL).</summary>
        public static void Reserve(string url, object descriptor)
        {
            using var key = RegistryShim::Microsoft.Win32.Registry.LocalMachine.CreateSubKey(@"SYSTEM\CurrentControlSet\Services\HTTP\Parameters\UrlAclInfo");
            key.SetValue(url, descriptor);
        }

        public static Service Start(string name)
        {
            string folder = TestEnvironment.NewDirectory(name);
            string? was = Environment.GetEnvironmentVariable("WPFWEBGPU_HTTPSYS_DIR");
            Environment.SetEnvironmentVariable("WPFWEBGPU_HTTPSYS_DIR", folder);
            return new Service(folder, was);
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
            Environment.SetEnvironmentVariable("WPFWEBGPU_HTTPSYS_DIR", _was);
        }
    }

    private static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }
}
