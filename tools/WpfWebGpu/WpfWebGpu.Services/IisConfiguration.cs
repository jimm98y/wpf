// IIS's configuration off Windows: the sites of the machine's web server, as applicationHost.config has them -- each
// site's bindings, its applications (a path, the application pool that runs it) and their virtual directories (a path
// and the folder it serves) -- and the application pools (the account each runs as, whether it starts with the
// server). A package takes it from the installation's machine (its applicationHost.config) with each folder made the
// installation's own, and installs it into the machine's state beside the services (iis.json); the web server
// (`wpfrun iis`) serves from it. A folder that is not the installation's (IIS's own wwwroot) is left out.

using System.Text.Json;
using System.Xml.Linq;

namespace WpfWebGpu.Services;

public sealed class IisConfiguration
{
    public List<IisSite> Sites { get; set; } = new();
    public List<IisPool> Pools { get; set; } = new();

    public const string FileName = "iis.json";

    private static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true, PropertyNameCaseInsensitive = true,
    };

    /// <summary>The machine's applicationHost.config in its state: where Windows keeps it (C:\Windows\System32\inetsrv\config),
    /// as the stack maps that folder, which IIS's configuration system (WpfWebGpu.IisAdmin) reads and writes.</summary>
    public const string ApplicationHostPath = "inetsrv/config/applicationHost.config";

    /// <summary>The machine's: WPFWEBGPU_IIS, else the machine's applicationHost.config, else iis.json (an installation
    /// made before IIS's configuration was the machine's own) in the machine's state (WPFRUN_STATE).</summary>
    public static string? DefaultPath()
    {
        if (Environment.GetEnvironmentVariable("WPFWEBGPU_IIS") is { Length: > 0 } explicitPath) return explicitPath;
        if (Environment.GetEnvironmentVariable("WPFRUN_STATE") is not { Length: > 0 } state) return null;
        string config = Path.Combine(state, ApplicationHostPath);
        return File.Exists(config) ? config : Path.Combine(state, FileName);
    }

    /// <summary>A configuration file: an applicationHost.config (its folders this system's, as the installation put them:
    /// an absolute path; any other not the installation's), or the json of an earlier installation.</summary>
    public static IisConfiguration Load(string? path)
    {
        if (path == null || !File.Exists(path)) return new IisConfiguration();
        if (path.EndsWith(".config", StringComparison.OrdinalIgnoreCase))
        {
            try { return Parse(File.ReadAllText(path), p => p.StartsWith('/') ? p : null); }
            catch (Exception e) when (e is System.Xml.XmlException or IOException or UnauthorizedAccessException) { return new IisConfiguration(); }
        }
        try { return JsonSerializer.Deserialize<IisConfiguration>(File.ReadAllText(path), s_json) ?? new IisConfiguration(); }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { return new IisConfiguration(); }
    }

    public string ToJson() => JsonSerializer.Serialize(this, s_json);

    /// <summary>
    /// The sites and pools of an applicationHost.config, each virtual directory's folder given by
    /// <paramref name="localFolder"/> (the Windows path, its environment variables expanded, to this machine's folder;
    /// null where it is not the installation's).
    /// </summary>
    public static IisConfiguration Parse(string applicationHostConfig, Func<string, string?> localFolder)
    {
        var root = XDocument.Parse(applicationHostConfig).Root!;
        var host = root.Element("system.applicationHost");
        var config = new IisConfiguration();
        var defaults = host?.Element("applicationPools")?.Element("applicationPoolDefaults");
        foreach (var p in host?.Element("applicationPools")?.Elements("add") ?? Enumerable.Empty<XElement>())
        {
            var model = p.Element("processModel") ?? defaults?.Element("processModel");
            config.Pools.Add(new IisPool
            {
                Name = (string?)p.Attribute("name") ?? "",
                Identity = (string?)model?.Attribute("identityType") ?? "ApplicationPoolIdentity",
                AutoStart = ((string?)p.Attribute("autoStart") ?? (string?)defaults?.Attribute("autoStart") ?? "true").Equals("true", StringComparison.OrdinalIgnoreCase),
            });
        }
        foreach (var s in host?.Element("sites")?.Elements("site") ?? Enumerable.Empty<XElement>())
        {
            var site = new IisSite { Name = (string?)s.Attribute("name") ?? "", Id = (int?)s.Attribute("id") ?? 0 };
            foreach (var b in s.Element("bindings")?.Elements("binding") ?? Enumerable.Empty<XElement>())
            {
                // bindingInformation: address:port:host
                string protocol = (string?)b.Attribute("protocol") ?? "http";
                if (protocol is not ("http" or "https")) continue;
                string[] info = ((string?)b.Attribute("bindingInformation") ?? "*:80:").Split(':');
                if (info.Length < 2 || !int.TryParse(info[^2], out int port)) continue;
                site.Bindings.Add(new IisBinding { Protocol = protocol, Address = string.Join(':', info[..^2]), Port = port, Host = info[^1] });
            }
            foreach (var a in s.Elements("application"))
            {
                var app = new IisApplication { Path = (string?)a.Attribute("path") ?? "/", Pool = (string?)a.Attribute("applicationPool") ?? "DefaultAppPool" };
                foreach (var v in a.Elements("virtualDirectory"))
                {
                    string windows = (string?)v.Attribute("physicalPath") ?? "";
                    app.VirtualDirectories.Add(new IisVirtualDirectory
                    {
                        Path = (string?)v.Attribute("path") ?? "/",
                        WindowsPath = windows,
                        PhysicalPath = localFolder(Expand(windows)),
                    });
                }
                site.Applications.Add(app);
            }
            config.Sites.Add(site);
        }
        return config;
    }

    /// <summary>
    /// The machine's applicationHost.config made from the installation's: each virtual directory's folder this system's
    /// where <paramref name="localFolder"/> knows it (the installation's, its ProgramData's), and each of
    /// <paramref name="extraApplications"/> (path, folder) an application of the site serving port 80 (IIS's Default Web
    /// Site, made where there is none). Every other section as it is.
    /// </summary>
    public static string ApplicationHostConfig(string? installationConfig, Func<string, string?> localFolder, IEnumerable<(string Path, string Folder)> extraApplications)
    {
        var doc = installationConfig != null ? XDocument.Parse(installationConfig, LoadOptions.PreserveWhitespace) : new XDocument(new XElement("configuration"));
        var root = doc.Root!;
        foreach (var v in root.Descendants("virtualDirectory"))
            if ((string?)v.Attribute("physicalPath") is { Length: > 0 } windows && localFolder(Expand(windows)) is { } local)
                v.SetAttributeValue("physicalPath", local);
        var extras = extraApplications.ToList();
        if (extras.Count > 0)
        {
            var host = Child(root, "system.applicationHost");
            var sites = Child(host, "sites");
            var site = sites.Elements("site").FirstOrDefault(s => s.Element("bindings")?.Elements("binding").Any(b =>
                (string?)b.Attribute("protocol") == "http" && ((string?)b.Attribute("bindingInformation") ?? "").Split(':') is { Length: >= 2 } info && info[^2] == "80") == true);
            if (site == null)
            {
                int id = sites.Elements("site").Select(s => (int?)s.Attribute("id") ?? 0).DefaultIfEmpty(0).Max() + 1;
                site = new XElement("site", new XAttribute("name", "Default Web Site"), new XAttribute("id", id),
                    new XElement("bindings", new XElement("binding", new XAttribute("protocol", "http"), new XAttribute("bindingInformation", "*:80:"))));
                sites.Add(site);
            }
            var pools = Child(host, "applicationPools");
            if (!pools.Elements("add").Any(p => (string?)p.Attribute("name") == "DefaultAppPool"))
                pools.Add(new XElement("add", new XAttribute("name", "DefaultAppPool")));
            foreach (var (path, folder) in extras)
            {
                site.Elements("application").Where(a => string.Equals((string?)a.Attribute("path"), path, StringComparison.OrdinalIgnoreCase)).Remove();
                var app = new XElement("application", new XAttribute("path", path), new XAttribute("applicationPool", "DefaultAppPool"),
                    new XElement("virtualDirectory", new XAttribute("path", "/"), new XAttribute("physicalPath", folder)));
                if (site.Element("bindings") is { } bindings) bindings.AddBeforeSelf(app); else site.Add(app);
            }
        }
        return doc.Declaration != null ? doc.Declaration + Environment.NewLine + root : root.ToString();
    }

    /// <summary>
    /// An upgrade's applicationHost.config merged into the machine's, as a product's setup updates IIS: a site the
    /// machine does not have added, and a site it has given the new version's applications -- its bindings (an HTTPS
    /// one a setup made, with http.sys's certificate) and its own settings kept; an application pool it does not have
    /// added. Everything else is the machine's.
    /// </summary>
    public static string MergeApplicationHost(string upgrade, string machine)
    {
        var mine = XDocument.Parse(machine, LoadOptions.PreserveWhitespace);
        var theirs = XDocument.Parse(upgrade);
        var host = Child(mine.Root!, "system.applicationHost");
        var sites = Child(host, "sites");
        foreach (var s in theirs.Root?.Element("system.applicationHost")?.Element("sites")?.Elements("site") ?? Enumerable.Empty<XElement>())
        {
            var existing = sites.Elements("site").FirstOrDefault(x => string.Equals((string?)x.Attribute("name"), (string?)s.Attribute("name"), StringComparison.OrdinalIgnoreCase));
            if (existing == null) { sites.Add(new XElement(s)); continue; }
            existing.Elements("application").Remove();
            var apps = s.Elements("application").Select(a => new XElement(a)).ToList();
            if (existing.Element("bindings") is { } bindings) bindings.AddBeforeSelf(apps); else existing.Add(apps);
        }
        var pools = Child(host, "applicationPools");
        foreach (var p in theirs.Root?.Element("system.applicationHost")?.Element("applicationPools")?.Elements("add") ?? Enumerable.Empty<XElement>())
            if (!pools.Elements("add").Any(x => string.Equals((string?)x.Attribute("name"), (string?)p.Attribute("name"), StringComparison.OrdinalIgnoreCase)))
                pools.Add(new XElement(p));
        return mine.Declaration != null ? mine.Declaration + Environment.NewLine + mine.Root : mine.Root!.ToString();
    }

    private static XElement Child(XElement parent, string name)
    {
        if (parent.Element(name) is { } e) return e;
        e = new XElement(name);
        parent.Add(e);
        return e;
    }

    // %SystemDrive% and its like, as IIS expands them on the machine the configuration came from
    private static string Expand(string path) => path
        .Replace("%SystemDrive%", "C:", StringComparison.OrdinalIgnoreCase)
        .Replace("%SystemRoot%", @"C:\Windows", StringComparison.OrdinalIgnoreCase)
        .Replace("%windir%", @"C:\Windows", StringComparison.OrdinalIgnoreCase)
        .Replace("%ProgramFiles%", @"C:\Program Files", StringComparison.OrdinalIgnoreCase)
        .Replace("%ProgramData%", @"C:\ProgramData", StringComparison.OrdinalIgnoreCase);
}

public sealed class IisSite
{
    public string Name { get; set; } = "";
    public int Id { get; set; }
    public List<IisBinding> Bindings { get; set; } = new();
    public List<IisApplication> Applications { get; set; } = new();
}

public sealed class IisBinding
{
    public string Protocol { get; set; } = "http";
    /// <summary>The address it listens on: * for every one.</summary>
    public string Address { get; set; } = "*";
    public int Port { get; set; } = 80;
    /// <summary>The host name it answers for: empty for every one.</summary>
    public string Host { get; set; } = "";
}

public sealed class IisApplication
{
    public string Path { get; set; } = "/";
    public string Pool { get; set; } = "DefaultAppPool";
    public List<IisVirtualDirectory> VirtualDirectories { get; set; } = new();
}

public sealed class IisVirtualDirectory
{
    public string Path { get; set; } = "/";
    public string WindowsPath { get; set; } = "";
    /// <summary>The folder it serves here; null where it is not the installation's.</summary>
    public string? PhysicalPath { get; set; }
}

public sealed class IisPool
{
    public string Name { get; set; } = "";
    /// <summary>processModel's identityType: LocalSystem, LocalService, NetworkService, ApplicationPoolIdentity.</summary>
    public string Identity { get; set; } = "ApplicationPoolIdentity";
    public bool AutoStart { get; set; } = true;
}
