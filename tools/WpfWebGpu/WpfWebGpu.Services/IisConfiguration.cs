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

    /// <summary>The machine's: WPFWEBGPU_IIS, else iis.json in the machine's state (WPFRUN_STATE).</summary>
    public static string? DefaultPath()
    {
        if (Environment.GetEnvironmentVariable("WPFWEBGPU_IIS") is { Length: > 0 } explicitPath) return explicitPath;
        return Environment.GetEnvironmentVariable("WPFRUN_STATE") is { Length: > 0 } state ? Path.Combine(state, FileName) : null;
    }

    public static IisConfiguration Load(string? path)
    {
        if (path == null || !File.Exists(path)) return new IisConfiguration();
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
