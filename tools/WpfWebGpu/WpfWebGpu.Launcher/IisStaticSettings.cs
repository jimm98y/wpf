// What a folder's web.config files say about the files IIS's static file handler serves from it, as IIS merges them
// from the virtual directory's folder down to the file's: the custom headers of every response (httpProtocol/
// customHeaders) and the content types of extensions (staticContent/mimeMap), each list's add, remove and clear in
// order. And what IIS's request filtering refuses whatever the configuration, by default: its configuration files
// and source files' extensions, and its applications' own folders (bin, App_Data and the like).

using System.Collections.Concurrent;
using System.Xml.Linq;

namespace WpfWebGpu;

internal sealed class StaticSettings
{
    public List<(string Name, string Value)> Headers { get; } = new();
    public Dictionary<string, string> Types { get; } = new(StringComparer.OrdinalIgnoreCase);

    private static readonly ConcurrentDictionary<string, (DateTime Stamp, XElement? Server)> s_files = new(StringComparer.Ordinal);

    // requestFiltering's defaults (applicationHost.config): <fileExtensions> denied, <hiddenSegments>
    private static readonly HashSet<string> s_deniedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".asax", ".ascx", ".master", ".skin", ".browser", ".sitemap", ".config", ".cs", ".csproj", ".vb", ".vbproj", ".webinfo",
        ".licx", ".resx", ".resources", ".mdb", ".vjsproj", ".java", ".jsl", ".ldb", ".dsdgm", ".ssdgm", ".lsad", ".ssmap", ".cd",
        ".dsprototype", ".lsaprototype", ".sdm", ".sdmDocument", ".mdf", ".ldf", ".ad", ".dd", ".ldd", ".sd", ".adprototype",
        ".lddprototype", ".exclude", ".refresh", ".compiled", ".msgx", ".vsdisco", ".rules",
    };
    private static readonly HashSet<string> s_hiddenSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "web.config", "bin", "App_code", "App_GlobalResources", "App_LocalResources", "App_WebReferences", "App_Data", "App_Browsers",
    };

    /// <summary>Whether request filtering refuses the path (relative to the virtual directory) before anything serves it.</summary>
    public static bool Refused(string relative)
    {
        string[] segments = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(s_hiddenSegments.Contains)) return true;
        return segments.Length > 0 && s_deniedExtensions.Contains(Path.GetExtension(segments[^1]));
    }

    /// <summary>The settings for <paramref name="file"/> under the virtual directory's <paramref name="root"/>.</summary>
    public static StaticSettings For(string root, string file)
    {
        var settings = new StaticSettings();
        var folders = new List<string> { root };
        string? dir = Directory.Exists(file) ? file : Path.GetDirectoryName(file);
        var below = new List<string>();
        for (string? d = dir; d != null && d.Length > root.Length && d.StartsWith(root, StringComparison.Ordinal); d = Path.GetDirectoryName(d)) below.Add(d);
        below.Reverse();
        folders.AddRange(below);
        foreach (string folder in folders)
        {
            if (ServerOf(Path.Combine(folder, "web.config")) is not { } server) continue;
            Apply(server.Element("httpProtocol")?.Element("customHeaders"), settings.Headers,
                e => ((string?)e.Attribute("name") ?? "", (string?)e.Attribute("value") ?? ""), (list, name) => list.RemoveAll(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase)));
            foreach (var e in server.Element("staticContent")?.Elements() ?? Enumerable.Empty<XElement>())
            {
                string ext = (string?)e.Attribute("fileExtension") ?? "";
                switch (e.Name.LocalName)
                {
                    case "clear": settings.Types.Clear(); break;
                    case "remove": settings.Types.Remove(ext); break;
                    case "mimeMap" when ext.Length > 0: settings.Types[ext] = (string?)e.Attribute("mimeType") ?? "application/octet-stream"; break;
                }
            }
        }
        return settings;
    }

    private static void Apply(XElement? list, List<(string Name, string Value)> into, Func<XElement, (string, string)> item, Action<List<(string Name, string Value)>, string> remove)
    {
        foreach (var e in list?.Elements() ?? Enumerable.Empty<XElement>())
        {
            switch (e.Name.LocalName)
            {
                case "clear": into.Clear(); break;
                case "remove": remove(into, (string?)e.Attribute("name") ?? ""); break;
                case "add":
                    var (name, value) = item(e);
                    if (name.Length == 0) break;
                    remove(into, name);
                    into.Add((name, value));
                    break;
            }
        }
    }

    // a web.config's system.webServer, read again when the file changes
    private static XElement? ServerOf(string webConfig)
    {
        DateTime stamp = File.Exists(webConfig) ? File.GetLastWriteTimeUtc(webConfig) : DateTime.MinValue;
        if (s_files.TryGetValue(webConfig, out var cached) && cached.Stamp == stamp) return cached.Server;
        XElement? server = null;
        if (stamp != DateTime.MinValue)
        {
            try { server = XDocument.Load(webConfig).Root?.Element("system.webServer"); }
            catch (Exception e) when (e is System.Xml.XmlException or IOException or UnauthorizedAccessException) { }
        }
        s_files[webConfig] = (stamp, server);
        return server;
    }
}
