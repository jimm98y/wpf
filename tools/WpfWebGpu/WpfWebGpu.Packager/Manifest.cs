// The package manifest (*.wpfpkg.json): what to package and how. The scanner writes a first one from an
// installation, the user confirms and edits it (the Packager app, or by hand), and every build reads it --
// so a package is made again the same way from the same manifest.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace WpfWebGpu.Packager;

public sealed class PackageManifest
{
    public const string Extension = ".wpfpkg.json";

    /// <summary>The installation: its Program Files folder, the ProgramData folders it keeps its data in
    /// (each copied as a folder of that name into ProgramData), and regedit exports of its registry.</summary>
    public InputsSection Inputs { get; set; } = new();
    public ProductSection Product { get; set; } = new();
    public List<AppEntry> Apps { get; set; } = new();
    public List<ServiceEntry> Services { get; set; } = new();
    /// <summary>Windows paths the installation's registry names, and what they become where it is installed.</summary>
    public List<PathMapping> PathMappings { get; set; } = new();
    /// <summary>Files left out of the package: globs relative to the installation or a ProgramData folder.</summary>
    public List<string> Exclude { get; set; } = new() { "**/Logs/**", "**/*.dmp" };
    /// <summary>ProgramData files left out of the phone, headset and browser apps besides: what a machine
    /// records as it runs (a historian's data), which an app on its own has no use for.</summary>
    public List<string> MobileExclude { get; set; } = new();
    public OutputSection Output { get; set; } = new();
    public SigningSection Signing { get; set; } = new();
    /// <summary>The SQL Server the installation brings for its databases: an optional part of each desktop
    /// installer, chosen unless the person installing leaves it out (SqlServer.SqlServerPacks).</summary>
    public SqlServerSection SqlServer { get; set; } = new();

    public static PackageManifest Load(string path)
    {
        if (!File.Exists(path)) throw new PackagerError($"no such manifest: {path}");
        var manifest = JsonSerializer.Deserialize(File.ReadAllText(path), ManifestJson.Default.PackageManifest)
            ?? throw new PackagerError($"{path}: empty manifest");
        manifest.Resolve(Path.GetDirectoryName(Path.GetFullPath(path))!);
        return manifest;
    }

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, ManifestJson.Default.PackageManifest));

    // input paths may be written relative to the manifest
    private void Resolve(string dir)
    {
        string Full(string p) => Path.GetFullPath(p, dir);
        Inputs.InstallDir = Full(Inputs.InstallDir);
        Inputs.ProgramData = Inputs.ProgramData.Select(Full).ToList();
        Inputs.Registry = Inputs.Registry.Select(Full).ToList();
        if (Inputs.IisConfig.Length > 0) Inputs.IisConfig = Full(Inputs.IisConfig);
        if (Output.Directory.Length > 0) Output.Directory = Full(Output.Directory);
    }
}

public sealed class InputsSection
{
    public string InstallDir { get; set; } = "";
    public List<string> ProgramData { get; set; } = new();
    public List<string> Registry { get; set; } = new();
    /// <summary>The installation's machine's IIS configuration (applicationHost.config), where it serves web
    /// applications of the installation: the sites the desktop packages serve.</summary>
    public string IisConfig { get; set; } = "";
}

public sealed class ProductSection
{
    public string Name { get; set; } = "";
    public string Version { get; set; } = "1.0.0";
    public string Publisher { get; set; } = "";
    /// <summary>Reverse-DNS prefix of the apps' ids (com.example: com.example.app).</summary>
    public string IdPrefix { get; set; } = "com.example";
    /// <summary>The product's icon: an .exe whose icon it is, an .ico or a .png; relative to the installation.</summary>
    public string Icon { get; set; } = "";
    /// <summary>"windows": every program of the product is told it runs on Windows (WPFWEBGPU_OS_IDENTITY) -- a
    /// product written for Windows only, whose programs check for it (a licence read through WMI, a service that
    /// refuses to start elsewhere). Empty: the system's own answers.</summary>
    public string OsIdentity { get; set; } = "";
}

[JsonConverter(typeof(JsonStringEnumConverter<AppKind>))]
public enum AppKind { Gui, Console, Service }

public sealed class AppEntry
{
    /// <summary>The .exe, relative to the installation, with '/' separators.</summary>
    public string Exe { get; set; } = "";
    public string Name { get; set; } = "";
    public AppKind Kind { get; set; }
    /// <summary>Shipped as an app of its own on iOS/iPad, Android, Quest and Vision Pro.</summary>
    public bool Mobile { get; set; }
    /// <summary>Built for the browser and served by the desktop installations.</summary>
    public bool Web { get; set; }
    /// <summary>Where the desktop installations serve its page (a URL path on the machine's web server, port 80);
    /// empty: /webassembly/&lt;its name in lower case&gt;.</summary>
    public string WebPath { get; set; } = "";
    /// <summary>A desktop shortcut / Start-menu / Applications entry.</summary>
    public bool Shortcut { get; set; }
    /// <summary>Why it may not run off Windows (32-bit only, mixed-mode C++/CLI, native code); informative.</summary>
    public List<string> Warnings { get; set; } = new();
}

[JsonConverter(typeof(JsonStringEnumConverter<StartType>))]
public enum StartType { Auto, Manual, Disabled }

public sealed class ServiceEntry
{
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Exe { get; set; } = "";
    public List<string> Args { get; set; } = new();
    public StartType Start { get; set; } = StartType.Auto;
    public List<string> DependsOn { get; set; } = new();
    /// <summary>Its environment besides the installation's (WPFWEBGPU_OS_IDENTITY=windows for a program that refuses to
    /// start anywhere but on Windows: its own checks are then answered as Windows would answer them).</summary>
    public Dictionary<string, string> Environment { get; set; } = new();
    /// <summary>The account it runs as, as its Windows service does (its ObjectName): LocalSystem (root), or
    /// NetworkService / LocalService -- machine accounts the installers make, which the stack takes for Windows' own.</summary>
    public ServiceAccount Account { get; set; } = ServiceAccount.LocalSystem;
    /// <summary>Its description, as the service manager shows it.</summary>
    public string Description { get; set; } = "";
    /// <summary>An automatic service started a little after the others (DelayedAutostart).</summary>
    public bool DelayedAutoStart { get; set; }
    /// <summary>The account as Windows names it (ObjectName: LocalSystem, NT AUTHORITY\NetworkService, NT SERVICE\Name),
    /// what the service manager reports; Account is what runs it here.</summary>
    public string StartName { get; set; } = "";
    /// <summary>Who may query, start, stop or change it: its security descriptor (the service key's Security value),
    /// base64; empty for Windows' default (administrators and the system control it, users query it).</summary>
    public string Security { get; set; } = "";
    /// <summary>Started again when it fails, after this many seconds: its recovery actions (the key's FailureActions)
    /// restart it. 0: it is not, as a service without recovery actions is not on Windows.</summary>
    public int RestartAfterFailure { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter<ServiceAccount>))]
public enum ServiceAccount { LocalSystem, NetworkService, LocalService }

public sealed class PathMapping
{
    /// <summary>The Windows path as the installation's registry has it (C:\Program Files\Vendor\Product).</summary>
    public string From { get; set; } = "";
    /// <summary>What it becomes: a token (PathTokens) followed by an optional relative part.</summary>
    public string To { get; set; } = "";
}

[JsonConverter(typeof(JsonStringEnumConverter<Target>))]
public enum Target { Windows, MacOS, Linux, Android, Quest, IOS, VisionOS }

public sealed class OutputSection
{
    public string Directory { get; set; } = "";
    public List<Target> Targets { get; set; } = new();
    /// <summary>Ahead-of-time compiled builds with the real toolchains, where they are installed.</summary>
    public bool Aot { get; set; }
    /// <summary>Packages for testing: nothing compressed, made as fast as they can be written.</summary>
    public bool Fast { get; set; }
}

public sealed class SqlServerSection
{
    /// <summary>Whether the desktop installers carry SQL Server at all (each offers it, chosen, to install).</summary>
    public bool Include { get; set; }
    /// <summary>The TCP port it listens on (the default instance's).</summary>
    public int Port { get; set; } = 1433;
    /// <summary>The instance Windows installs, named as the installation's configuration names it (the scanner takes
    /// it from the registry's MSSQL$&lt;instance&gt; service).</summary>
    public string Instance { get; set; } = "SQLEXPRESS";
}

public sealed class SigningSection
{
    public string AndroidKeystore { get; set; } = "";
    public string AndroidKeyAlias { get; set; } = "";
    public string AppleCertificate { get; set; } = "";
    /// <summary>The keychain identity a device .ipa is signed with ("Apple Distribution: Name (TEAM)").</summary>
    public string AppleSigningIdentity { get; set; } = "";
    public string AppleProvisioningProfile { get; set; } = "";
    public string DeveloperIdCertificate { get; set; } = "";
    /// <summary>The keychain identity a macOS package is signed with ("Developer ID Installer: Name (TEAM)").</summary>
    public string DeveloperIdInstaller { get; set; } = "";
    public string NotaryKey { get; set; } = "";
    public string AuthenticodeCertificate { get; set; } = "";
    // passwords are never stored: WPFPKG_*_PASSWORD environment variables, or asked for
}

/// <summary>The tokens a staged path is written with, resolved where the package is installed.</summary>
public static class PathTokens
{
    public const string InstallDir = "${WPFRUN_INSTALLDIR}";
    public const string ProgramData = "${WPFRUN_PROGRAMDATA}";
    /// <summary>On a phone, headset or browser: the one app's own folder.</summary>
    public const string AppDir = "${WPFRUN_APPDIR}";
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true)]
[JsonSerializable(typeof(PackageManifest))]
internal sealed partial class ManifestJson : JsonSerializerContext;
