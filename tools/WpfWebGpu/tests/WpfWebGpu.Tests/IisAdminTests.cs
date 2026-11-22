// IIS's configuration system run here (WpfWebGpu.IisAdmin), as a program configures IIS through it: Microsoft.Web.Administration
// itself -- the copy programs ship, rewritten as wpfrun rewrites it when a program loads it -- reads the sites of an
// applicationHost.config, changes a site's bindings and commits, on every OS.

extern alias RegistryShim;

using System.Reflection;
using System.Runtime.Loader;
using System.Xml.Linq;
using WpfWebGpu.ComRewrite;
using Xunit;

namespace WpfWebGpu.Tests;

[Collection("IisAdmin")]
public sealed class IisAdminTests
{
    private static string Meta(string key) => typeof(IisAdminTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == key).Value!;

    private static readonly Lazy<Assembly> s_mwa = new(() =>
    {
        string mwa = Meta("MicrosoftWebAdministration");
        var (image, symbols, _) = ComRewriter.RewriteToMemory(mwa, new[] { Path.GetDirectoryName(mwa)!, AppContext.BaseDirectory },
            com: true, pinvokes: true, windows: false, catalog: Win32Catalog.Load(new[] { Meta("WpfWebGpuWin32") }));
        symbols?.Dispose();
        return new ClientContext().LoadFromStream(image);
    });

    private sealed class ClientContext() : AssemblyLoadContext("Microsoft.Web.Administration, rewritten")
    {
        protected override Assembly? Load(AssemblyName name) =>
            name.Name == "WpfWebGpu.ComInterop" ? typeof(WpfWebGpu.ComInterop.Com).Assembly : null;
    }

    private const string Config = """
        <?xml version="1.0" encoding="UTF-8"?>
        <configuration>
            <system.applicationHost>
                <applicationPools>
                    <add name="DefaultAppPool" />
                </applicationPools>
                <sites>
                    <site name="Default Web Site" id="1">
                        <application path="/">
                            <virtualDirectory path="/" physicalPath="%SystemDrive%\inetpub\wwwroot" />
                        </application>
                        <bindings>
                            <binding protocol="http" bindingInformation="*:80:" />
                        </bindings>
                    </site>
                </sites>
            </system.applicationHost>
        </configuration>
        """;

    // a ServerManager over a configuration folder of the test's own
    private static (dynamic Manager, string File) Open(string? config = Config)
    {
        string dir = TestEnvironment.NewDirectory("iisconfig");
        string file = Path.Combine(dir, "applicationHost.config");
        if (config != null) File.WriteAllText(file, config);
        Environment.SetEnvironmentVariable("WPFWEBGPU_IIS_CONFIG", dir);
        Type type = s_mwa.Value.GetType("Microsoft.Web.Administration.ServerManager", throwOnError: true)!;
        return (Activator.CreateInstance(type)!, file);
    }

    private static dynamic Site(dynamic manager, string name)
    {
        foreach (dynamic s in manager.Sites)
            if ((string)s.Name == name) return s;
        throw new InvalidOperationException("no site " + name);
    }

    [Fact]
    public void Microsoft_Web_Administration_reads_the_sites_and_their_bindings()
    {
        var (manager, _) = Open();
        dynamic site = Site(manager, "Default Web Site");
        Assert.Equal(1L, (long)site.Id);
        Assert.True((bool)site.ServerAutoStart);   // the schema's default: the file does not set it
        var bindings = new List<(string, string)>();
        foreach (dynamic b in site.Bindings) bindings.Add(((string)b.Protocol, (string)b.BindingInformation));
        Assert.Equal(new[] { ("http", "*:80:") }, bindings);
        dynamic app = site.Applications["/"];
        Assert.Equal(@"%SystemDrive%\inetpub\wwwroot", (string)app.VirtualDirectories["/"].PhysicalPath);
        manager.Dispose();
    }

    [Fact]
    public void A_binding_added_and_committed_is_in_applicationHost_config()
    {
        var (manager, file) = Open();
        dynamic site = Site(manager, "Default Web Site");
        site.Bindings.Add("*:8080:", "http");
        manager.CommitChanges();
        manager.Dispose();

        var bindings = XDocument.Load(file).Descendants("binding").Select(b => ((string?)b.Attribute("protocol"), (string?)b.Attribute("bindingInformation"))).ToList();
        Assert.Equal(new[] { ("http", "*:80:"), ("http", "*:8080:") }, bindings);

        // and a new ServerManager reads it back; a duplicate is refused, as IIS refuses it
        dynamic again = Activator.CreateInstance(s_mwa.Value.GetType("Microsoft.Web.Administration.ServerManager")!)!;
        dynamic site2 = Site(again, "Default Web Site");
        Assert.Equal(2, (int)site2.Bindings.Count);
        Assert.ThrowsAny<Exception>(() => { site2.Bindings.Add("*:8080:", "http"); again.CommitChanges(); });
    }

    [Fact]
    public void A_binding_removed_is_gone_from_the_file()
    {
        var (manager, file) = Open();
        dynamic site = Site(manager, "Default Web Site");
        dynamic bindings = site.Bindings;
        bindings.RemoveAt(0);
        manager.CommitChanges();
        Assert.Empty(XDocument.Load(file).Descendants("binding"));
    }

    [Fact]
    public void An_HTTPS_binding_with_a_certificate_is_http_sys_s_as_IIS_makes_it()
    {
        // a certificate as a setup makes one (a self-signed one, its hash what the binding names)
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=iis-test", rsa,
            System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        byte[] hash = cert.GetCertHash();
        const string Key = @"SYSTEM\CurrentControlSet\Services\HTTP\Parameters\SslBindingInfo\0.0.0.0:443";
        RegistryShim::Microsoft.Win32.Registry.LocalMachine.DeleteSubKeyTree(Key, false);

        var (manager, file) = Open();
        dynamic site = Site(manager, "Default Web Site");
        site.Bindings.Add("*:443:", hash, "My");
        manager.CommitChanges();
        manager.Dispose();

        // the binding in applicationHost.config, without its certificate (that is http.sys's)
        var binding = XDocument.Load(file).Descendants("binding").Single(b => (string?)b.Attribute("protocol") == "https");
        Assert.Equal("*:443:", (string?)binding.Attribute("bindingInformation"));
        Assert.Null(binding.Attribute("certificateHash"));
        // the certificate in http.sys's configuration, where netsh http add sslcert puts it
        using (var k = RegistryShim::Microsoft.Win32.Registry.LocalMachine.OpenSubKey(Key))
        {
            Assert.NotNull(k);
            Assert.Equal(hash, (byte[])k!.GetValue("SslCertHash")!);
            Assert.Equal("My", (string?)k.GetValue("SslCertStoreName"), ignoreCase: true);
        }
        // and read back as IIS reads it: the binding's certificate
        dynamic again = Activator.CreateInstance(s_mwa.Value.GetType("Microsoft.Web.Administration.ServerManager")!)!;
        foreach (dynamic b in Site(again, "Default Web Site").Bindings)
            if ((string)b.Protocol == "https") Assert.Equal(hash, (byte[])b.CertificateHash);

        // removed with its binding
        dynamic site3 = Site(again, "Default Web Site");
        dynamic list = site3.Bindings;
        for (int i = (int)list.Count - 1; i >= 0; i--)
            if ((string)list[i].Protocol == "https") list.RemoveAt(i);
        again.CommitChanges();
        using (var gone = RegistryShim::Microsoft.Win32.Registry.LocalMachine.OpenSubKey(Key))
            Assert.Null(gone);
    }

    [Fact]
    public void A_site_added_where_the_file_has_none()
    {
        var (manager, file) = Open(config: null);
        dynamic site = manager.Sites.Add("Web", "http", "*:8081:", "/srv/web");
        site.Id = 7L;
        manager.CommitChanges();
        var x = XDocument.Load(file).Descendants("site").Single();
        Assert.Equal("Web", (string?)x.Attribute("name"));
        Assert.Equal("7", (string?)x.Attribute("id"));
        Assert.Equal("*:8081:", (string?)x.Descendants("binding").Single().Attribute("bindingInformation"));
        Assert.Equal("/srv/web", (string?)x.Descendants("virtualDirectory").Single().Attribute("physicalPath"));
    }
}
