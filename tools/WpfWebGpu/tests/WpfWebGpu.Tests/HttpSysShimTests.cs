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

    private static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }
}
