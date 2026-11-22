// The machine's applicationHost.config an installation brings (IisConfiguration.ApplicationHostConfig) and an upgrade's
// merged into it (MergeApplicationHost): its folders this system's, its pages its applications, and what IIS's
// configuration system changed on the machine -- an HTTPS binding a setup made -- kept through the upgrade.

using System.Xml.Linq;
using WpfWebGpu.Services;
using Xunit;

namespace WpfWebGpu.Tests;

public sealed class ApplicationHostConfigTests
{
    private const string Installation = """
        <?xml version="1.0" encoding="UTF-8"?>
        <configuration>
            <system.webServer><httpCompression directory="%SystemDrive%\inetpub\temp" /></system.webServer>
            <system.applicationHost>
                <applicationPools><add name="AppPool" /></applicationPools>
                <sites>
                    <site name="Default Web Site" id="1">
                        <application path="/">
                            <virtualDirectory path="/" physicalPath="%SystemDrive%\inetpub\wwwroot" />
                        </application>
                        <application path="/Web" applicationPool="AppPool">
                            <virtualDirectory path="/" physicalPath="C:\Program Files\Product\Web" />
                        </application>
                        <bindings><binding protocol="http" bindingInformation="*:80:" /></bindings>
                    </site>
                </sites>
            </system.applicationHost>
        </configuration>
        """;

    private static string? Local(string windows) =>
        windows.StartsWith(@"C:\Program Files\Product", StringComparison.OrdinalIgnoreCase) ? "/opt/product/app" + windows[@"C:\Program Files\Product".Length..].Replace('\\', '/') : null;

    [Fact]
    public void The_installation_s_folders_are_this_system_s_and_its_pages_applications()
    {
        string xml = IisConfiguration.ApplicationHostConfig(Installation, Local, new[] { ("/Viewer", "/opt/product/web/viewer") });
        var doc = XDocument.Parse(xml);
        var dirs = doc.Descendants("virtualDirectory").Select(v => (string?)v.Attribute("physicalPath")).ToList();
        Assert.Contains("/opt/product/app/Web", dirs);
        Assert.Contains(@"%SystemDrive%\inetpub\wwwroot", dirs);   // not the installation's: as it was
        Assert.Contains("/opt/product/web/viewer", dirs);
        Assert.NotNull(doc.Descendants("httpCompression").SingleOrDefault());   // every other section kept
        Assert.Contains(doc.Descendants("add"), a => (string?)a.Attribute("name") == "DefaultAppPool");

        // as the web server reads it: what it serves is the installation's
        var served = IisConfiguration.Parse(xml, p => p.StartsWith('/') ? p : null);
        Assert.Equal(new[] { "/Web", "/Viewer" }, served.Sites.Single().Applications.Where(a => a.VirtualDirectories.Any(v => v.PhysicalPath != null)).Select(a => a.Path));
    }

    [Fact]
    public void An_upgrade_keeps_the_machine_s_bindings_and_brings_its_applications()
    {
        string machine = IisConfiguration.ApplicationHostConfig(Installation, Local, Array.Empty<(string, string)>());
        // the machine's own: an HTTPS binding a setup added, a setting changed
        var doc = XDocument.Parse(machine);
        var site = doc.Descendants("site").Single();
        site.SetAttributeValue("serverAutoStart", "false");
        site.Element("bindings")!.Add(new XElement("binding", new XAttribute("protocol", "https"), new XAttribute("bindingInformation", "*:443:")));
        // the upgrade: an application more, a pool more
        string upgrade = IisConfiguration.ApplicationHostConfig(Installation.Replace("</sites>", "</sites>").Replace(
            "<bindings>", """<application path="/New"><virtualDirectory path="/" physicalPath="C:\Program Files\Product\New" /></application><bindings>""")
            .Replace("""<add name="AppPool" />""", """<add name="AppPool" /><add name="NewPool" />"""), Local, Array.Empty<(string, string)>());

        var merged = XDocument.Parse(IisConfiguration.MergeApplicationHost(upgrade, doc.ToString()));
        var s = merged.Descendants("site").Single();
        Assert.Equal("false", (string?)s.Attribute("serverAutoStart"));
        Assert.Equal(new[] { "http", "https" }, s.Descendants("binding").Select(b => (string?)b.Attribute("protocol")));
        Assert.Equal(new[] { "/", "/Web", "/New" }, s.Elements("application").Select(a => (string?)a.Attribute("path")));
        Assert.Equal(new[] { "AppPool", "NewPool" }, merged.Descendants("applicationPools").Single().Elements("add").Select(a => (string?)a.Attribute("name")));
    }
}
