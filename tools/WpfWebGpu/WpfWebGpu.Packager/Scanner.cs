// A first manifest from an installation: its apps (every .exe that starts a .NET app), what each is (a
// window app, a console tool, or -- a console app that uses Windows' service host -- a service), what may
// keep one from running off Windows, and the Windows paths its registry names. Suggestions only: the
// user confirms them, and nothing here knows any particular product.

using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WpfWebGpu.Packager;

public static class Scanner
{
    // what a service links to: Windows' ServiceBase, or the generic host's Windows-service lifetime
    private static readonly string[] s_serviceAssemblies =
    {
        "System.ServiceProcess.ServiceController",
        "System.ServiceProcess",
        "Microsoft.Extensions.Hosting.WindowsServices",
    };

    /// <summary>Each service's account as the registry's service key has it (ObjectName), the key found by the
    /// service's name or by its program (ImagePath).</summary>
    internal static void AssignAccounts(PackageManifest manifest, IEnumerable<RegEntry> entries, Action<string>? log)
    {
        const string Services = @"SYSTEM\CurrentControlSet\Services\";
        var keys = new Dictionary<string, (string? ObjectName, string? ImagePath)>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries)
        {
            if (!e.Hive.Equals("HKEY_LOCAL_MACHINE", StringComparison.OrdinalIgnoreCase) || !e.Key.StartsWith(Services, StringComparison.OrdinalIgnoreCase)) continue;
            string name = e.Key[Services.Length..];
            if (name.Contains('\\')) continue;
            keys.TryGetValue(name, out var k);
            if (e.Name.Equals("ObjectName", StringComparison.OrdinalIgnoreCase)) k.ObjectName = e.Value as string;
            else if (e.Name.Equals("ImagePath", StringComparison.OrdinalIgnoreCase)) k.ImagePath = e.Value as string;
            keys[name] = k;
        }
        foreach (var service in manifest.Services)
        {
            string exe = Path.GetFileName(service.Exe);
            var key = keys.TryGetValue(service.Name, out var byName) ? byName
                : keys.Values.FirstOrDefault(k => k.ImagePath?.Contains(exe, StringComparison.OrdinalIgnoreCase) == true);
            if (key.ObjectName is not { Length: > 0 } account) continue;
            string bare = account.Replace(" ", "");
            if (bare.Equals("LocalSystem", StringComparison.OrdinalIgnoreCase) || bare.Equals(@"NTAUTHORITY\SYSTEM", StringComparison.OrdinalIgnoreCase)) service.Account = ServiceAccount.LocalSystem;
            else if (bare.Equals(@"NTAUTHORITY\NetworkService", StringComparison.OrdinalIgnoreCase)) service.Account = ServiceAccount.NetworkService;
            else if (bare.Equals(@"NTAUTHORITY\LocalService", StringComparison.OrdinalIgnoreCase)) service.Account = ServiceAccount.LocalService;
            else log?.Invoke($"service {service.Name} runs as {account} on Windows: run as LocalSystem here (set \"account\" to choose)");
        }
    }

    public static PackageManifest Scan(string installDir, IEnumerable<string>? programData = null, IEnumerable<string>? registry = null, Action<string>? log = null)
    {
        installDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installDir));
        if (!Directory.Exists(installDir)) throw new PackagerError($"no such folder: {installDir}");
        var manifest = new PackageManifest();
        manifest.Inputs.InstallDir = installDir;
        foreach (string p in programData ?? Array.Empty<string>())
        {
            string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(p));
            if (!Directory.Exists(full)) throw new PackagerError($"no such folder: {full}");
            manifest.Inputs.ProgramData.Add(full);
        }
        foreach (string r in registry ?? Array.Empty<string>())
        {
            string full = Path.GetFullPath(r);
            if (!File.Exists(full)) throw new PackagerError($"no such file: {full}");
            manifest.Inputs.Registry.Add(full);
        }

        var folders = new Dictionary<string, FolderInfo>(StringComparer.Ordinal);
        var companies = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        // every executable that starts a .NET app beside it: App.exe, or another name its apphost carries the app
        // under (App_ARM64.exe)
        foreach (string exe in Directory.EnumerateFiles(installDir, "*.exe", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
        {
            string dir = Path.GetDirectoryName(exe)!;
            if (AppHost.AppDll(exe) is not { } dll) continue;
            string name = Path.GetFileNameWithoutExtension(dll);
            string config = Path.ChangeExtension(dll, ".runtimeconfig.json");
            var exePe = PeFile.TryRead(exe);
            if (exePe == null) continue;

            if (!folders.TryGetValue(dir, out var folder)) folders[dir] = folder = FolderInfo.Read(dir);
            var app = new AppEntry
            {
                Exe = Path.GetRelativePath(installDir, exe).Replace('\\', '/'),
                Name = DisplayName(dll, name),
                Kind = exePe.Gui ? AppKind.Gui : IsService(dll, dir) ? AppKind.Service : AppKind.Console,
            };
            app.Shortcut = app.Kind == AppKind.Gui;
            if (exePe.Machine == Machine.I386 || PeFile.TryRead(dll)?.Requires32Bit == true) app.Warnings.Add("32-bit only");
            app.Warnings.AddRange(folder.Warnings);
            if (Frameworks(config) is { Count: > 0 } fw && !fw.Contains("Microsoft.NETCore.App")) app.Warnings.Add("framework: " + string.Join(", ", fw));
            manifest.Apps.Add(app);
            if (Company(dll) is { Length: > 0 } company) companies[company] = companies.GetValueOrDefault(company) + 1;
            log?.Invoke($"{app.Kind,-8} {app.Exe}{(app.Warnings.Count > 0 ? "  [" + string.Join("; ", app.Warnings) + "]" : "")}");
        }
        if (manifest.Apps.Count == 0) throw new PackagerError($"{installDir} holds no .NET app (no .exe starting a <name>.dll with its <name>.runtimeconfig.json).");

        var entries = manifest.Inputs.Registry.SelectMany(r => RegText.Parse(RegText.Read(r))).ToList();
        manifest.PathMappings.AddRange(DetectMappings(entries, installDir));

        // the services: as the registry registers them, where it does; else the programs that use Windows' service host
        var registered = RegisteredServices(manifest, entries, installDir, log);
        if (registered.Count > 0)
        {
            manifest.Services.AddRange(registered);
            foreach (var app in manifest.Apps)
                if (registered.Any(r => r.Exe.Equals(app.Exe, StringComparison.OrdinalIgnoreCase))) { app.Kind = AppKind.Service; app.Shortcut = false; }
        }
        else
        {
            foreach (var app in manifest.Apps.Where(a => a.Kind == AppKind.Service))
            {
                string name = Path.GetFileNameWithoutExtension(app.Exe);
                // a 32-bit program cannot run on the 64-bit runtime an installation carries: registered, not started
                manifest.Services.Add(new ServiceEntry
                {
                    Name = name, DisplayName = app.Name, Exe = app.Exe,
                    Start = app.Warnings.Contains("32-bit only") ? StartType.Disabled : StartType.Auto,
                });
            }
            AssignAccounts(manifest, entries, log);
        }
        foreach (var service in manifest.Services)
        {
            // a program that checks it is on Windows and refuses to start otherwise (its own assembly asks the system
            // and makes a PlatformNotSupportedException): told it is (WindowsPaths: WPFWEBGPU_OS_IDENTITY)
            if (AppHost.AppDll(Path.Combine(installDir, service.Exe)) is { } dll && PeFile.TryRead(dll) is { } pe
                && pe.TypeReferences.Contains("PlatformNotSupportedException") && pe.TypeReferences.Contains("OperatingSystem"))
                service.Environment["WPFWEBGPU_OS_IDENTITY"] = "windows";
        }

        manifest.Product.Name = Path.GetFileName(installDir);
        // programs that check for Windows: the product is told it runs on it, every program of it alike
        if (manifest.Services.Any(s => s.Environment.ContainsKey("WPFWEBGPU_OS_IDENTITY"))) manifest.Product.OsIdentity = "windows";
        // the first window app's icon (the product's main app comes first in the folder order more often than not)
        foreach (var app in manifest.Apps.Where(a => a.Kind == AppKind.Gui))
        {
            if (PeFile.ReadIcon(Path.Combine(installDir, app.Exe)) is not { } ico) continue;
            manifest.Product.Icon = app.Exe;
            log?.Invoke($"icon: {app.Exe} ({BitConverter.ToUInt16(ico, 4)} images, {ico.Length} bytes)");
            break;
        }
        manifest.Product.Publisher = companies.OrderByDescending(c => c.Value).Select(c => c.Key).FirstOrDefault() ?? "";
        manifest.Product.IdPrefix = "com." + Identifier(manifest.Product.Publisher.Length > 0 ? manifest.Product.Publisher : "example");
        return manifest;
    }

    // ---- the services the registry registers ---------------------------------------------------------
    //
    // A Windows service is what the service manager has registered: the key SYSTEM\CurrentControlSet\Services\<name>,
    // with its program (ImagePath), how it starts (Start, DelayedAutostart), the account it runs as (ObjectName), the
    // services it needs first (DependOnService) and who may control it (its Security key). Where the registry input
    // holds keys for the installation's programs, those are the services, under the names, start types and accounts
    // they were registered with -- not a guess from what the programs reference. A key whose program is not the
    // installation's (Windows' own services, another product's) is passed over.

    private const string ServicesKey = @"SYSTEM\CurrentControlSet\Services\";

    internal static List<ServiceEntry> RegisteredServices(PackageManifest manifest, IReadOnlyList<RegEntry> entries, string installDir, Action<string>? log)
    {
        var keys = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        foreach (var e in entries)
        {
            if (e.Kind != RegEntryKind.Value || e.Value == null || !e.Hive.Equals("HKEY_LOCAL_MACHINE", StringComparison.OrdinalIgnoreCase)
                || !e.Key.StartsWith(ServicesKey, StringComparison.OrdinalIgnoreCase)) continue;
            string rest = e.Key[ServicesKey.Length..];
            int slash = rest.IndexOf('\\');
            string name = slash < 0 ? rest : rest[..slash];
            // the key's own values, and its security descriptor (Security\Security)
            string value;
            if (slash < 0) value = e.Name;
            else if (rest[(slash + 1)..].Equals("Security", StringComparison.OrdinalIgnoreCase) && e.Name.Equals("Security", StringComparison.OrdinalIgnoreCase)) value = "\\Security";
            else continue;
            if (!keys.TryGetValue(name, out var values)) { keys[name] = values = new(StringComparer.OrdinalIgnoreCase); order.Add(name); }
            values[value] = e.Value;
        }

        var mapper = new PathMapper(manifest.PathMappings);
        var services = new List<ServiceEntry>();
        foreach (string name in order)
        {
            var v = keys[name];
            if (v.GetValueOrDefault("ImagePath") is not string imagePath) continue;
            var (program, args) = SplitCommandLine(imagePath);
            string mapped = mapper.Map(program);
            if (!mapped.StartsWith(PathTokens.InstallDir, StringComparison.Ordinal)) continue;
            string rel = mapped[PathTokens.InstallDir.Length..].TrimStart('\\', '/').Replace('\\', '/');
            if (ProgramOf(manifest, installDir, rel) is not { } app)
            {
                log?.Invoke($"service {name}: {rel} is not a program of the installation; left out");
                continue;
            }
            int start = v.GetValueOrDefault("Start") is int st ? st : 3;
            var service = new ServiceEntry
            {
                Name = name,
                DisplayName = v.GetValueOrDefault("DisplayName") as string ?? name,
                Description = v.GetValueOrDefault("Description") as string ?? "",
                Exe = app.Exe,
                Args = args,
                // boot and system start (0, 1) are a driver's; a program started with the system is automatic
                Start = start switch { <= 2 => StartType.Auto, 3 => StartType.Manual, _ => StartType.Disabled },
                DelayedAutoStart = v.GetValueOrDefault("DelayedAutostart") is int d && d != 0,
                StartName = v.GetValueOrDefault("ObjectName") as string ?? "LocalSystem",
                Security = v.GetValueOrDefault("\\Security") is byte[] sd ? Convert.ToBase64String(sd) : "",
                RestartAfterFailure = v.GetValueOrDefault("FailureActions") is byte[] fa ? RestartDelay(fa) : 0,
            };
            service.Account = AccountFor(service.StartName, name, log);
            // a 32-bit program cannot run on the 64-bit runtime an installation carries: registered, not started
            if (app.Warnings.Contains("32-bit only") && service.Start != StartType.Disabled)
            {
                log?.Invoke($"service {name}: {app.Exe} is 32-bit only; registered disabled");
                service.Start = StartType.Disabled;
            }
            if (v.GetValueOrDefault("DependOnService") is string[] deps) service.DependsOn.AddRange(deps.Where(x => x.Length > 0));
            else if (v.GetValueOrDefault("DependOnService") is string dep && dep.Length > 0) service.DependsOn.Add(dep);
            services.Add(service);
            log?.Invoke($"service  {name} ({service.Start}{(service.DelayedAutoStart ? ", delayed" : "")}, {service.StartName}): {app.Exe}");
        }
        // what they depend on that is not one of them is Windows' own (Tcpip, HTTP), which this system provides itself
        var names = new HashSet<string>(services.Select(x => x.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var service in services) service.DependsOn.RemoveAll(x => !names.Contains(x));
        return services;
    }

    // the account a service runs as here, for the one it is registered with: Windows' own service accounts are the
    // machine accounts the installers make; a virtual account (NT SERVICE\name), which reaches the network as the
    // machine does, runs as NETWORK SERVICE; any other (a user's) as the system, as before
    /// <summary>The seconds after which a service's recovery actions restart it (at least one), 0 where they never do:
    /// SERVICE_FAILURE_ACTIONS as the registry keeps it -- reset period, reboot message, command, the count of actions
    /// and where they are (32-bit each), then each action's type (SC_ACTION_RESTART: 1) and delay in milliseconds.</summary>
    internal static int RestartDelay(byte[] failureActions)
    {
        if (failureActions.Length < 20) return 0;
        int count = BitConverter.ToInt32(failureActions, 12);
        for (int i = 0, at = 20; i < count && at + 8 <= failureActions.Length; i++, at += 8)
            if (BitConverter.ToInt32(failureActions, at) == 1)
                return Math.Max(1, BitConverter.ToInt32(failureActions, at + 4) / 1000);
        return 0;
    }

    private static ServiceAccount AccountFor(string objectName, string service, Action<string>? log)
    {
        string bare = objectName.Replace(" ", "");
        if (bare.Length == 0 || bare.Equals("LocalSystem", StringComparison.OrdinalIgnoreCase) || bare.Equals(@"NTAUTHORITY\SYSTEM", StringComparison.OrdinalIgnoreCase)
            || bare.Equals(@".\LocalSystem", StringComparison.OrdinalIgnoreCase)) return ServiceAccount.LocalSystem;
        if (bare.Equals(@"NTAUTHORITY\NetworkService", StringComparison.OrdinalIgnoreCase)) return ServiceAccount.NetworkService;
        if (bare.Equals(@"NTAUTHORITY\LocalService", StringComparison.OrdinalIgnoreCase)) return ServiceAccount.LocalService;
        if (bare.StartsWith(@"NTSERVICE\", StringComparison.OrdinalIgnoreCase)) return ServiceAccount.NetworkService;
        log?.Invoke($"service {service} runs as {objectName} on Windows: run as LocalSystem here (set \"account\" to choose)");
        return ServiceAccount.LocalSystem;
    }

    // an ImagePath's program and arguments: quoted, or -- as the service manager reads an unquoted one -- up to the .exe
    internal static (string Program, List<string> Args) SplitCommandLine(string commandLine)
    {
        string text = commandLine.Trim();
        string program, rest;
        if (text.StartsWith('"'))
        {
            int close = text.IndexOf('"', 1);
            program = close < 0 ? text[1..] : text[1..close];
            rest = close < 0 ? "" : text[(close + 1)..];
        }
        else
        {
            int exe = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            int end = exe < 0 ? (text.IndexOf(' ') is int sp && sp > 0 ? sp : text.Length) : exe + 4;
            program = text[..end];
            rest = text[end..];
        }
        var args = new List<string>();
        var current = new System.Text.StringBuilder();
        bool quoted = false, any = false;
        foreach (char c in rest)
        {
            if (c == '"') { quoted = !quoted; any = true; continue; }
            if (char.IsWhiteSpace(c) && !quoted) { if (any) { args.Add(current.ToString()); current.Clear(); any = false; } continue; }
            current.Append(c); any = true;
        }
        if (any) args.Add(current.ToString());
        return (program, args);
    }

    // the installation's program a registration names: that executable, or -- where the installation carries it under
    // another name, as for another processor (App_ARM64.exe for App.exe) -- the executable in its folder that starts
    // the same app
    private static AppEntry? ProgramOf(PackageManifest manifest, string installDir, string rel)
    {
        if (manifest.Apps.FirstOrDefault(a => a.Exe.Equals(rel, StringComparison.OrdinalIgnoreCase)) is { } exact) return exact;
        string dir = Path.GetDirectoryName(rel)?.Replace('\\', '/') ?? "";
        string wanted = Path.Combine(installDir, dir, Path.GetFileNameWithoutExtension(rel) + ".dll");
        return manifest.Apps.FirstOrDefault(a =>
            (Path.GetDirectoryName(a.Exe)?.Replace('\\', '/') ?? "").Equals(dir, StringComparison.OrdinalIgnoreCase)
            && AppHost.AppDll(Path.Combine(installDir, a.Exe)) is { } dll && dll.Equals(Path.GetFullPath(wanted), StringComparison.OrdinalIgnoreCase));
    }

    // ---- what an app is -------------------------------------------------------------------------------

    private static bool IsService(string dll, string dir)
    {
        // the app's own assembly, or one of its folder's that it uses (a product's shared service host)
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((dll, 0));
        while (queue.Count > 0)
        {
            var (path, depth) = queue.Dequeue();
            if (!seen.Add(path) || PeFile.TryRead(path) is not { Managed: true } pe) continue;
            if (pe.AssemblyReferences.Any(r => s_serviceAssemblies.Contains(r, StringComparer.OrdinalIgnoreCase))) return true;
            if (pe.TypeReferences.Contains("ServiceBase")) return true;
            if (depth >= 2) continue;
            foreach (string r in pe.AssemblyReferences)
            {
                string local = Path.Combine(dir, r + ".dll");
                if (File.Exists(local)) queue.Enqueue((local, depth + 1));
            }
        }
        return false;
    }

    private static List<string> Frameworks(string runtimeConfig)
    {
        var names = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(runtimeConfig), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (!doc.RootElement.TryGetProperty("runtimeOptions", out var options)) return names;
            if (options.TryGetProperty("framework", out var one) && one.TryGetProperty("name", out var n1)) names.Add(n1.GetString() ?? "");
            if (options.TryGetProperty("frameworks", out var many))
                foreach (var f in many.EnumerateArray())
                    if (f.TryGetProperty("name", out var n)) names.Add(n.GetString() ?? "");
        }
        catch (JsonException)
        {
        }
        return names;
    }

    private static string DisplayName(string dll, string fallback)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(dll);
            string? text = !string.IsNullOrWhiteSpace(info.FileDescription) ? info.FileDescription : info.ProductName;
            return string.IsNullOrWhiteSpace(text) || text.Equals(fallback, StringComparison.OrdinalIgnoreCase) ? fallback : text.Trim();
        }
        catch (FileNotFoundException)
        {
            return fallback;
        }
    }

    private static string Company(string dll)
    {
        try { return FileVersionInfo.GetVersionInfo(dll).CompanyName?.Trim() ?? ""; }
        catch (FileNotFoundException) { return ""; }
    }

    private static string Identifier(string text)
    {
        string id = new string(text.ToLowerInvariant().Where(char.IsAsciiLetterOrDigit).ToArray());
        return id.Length == 0 || char.IsAsciiDigit(id[0]) ? "app" + id : id;
    }

    /// <summary>What may keep a folder's apps from running off Windows: C++/CLI's mixed-mode assemblies and
    /// native libraries with no build for macOS or Linux beside them (runtimes/&lt;rid&gt;/native).</summary>
    private sealed class FolderInfo
    {
        public List<string> Warnings { get; } = new();

        public static FolderInfo Read(string dir)
        {
            var info = new FolderInfo();
            var unixNatives = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string runtimes = Path.Combine(dir, "runtimes");
            if (Directory.Exists(runtimes))
                foreach (string f in Directory.EnumerateFiles(runtimes, "*", SearchOption.AllDirectories))
                    if (f.EndsWith(".so", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".dylib", StringComparison.OrdinalIgnoreCase))
                    {
                        string n = Path.GetFileNameWithoutExtension(f);
                        unixNatives.Add(n.StartsWith("lib", StringComparison.Ordinal) ? n[3..] : n);
                    }

            var mixed = new List<string>();
            var native = new List<string>();
            foreach (string f in Directory.EnumerateFiles(dir, "*.dll").Order(StringComparer.OrdinalIgnoreCase))
            {
                if (PeFile.TryRead(f) is not { } pe) continue;
                string name = Path.GetFileNameWithoutExtension(f);
                if (pe.Managed && !pe.ILOnly) mixed.Add(name);
                else if (!pe.Managed && !unixNatives.Contains(name) && !s_windowsOnlyHelpers.Contains(name)) native.Add(name);
            }
            if (mixed.Count > 0) info.Warnings.Add("mixed-mode: " + Short(mixed));
            if (native.Count > 0) info.Warnings.Add("Windows native: " + Short(native));
            return info;
        }

        // the .NET host's own Windows-only pieces, which every Windows app folder may carry
        private static readonly HashSet<string> s_windowsOnlyHelpers = new(StringComparer.OrdinalIgnoreCase)
        {
            "Ijwhost", "hostfxr", "hostpolicy", "coreclr", "clrjit", "Microsoft.DiaSymReader.Native.amd64",
            "WebView2Loader", "D3DCompiler_47_cor3", "PenImc_cor3", "PresentationNative_cor3", "vcruntime140_cor3", "wpfgfx_cor3",
        };

        private static string Short(List<string> names) =>
            names.Count <= 4 ? string.Join(", ", names) : string.Join(", ", names.Take(4)) + $" (+{names.Count - 4})";
    }

    // ---- the registry's Windows paths -----------------------------------------------------------------

    private static readonly Regex s_windowsPath = new(@"[A-Za-z]:\\[^""*?<>|\r\n\0;]*", RegexOptions.Compiled);

    /// <summary>The Windows folders the registry's paths start with that are the installation (each path's
    /// tail is found in it), and Windows' ProgramData, each mapped to its token.</summary>
    public static List<PathMapping> DetectMappings(IEnumerable<RegEntry> entries, string installDir)
    {
        var finder = new CaseInsensitiveFinder(installDir);
        var prefixes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries)
        {
            if (e.Kind != RegEntryKind.Value) continue;
            IEnumerable<string> texts = e.Value switch { string s => new[] { s }, string[] m => m, _ => Array.Empty<string>() };
            foreach (string text in texts)
                foreach (Match m in s_windowsPath.Matches(text))
                {
                    string[] parts = m.Value.TrimEnd('\\', ' ').Split('\\');
                    // the shortest prefix whose rest is in the installation
                    for (int i = 1; i <= parts.Length; i++)
                    {
                        if (!finder.Exists(parts[i..])) continue;
                        if (i > 1) prefixes[string.Join('\\', parts[..i])] = prefixes.GetValueOrDefault(string.Join('\\', parts[..i])) + 1;
                        break;
                    }
                }
        }
        var mappings = prefixes.OrderByDescending(p => p.Value).ThenByDescending(p => p.Key.Length)
            .Select(p => new PathMapping { From = p.Key, To = PathTokens.InstallDir }).ToList();
        mappings.Add(new PathMapping { From = @"C:\ProgramData", To = PathTokens.ProgramData });
        return mappings;
    }

    private sealed class CaseInsensitiveFinder(string root)
    {
        private readonly Dictionary<string, Dictionary<string, string>?> _listings = new(StringComparer.Ordinal);

        public bool Exists(string[] parts)
        {
            string at = root;
            foreach (string part in parts)
            {
                if (part.Length == 0) continue;
                var listing = List(at);
                if (listing == null || !listing.TryGetValue(part, out string? actual)) return false;
                at = Path.Combine(at, actual);
            }
            return parts.Any(p => p.Length > 0);
        }

        private Dictionary<string, string>? List(string dir)
        {
            if (_listings.TryGetValue(dir, out var listing)) return listing;
            listing = Directory.Exists(dir)
                ? Directory.EnumerateFileSystemEntries(dir).Select(Path.GetFileName).OfType<string>()
                    .GroupBy(n => n, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase)
                : null;
            return _listings[dir] = listing;
        }
    }
}
