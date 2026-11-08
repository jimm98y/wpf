// What the desktop installers share: names made safe for each system, the product's icon.

using System.Text;

namespace WpfWebGpu.Packager.Desktop;

internal static class DesktopCommon
{
    /// <summary>A name as an identifier's segment: lower case letters, digits and '-'.</summary>
    public static string Segment(string s)
    {
        var sb = new StringBuilder();
        foreach (char c in s.ToLowerInvariant()) if (char.IsAsciiLetterOrDigit(c) || c == '-') sb.Append(c);
        return sb.Length == 0 ? "app" : sb.ToString();
    }

    private static readonly string[] s_architectureSuffixes = { "_ARM64", "_x64", "_x86" };

    /// <summary>The program an app entry is, whichever processor's executable it names: App for App.exe and for
    /// App_ARM64.exe alike (a product's per-processor executables), so that what is installed for it (an app
    /// bundle's identifier, a .desktop entry) is the same app from one package to the next.</summary>
    public static string ProgramName(AppEntry app)
    {
        string name = Path.GetFileNameWithoutExtension(app.Exe);
        foreach (string suffix in s_architectureSuffixes)
            if (name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return name[..^suffix.Length];
        return name;
    }

    /// <summary>
    /// The service manager's database the package installs into the machine's state (services.json): each service as
    /// it was registered -- its name, display name, description, start type, account, dependencies and security
    /// descriptor -- with the program it runs here and the system service that runs it (<paramref name="unit"/>).
    /// </summary>
    public static string ServiceDatabase(PackageManifest manifest, string root, Func<ServiceEntry, string> unit)
    {
        var db = new WpfWebGpu.Services.ServiceDatabase();
        foreach (var s in manifest.Services)
        {
            string program = root + "/app/" + s.Exe.Replace('\\', '/');
            db.Services.Add(new WpfWebGpu.Services.ServiceRecord
            {
                Name = s.Name,
                DisplayName = s.DisplayName.Length > 0 ? s.DisplayName : s.Name,
                Description = s.Description,
                PathName = "\"" + program + "\"" + string.Concat(s.Args.Select(a => " " + (a.Contains(' ') ? "\"" + a + "\"" : a))),
                Start = s.Start switch { StartType.Auto => WpfWebGpu.Services.ServiceStart.Auto, StartType.Disabled => WpfWebGpu.Services.ServiceStart.Disabled, _ => WpfWebGpu.Services.ServiceStart.Manual },
                DelayedAutoStart = s.DelayedAutoStart,
                StartName = s.StartName.Length > 0 ? s.StartName : s.Account switch
                {
                    ServiceAccount.NetworkService => @"NT AUTHORITY\NetworkService",
                    ServiceAccount.LocalService => @"NT AUTHORITY\LocalService",
                    _ => "LocalSystem",
                },
                DependsOn = new List<string>(s.DependsOn),
                Security = s.Security,
                Unit = unit(s),
            });
        }
        string path = Path.GetTempFileName();
        try { db.Save(path); return File.ReadAllText(path); }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// The installation's web sites (iis.json, installed into the machine's state): its machine's IIS configuration,
    /// each virtual directory's folder where the installation (under <paramref name="root"/>) or its ProgramData
    /// (under <paramref name="state"/>) puts it; null where there is none to serve.
    /// </summary>
    public static string? IisConfiguration(PackageManifest manifest, string root, string state, IReadOnlyList<WebPages.Page>? pages = null)
    {
        bool hasIis = manifest.Inputs.IisConfig.Length > 0 && File.Exists(manifest.Inputs.IisConfig);
        if (!hasIis && (pages == null || pages.Count == 0)) return null;
        var mapper = new PathMapper(manifest.PathMappings);
        string? Local(string windows)
        {
            string mapped = mapper.Map(windows);
            string rest(string token) => mapped[token.Length..].Replace('\\', '/').TrimEnd('/');
            if (mapped.StartsWith(PathTokens.InstallDir, StringComparison.Ordinal)) return root + "/app" + rest(PathTokens.InstallDir);
            if (mapped.StartsWith(PathTokens.ProgramData, StringComparison.Ordinal)) return state + "/ProgramData" + rest(PathTokens.ProgramData);
            return null;
        }
        var config = hasIis ? WpfWebGpu.Services.IisConfiguration.Parse(File.ReadAllText(manifest.Inputs.IisConfig), Local) : new WpfWebGpu.Services.IisConfiguration();
        // the apps' pages: applications of the site that serves port 80 (IIS's Default Web Site), made where there is none
        if (pages is { Count: > 0 })
        {
            var site = config.Sites.FirstOrDefault(s => s.Bindings.Any(b => b.Protocol == "http" && b.Port == 80));
            if (site == null)
            {
                site = new WpfWebGpu.Services.IisSite { Name = "Default Web Site", Id = config.Sites.Count == 0 ? 1 : config.Sites.Max(s => s.Id) + 1 };
                site.Bindings.Add(new WpfWebGpu.Services.IisBinding { Protocol = "http", Address = "*", Port = 80 });
                config.Sites.Add(site);
            }
            if (!config.Pools.Any(p => p.Name == "DefaultAppPool")) config.Pools.Add(new WpfWebGpu.Services.IisPool { Name = "DefaultAppPool" });
            foreach (var page in pages)
            {
                site.Applications.RemoveAll(a => a.Path.Equals(page.UrlPath, StringComparison.OrdinalIgnoreCase));
                var app = new WpfWebGpu.Services.IisApplication { Path = page.UrlPath, Pool = "DefaultAppPool" };
                app.VirtualDirectories.Add(new WpfWebGpu.Services.IisVirtualDirectory { Path = "/", PhysicalPath = root + "/web/" + page.Folder });
                site.Applications.Add(app);
            }
        }
        return config.Sites.Any(s => s.Applications.Any(a => a.VirtualDirectories.Any(v => v.PhysicalPath != null))) ? config.ToJson() : null;
    }

    public static string FileName(string s) => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ':' ? '_' : c));

    /// <summary>The app's own icon (its .exe's), else the product's.</summary>
    public static (byte[] Rgba, int Width, int Height)? Icon(PackageManifest manifest, AppEntry? app = null)
    {
        if (app != null && PeFile.ReadIcon(Path.Combine(manifest.Inputs.InstallDir, app.Exe)) is { } own && Icons.Largest(own) is { } image) return image;
        if (manifest.Product.Icon.Length == 0) return null;
        string p = Path.Combine(manifest.Inputs.InstallDir, manifest.Product.Icon);
        byte[]? ico = p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? PeFile.ReadIcon(p) : File.Exists(p) ? File.ReadAllBytes(p) : null;
        return ico == null ? null : Icons.Largest(ico);
    }

    /// <summary>Whether a program of the installation serves through http.sys (references its server assembly):
    /// then the package installs the machine's http.sys service (wpfrun httpsys), which owns the ports.</summary>
    public static bool UsesHttpSys(PackageManifest manifest)
    {
        byte[] name = System.Text.Encoding.ASCII.GetBytes("Microsoft.AspNetCore.Server.HttpSys");
        var excludes = new GlobSet(manifest.Exclude);
        foreach (string dll in Directory.EnumerateFiles(manifest.Inputs.InstallDir, "*.dll", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(manifest.Inputs.InstallDir, dll).Replace('\\', '/');
            if (excludes.Matches(rel) || Path.GetFileNameWithoutExtension(dll).Equals("Microsoft.AspNetCore.Server.HttpSys", StringComparison.OrdinalIgnoreCase)) continue;
            var info = new FileInfo(dll);
            if (info.Length > 64 * 1024 * 1024) continue;
            if (File.ReadAllBytes(dll).AsSpan().IndexOf(name) >= 0) return true;
        }
        return false;
    }

    /// <summary>The system account a service account is on macOS or Linux (the installers make it where it is
    /// missing; the stack takes it for Windows' own: NETWORK SERVICE, LOCAL SERVICE); null for LocalSystem (root).</summary>
    public static string? SystemAccount(ServiceAccount account, bool mac) => account switch
    {
        ServiceAccount.NetworkService => mac ? "_wpfnetworkservice" : "wpf-networkservice",
        ServiceAccount.LocalService => mac ? "_wpflocalservice" : "wpf-localservice",
        _ => null,
    };

    public static string AccountTitle(ServiceAccount account) => account == ServiceAccount.LocalService ? "LOCAL SERVICE" : "NETWORK SERVICE";

    /// <summary>services.sh: the installation's services by their Windows names, as the Services console or sc.exe
    /// manages them -- list, status, start, stop, restart -- through launchd (macOS) or systemd (Linux).
    /// <paramref name="services"/>: each service's name and its launchd label or systemd unit.</summary>
    public static string ServicesScript(string product, IEnumerable<(string Name, string Id)> services, bool mac)
    {
        var list = services.ToList();
        string table = string.Concat(list.Select(x => $"{x.Name}={x.Id}\n"));
        string start = mac
            ? "launchctl bootstrap system \"/Library/LaunchDaemons/$1.plist\" 2>/dev/null || launchctl kickstart system/\"$1\""
            : "systemctl start \"$1\"";
        string stop = mac ? "launchctl bootout system/\"$1\" 2>/dev/null || true" : "systemctl stop \"$1\"";
        string restart = mac
            ? "if launchctl print system/\"$1\" >/dev/null 2>&1; then launchctl kickstart -k system/\"$1\"; else launchctl bootstrap system \"/Library/LaunchDaemons/$1.plist\"; fi"
            : "systemctl restart \"$1\"";
        string state = mac
            ? "launchctl print system/\"$1\" 2>/dev/null | awk '/^\\tstate = /{s=substr($0,index($0,\"= \")+2)} /^\\tpid = /{p=$3} /^\\tlast exit code = /{e=substr($0,index($0,\"= \")+2)} END{if(s==\"\")s=\"not loaded\"; printf \"%s%s%s\", s, (p!=\"\"?\"  pid \"p:\"\"), (s!=\"running\" && e!=\"\"?\"  (last exit: \"e\")\":\"\")}'"
            : "printf '%s' \"$(systemctl is-active \"$1\" 2>/dev/null)$(p=$(systemctl show -p MainPID --value \"$1\" 2>/dev/null); [ \"$p\" != 0 ] && [ -n \"$p\" ] && echo \"  pid $p\")\""; 
        return $$"""
            #!/bin/sh
            # {{product}}'s services, as the Services console or sc.exe manages them on Windows:
            #   services.sh [list]                           every service, whether it runs, and its process
            #   services.sh status|start|stop|restart <name> one service, by its name (any case)
            #   services.sh start|stop|restart all           every service
            # A service's process carries its program's name (ps, top, Activity Monitor); its log is the program's.
            TABLE="{{table}}"
            id_of() { printf "$TABLE" | awk -F= -v n="$1" 'tolower($1)==tolower(n){print $2; exit}'; }
            state_of() { {{state}}; }
            do_start() { {{start}}; }
            do_stop() { {{stop}}; }
            do_restart() { {{restart}}; }
            command="${1:-list}"
            case "$command" in
              list)
                printf "$TABLE" | while IFS== read -r name id; do [ -n "$name" ] && printf '%-44s %s\n' "$name" "$(state_of "$id")"; done
                exit 0 ;;
              status|start|stop|restart) ;;
              *) echo "usage: $0 [list] | status|start|stop|restart <service>|all" >&2; exit 2 ;;
            esac
            [ -n "$2" ] || { echo "usage: $0 $command <service>|all" >&2; exit 2; }
            if [ "$command" != status ] && [ "$(id -u)" != 0 ]; then exec sudo "$0" "$@"; fi
            if [ "$2" = all ]; then
              printf "$TABLE" | while IFS== read -r name id; do [ -n "$id" ] && [ "$command" != status ] && do_$command "$id"; [ -n "$name" ] && printf '%-44s %s\n' "$name" "$(state_of "$id")"; done
              exit 0
            fi
            id=$(id_of "$2")
            [ -n "$id" ] || { echo "no service $2 (services.sh list names them)" >&2; exit 1; }
            [ "$command" = status ] || do_$command "$id"
            printf '%-44s %s\n' "$2" "$(state_of "$id")"

            """;
    }

    public static string AppName(AppEntry app) => app.Name.Length > 0 ? app.Name : Path.GetFileNameWithoutExtension(app.Exe);
}
