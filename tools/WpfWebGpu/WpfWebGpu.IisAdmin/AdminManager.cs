// IIS's configuration system (ahadmin's AppHostAdminManager and AppHostWritableAdminManager), as a COM server run here: a
// client -- Microsoft.Web.Administration's ServerManager, a program's own interop -- creates it by its CLSID and asks it for a
// section at a configuration path, reads and changes it, and commits. The paths are IIS's:
//
//   MACHINE/WEBROOT/APPHOST[/site[/application]]   applicationHost.config (a deeper path: its <location path="..."> element)
//   MACHINE/REDIRECTION                              redirection.config
//   MACHINE/WEBROOT                                  the .NET Framework's root web.config (none here: empty)
//
// The files are where Windows keeps them, C:\Windows\System32\inetsrv\config, which is the machine's state here (each
// installation's, as its registry is), so a program that reads applicationHost.config by its path reads the same file.
// Each admin manager reads the files when it first needs them and writes the ones it changed when it commits, as
// Windows' does; the web server (`wpfrun iis`) serves the sites applicationHost.config has.

using System.Runtime.InteropServices;
using System.Xml.Linq;
using WpfWebGpu.ComInterop;

namespace WpfWebGpu.IisAdmin;

/// <summary>A configuration file, as one admin manager reads and changes it.</summary>
internal sealed class ConfigFile
{
    public readonly string ConfigPath;
    /// <summary>Where it is on this system; null for a file this system has none of (the root web.config).</summary>
    public readonly string? FilePath;
    public readonly XDocument Document;
    public bool Dirty;
    /// <summary>Each XML element's element object, the same each time it is asked for.</summary>
    public readonly System.Runtime.CompilerServices.ConditionalWeakTable<XElement, AppHostElement> Elements = new();

    public ConfigFile(string configPath, string? filePath)
    {
        ConfigPath = configPath;
        FilePath = filePath;
        Document = filePath != null && System.IO.File.Exists(filePath)
            ? XDocument.Load(filePath, LoadOptions.PreserveWhitespace)
            : new XDocument(new XElement("configuration"));
        if (Document.Root == null || Document.Root.Name.LocalName != "configuration") Document.Root?.ReplaceWith(new XElement("configuration"));
    }

    public XElement Root => Document.Root!;

    public void Save()
    {
        if (!Dirty || FilePath == null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        // written beside, then moved over it: a reader (the web server watching it) never sees half a file
        string temp = FilePath + ".new";
        using (var w = System.Xml.XmlWriter.Create(temp, new System.Xml.XmlWriterSettings { Indent = !HasWhitespace(), OmitXmlDeclaration = Document.Declaration == null, Encoding = new System.Text.UTF8Encoding(false) }))
            Document.Save(w);
        File.Move(temp, FilePath, overwrite: true);
        Dirty = false;
    }

    private bool HasWhitespace() => Document.DescendantNodes().OfType<XText>().Any(t => string.IsNullOrWhiteSpace(t.Value));
}

/// <summary>AppHostWritableAdminManager: IIS's configuration, read and written.</summary>
[Guid("2B72133B-3F5B-4602-8952-803546CE3344"), ComVisible(true)]
public class AppHostWritableAdminManager : IAppHostWritableAdminManager, IAppHostAdminManager, IComServerObject
{
    internal const string AppHost = "MACHINE/WEBROOT/APPHOST", WebRoot = "MACHINE/WEBROOT", Redirection = "MACHINE/REDIRECTION";

    private readonly Dictionary<string, ConfigFile> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, object?> _metadata = new(StringComparer.OrdinalIgnoreCase);
    private string _commitPath = AppHost;

    /// <summary>For tests: the folder of the configuration files, in place of the machine's.</summary>
    internal static string? FolderOverride;

    /// <summary>The machine's IIS configuration folder: C:\Windows\System32\inetsrv\config as this system has it.</summary>
    internal static string Folder => FolderOverride
        ?? Environment.GetEnvironmentVariable("WPFWEBGPU_IIS_CONFIG")
        ?? WindowsPaths.Disk(@"C:\Windows\System32\inetsrv\config")!;

    internal ConfigFile File(string configPath)
    {
        // the file a path is in: applicationHost.config for APPHOST and below
        string key = configPath.StartsWith(AppHost, StringComparison.OrdinalIgnoreCase) ? AppHost
            : configPath.Equals(Redirection, StringComparison.OrdinalIgnoreCase) ? Redirection
            : configPath.Equals(WebRoot, StringComparison.OrdinalIgnoreCase) ? WebRoot
            : throw HResults.Error(HResults.E_INVALIDARG, $"Invalid configuration path: {configPath}");
        if (_files.TryGetValue(key, out var f)) return f;
        string? file = key switch
        {
            AppHost => Path.Combine(Folder, "applicationHost.config"),
            Redirection => Path.Combine(Folder, "redirection.config"),
            _ => null,
        };
        return _files[key] = new ConfigFile(key, file);
    }

    public IAppHostElement GetAdminSection(string bstrSectionName, string bstrPath)
    {
        string path = (bstrPath ?? AppHost).Trim('/');
        var file = File(path);
        // a path below APPHOST: the section in its <location path="...">
        string? location = path.Length > AppHost.Length && path.StartsWith(AppHost + "/", StringComparison.OrdinalIgnoreCase) ? path[(AppHost.Length + 1)..] : null;
        return Section(file, bstrSectionName, location);
    }

    internal static AppHostElement Section(ConfigFile file, string sectionName, string? location)
    {
        var root = new AppHostElement(file, ElementDef.Unschematized("configuration"), file.Root, null);
        AppHostElement parent = root;
        XElement? node = file.Root;
        if (location != null)
        {
            XElement? loc = file.Root.Elements("location").FirstOrDefault(l => string.Equals((string?)l.Attribute("path"), location, StringComparison.OrdinalIgnoreCase));
            var locDef = ElementDef.Unschematized("location");
            parent = new AppHostElement(file, locDef, loc, root, loc == null ? location : null);
            node = loc;
        }
        string[] parts = sectionName.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            node = node?.Elements().FirstOrDefault(x => AppHostElement.Same(x, parts[i]));
            ElementDef def = i == parts.Length - 1
                ? IisSchema.Section(sectionName) ?? (node != null ? AppHostElement.Unschematized(node) : ElementDef.Unschematized(parts[i]))
                : ElementDef.Unschematized(parts[i]);
            parent = new AppHostElement(file, def, node, parent);
        }
        return parent;
    }

    public object? GetMetadata(string bstrMetadataType) => _metadata.TryGetValue(bstrMetadataType, out var v) ? v : null;

    // pathMapper, expandEnvironmentStrings, changeHandler and the rest: kept, as the configuration system keeps them
    public void SetMetadata(string bstrMetadataType, object Value) => _metadata[bstrMetadataType] = Value;

    public IAppHostConfigManager get_ConfigManager() => new AppHostConfigManager(this);

    public void CommitChanges()
    {
        foreach (var f in _files.Values) f.Save();
    }

    public string get_CommitPath() => _commitPath;
    public void set_CommitPath(string value) => _commitPath = string.IsNullOrEmpty(value) ? AppHost : value;
}

/// <summary>AppHostAdminManager: the same, read only (a client that commits through it is refused, as Windows' is).</summary>
[Guid("228FB8F7-FB53-4FD5-8C7B-FF59DE606C5B"), ComVisible(true)]
public sealed class AppHostAdminManager : AppHostWritableAdminManager
{
}

public sealed class AppHostConfigManager : IAppHostConfigManager, IComServerObject
{
    private readonly AppHostWritableAdminManager _manager;
    internal AppHostConfigManager(AppHostWritableAdminManager manager) => _manager = manager;

    public IAppHostConfigFile GetConfigFile(string bstrConfigPath) => new AppHostConfigFile(_manager.File(bstrConfigPath));
    public string GetUniqueConfigPath(string bstrConfigPath) => bstrConfigPath;
}

public sealed class AppHostConfigFile : IAppHostConfigFile, IComServerObject
{
    private readonly ConfigFile _file;
    internal AppHostConfigFile(ConfigFile file) => _file = file;

    public string get_ConfigPath() => _file.ConfigPath;
    public string get_FilePath() => _file.FilePath ?? "";
    public IAppHostConfigLocationCollection get_Locations() => new AppHostConfigLocationCollection(_file);
    public IAppHostElement GetAdminSection(string bstrSectionName, string bstrConfigPath)
    {
        string path = (bstrConfigPath ?? _file.ConfigPath).Trim('/');
        string? location = path.Length > _file.ConfigPath.Length ? path[(_file.ConfigPath.Length + 1)..] : null;
        return AppHostWritableAdminManager.Section(_file, bstrSectionName, location);
    }
    public object? GetMetadata(string bstrMetadataType) => null;
    public void SetMetadata(string bstrMetadataType, object Value) { }
    public void ClearInvalidSections() { }
    public IAppHostSectionGroup get_RootSectionGroup() => throw new NotImplementedException("configSections");
}

public sealed class AppHostConfigLocationCollection : IAppHostConfigLocationCollection, IComServerObject
{
    private readonly ConfigFile _file;
    internal AppHostConfigLocationCollection(ConfigFile file) => _file = file;

    private List<XElement> Locations() => _file.Root.Elements("location").ToList();

    public uint get_Count() => (uint)Locations().Count;

    public IAppHostConfigLocation get_Item(object cIndex)
    {
        var list = Locations();
        return HResults.Index(cIndex) switch
        {
            int i when i >= 0 && i < list.Count => new AppHostConfigLocation(_file, list[i]),
            string path when list.FirstOrDefault(l => string.Equals((string?)l.Attribute("path"), path, StringComparison.OrdinalIgnoreCase)) is { } l => new AppHostConfigLocation(_file, l),
            _ => throw HResults.Error(HResults.E_INVALIDARG, $"no location {cIndex}"),
        };
    }

    public IAppHostConfigLocation AddLocation(string bstrLocationPath)
    {
        var l = new XElement("location", new XAttribute("path", bstrLocationPath));
        _file.Root.Add(l);
        _file.Dirty = true;
        return new AppHostConfigLocation(_file, l);
    }

    public void DeleteLocation(object cIndex)
    {
        var list = Locations();
        XElement? l = HResults.Index(cIndex) switch
        {
            int i when i >= 0 && i < list.Count => list[i],
            string path => list.FirstOrDefault(x => string.Equals((string?)x.Attribute("path"), path, StringComparison.OrdinalIgnoreCase)),
            _ => null,
        };
        if (l == null) throw HResults.Error(HResults.E_INVALIDARG, $"no location {cIndex}");
        l.Remove();
        _file.Dirty = true;
    }

    public void RenameLocation(object varIndex, string bstrLocationPath)
    {
        var l = (AppHostConfigLocation)get_Item(varIndex);
        l.Node.SetAttributeValue("path", bstrLocationPath);
        _file.Dirty = true;
    }
}

public sealed class AppHostConfigLocation : IAppHostConfigLocation, IComServerObject
{
    private readonly ConfigFile _file;
    internal readonly XElement Node;
    internal AppHostConfigLocation(ConfigFile file, XElement node) { _file = file; Node = node; }

    public string get_Path() => (string?)Node.Attribute("path") ?? "";
    public uint get_Count() => (uint)Node.Elements().Count();
    public IAppHostElement get_Item(object cIndex)
    {
        var list = Node.Elements().ToList();
        return HResults.Index(cIndex) switch
        {
            int i when i >= 0 && i < list.Count => AppHostWritableAdminManager.Section(_file, list[i].Name.LocalName, get_Path()),
            string name => AppHostWritableAdminManager.Section(_file, name, get_Path()),
            _ => throw HResults.Error(HResults.E_INVALIDARG, $"no section {cIndex}"),
        };
    }
    public IAppHostElement AddConfigSection(string bstrSectionName)
    {
        var section = AppHostWritableAdminManager.Section(_file, bstrSectionName, get_Path());
        section.EnsureNode();
        return section;
    }
    public void DeleteConfigSection(object cIndex)
    {
        if (get_Item(cIndex) is AppHostElement e && e.Node != null) { e.Node.Remove(); _file.Dirty = true; }
    }
}
