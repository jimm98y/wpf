extern alias RegistryShim;

using System.Net;
using WpfWebGpu.Services;
using Xunit;

namespace WpfWebGpu.Tests;

/// <summary>The installation's IIS sites: their configuration as its machine's applicationHost.config has it, and the web
/// server (wpfrun iis) claiming their URLs from http.sys and serving them.</summary>
[Collection("http.sys")]
public class IisTests
{
    private const string Config = """
        <?xml version="1.0" encoding="UTF-8"?>
        <configuration>
          <system.applicationHost>
            <applicationPools>
              <add name="DefaultAppPool" />
              <add name="ProductPool" autoStart="true" managedPipelineMode="Integrated">
                <processModel identityType="NetworkService" maxProcesses="1" />
              </add>
              <applicationPoolDefaults><processModel identityType="ApplicationPoolIdentity" /></applicationPoolDefaults>
            </applicationPools>
            <sites>
              <site name="Default Web Site" id="1">
                <application path="/">
                  <virtualDirectory path="/" physicalPath="%SystemDrive%\inetpub\wwwroot" />
                  <virtualDirectory path="/Displays" physicalPath="C:\ProgramData\Vendor\Displays" />
                </application>
                <application path="/Portal" applicationPool="ProductPool">
                  <virtualDirectory path="/Displays" physicalPath="C:\ProgramData\Vendor\Displays" />
                  <virtualDirectory path="/" physicalPath="C:\Program Files\Vendor\Product\Services\Portal" />
                </application>
                <bindings>
                  <binding protocol="http" bindingInformation="*:80:" />
                  <binding protocol="net.tcp" bindingInformation="808:*" />
                </bindings>
              </site>
            </sites>
          </system.applicationHost>
        </configuration>
        """;

    [Fact]
    public void Sites_are_taken_from_applicationHost_config_with_the_installation_s_folders()
    {
        static string? Local(string windows) =>
            windows.StartsWith(@"C:\Program Files\Vendor\Product", StringComparison.OrdinalIgnoreCase) ? "/opt/product/app" + windows[@"C:\Program Files\Vendor\Product".Length..].Replace('\\', '/')
            : windows.StartsWith(@"C:\ProgramData", StringComparison.OrdinalIgnoreCase) ? "/opt/product/state/ProgramData" + windows[@"C:\ProgramData".Length..].Replace('\\', '/')
            : null;
        var config = IisConfiguration.Parse(Config, Local);

        var site = Assert.Single(config.Sites);
        var binding = Assert.Single(site.Bindings);                                    // net.tcp is not http.sys's
        Assert.Equal(("http", "*", 80, ""), (binding.Protocol, binding.Address, binding.Port, binding.Host));
        Assert.Equal(new[] { "/", "/Portal" }, site.Applications.Select(a => a.Path));
        var root = site.Applications[0];
        Assert.Null(root.VirtualDirectories[0].PhysicalPath);                          // IIS's own wwwroot: not the installation's
        Assert.Equal("/opt/product/state/ProgramData/Vendor/Displays", root.VirtualDirectories[1].PhysicalPath);
        var portal = site.Applications[1];
        Assert.Equal(("ProductPool", "/opt/product/app/Services/Portal"), (portal.Pool, portal.VirtualDirectories.Single(v => v.Path == "/").PhysicalPath));
        Assert.Equal(("NetworkService", true), (config.Pools[1].Identity, config.Pools[1].AutoStart));
        Assert.Equal("ApplicationPoolIdentity", config.Pools[0].Identity);              // the defaults'
    }

    [Fact]
    public void A_folder_s_web_config_gives_its_files_headers_and_types_as_IIS_merges_them()
    {
        string root = TestEnvironment.NewDirectory("iis-static");
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        File.WriteAllText(Path.Combine(root, "web.config"), WpfWebGpu.Packager.Desktop.WebPages.WebConfig);
        File.WriteAllText(Path.Combine(root, "sub", "web.config"), """
            <configuration><system.webServer>
              <httpProtocol><customHeaders><remove name="Cross-Origin-Resource-Policy" /><add name="X-Sub" value="1" /></customHeaders></httpProtocol>
              <staticContent><remove fileExtension=".dll" /><mimeMap fileExtension=".gdfx" mimeType="application/xml" /></staticContent>
            </system.webServer></configuration>
            """);
        var page = StaticSettings.For(root, Path.Combine(root, "index.html"));
        Assert.Contains(("Cross-Origin-Opener-Policy", "same-origin"), page.Headers);
        Assert.Contains(("Cross-Origin-Embedder-Policy", "require-corp"), page.Headers);
        Assert.Equal("application/wasm", page.Types[".wasm"]);
        var sub = StaticSettings.For(root, Path.Combine(root, "sub", "a.gdfx"));
        Assert.DoesNotContain(sub.Headers, h => h.Name == "Cross-Origin-Resource-Policy");   // removed below
        Assert.Contains(("X-Sub", "1"), sub.Headers);
        Assert.Contains(("Cross-Origin-Opener-Policy", "same-origin"), sub.Headers);          // inherited
        Assert.False(sub.Types.ContainsKey(".dll"));
        Assert.Equal("application/xml", sub.Types[".gdfx"]);
        // request filtering's defaults: configuration files and the applications' own folders
        Assert.True(StaticSettings.Refused("web.config"));
        Assert.True(StaticSettings.Refused("payload/App.dll.config"));
        Assert.True(StaticSettings.Refused("bin/App.dll"));
        Assert.False(StaticSettings.Refused("_framework/App.dll"));
        Assert.False(StaticSettings.Refused("payload/000001.bin"));
    }

    [Fact]
    public async Task The_web_server_serves_a_site_s_files_through_http_sys()
    {
        if (OperatingSystem.IsWindows()) return;
        int port = FreePort();
        string folder = TestEnvironment.NewDirectory("iis-httpsys");
        string? was = Environment.GetEnvironmentVariable("WPFWEBGPU_HTTPSYS_DIR");
        Environment.SetEnvironmentVariable("WPFWEBGPU_HTTPSYS_DIR", folder);
        // a user's server registers under a reservation, as on Windows (IIS itself is the system's)
        using (var key = RegistryShim::Microsoft.Win32.Registry.LocalMachine.CreateSubKey(@"SYSTEM\CurrentControlSet\Services\HTTP\Parameters\UrlAclInfo"))
            key.SetValue($"http://+:{port}/", "D:(A;;GX;;;WD)");
        string site = TestEnvironment.NewDirectory("iis-site");
        File.WriteAllText(Path.Combine(site, "index.html"), "<p>home</p>");
        Directory.CreateDirectory(Path.Combine(site, "css"));
        File.WriteAllText(Path.Combine(site, "css", "a.css"), "p{}");
        var config = new IisConfiguration
        {
            Sites = { new IisSite { Name = "Test", Bindings = { new IisBinding { Port = port } }, Applications = { new IisApplication { Path = "/Docs", VirtualDirectories = { new IisVirtualDirectory { Path = "/", PhysicalPath = site } } } } } },
        };
        string configPath = Path.Combine(TestEnvironment.NewDirectory("iis-config"), IisConfiguration.FileName);
        File.WriteAllText(configPath, config.ToJson());
        using var stop = new CancellationTokenSource();
        var log = new List<string>();
        var serving = IisHost.Run(configPath, l => { lock (log) log.Add(l); }, stop.Token);
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        try
        {
            HttpResponseMessage? css = null;
            for (int i = 0; i < 100; i++)
            {
                try { css = await http.GetAsync($"http://127.0.0.1:{port}/docs/css/a.css"); if (css.StatusCode == HttpStatusCode.OK) break; }
                catch (HttpRequestException) { }
                await Task.Delay(100);
            }
            Assert.Equal(HttpStatusCode.OK, css?.StatusCode);                          // an application's path, as IIS's, any case
            Assert.Equal("text/css", css!.Content.Headers.ContentType?.MediaType);
            Assert.Equal("p{}", await css.Content.ReadAsStringAsync());
            var bare = await http.GetAsync($"http://127.0.0.1:{port}/Docs");
            Assert.Equal(HttpStatusCode.MovedPermanently, bare.StatusCode);              // a folder's URL ends in '/'
            Assert.Equal("<p>home</p>", await http.GetStringAsync($"http://127.0.0.1:{port}/Docs/"));   // its default document
            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"http://127.0.0.1:{port}/Docs/../../etc/passwd")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"http://127.0.0.1:{port}/Elsewhere/")).StatusCode);
        }
        finally
        {
            stop.Cancel();
            await serving;
            string pid = Path.Combine(folder, "service.pid");
            if (File.Exists(pid) && int.TryParse(File.ReadAllText(pid), out int id))
                try { using var p = System.Diagnostics.Process.GetProcessById(id); p.Kill(); p.WaitForExit(5000); } catch (ArgumentException) { }
            Environment.SetEnvironmentVariable("WPFWEBGPU_HTTPSYS_DIR", was);
        }
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
