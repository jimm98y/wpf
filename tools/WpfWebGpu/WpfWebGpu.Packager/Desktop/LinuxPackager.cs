// The whole installation as a Debian package (.deb), written in managed code -- an ar archive of debian-binary,
// control.tar.gz and data.tar.gz -- so it is made on any machine, with nothing installed:
//
//   /opt/<product>/app/          the Program Files folder, as it is
//   /opt/<product>/runtime/      what runs it (desktop-linux-<arch>.pack: .NET, wpfrun)
//   /opt/<product>/seed/         the registry (paths as tokens) and ProgramData: made the machine's state on first install
//   /var/opt/<product>/state/    the machine's state (WPFRUN_STATE), kept on upgrades and on remove (purge removes it)
//   /lib/systemd/system/<pkg>-<service>.service       each service, as the SCM runs it
//   /usr/share/applications/<pkg>-<app>.desktop       each window app, with its icon
//   /etc/xdg/autostart/<pkg>-startup.desktop          at each log-on, the Startup folders' shortcuts (wpfrun startup)
//   /lib/systemd/system/<pkg>-httpsys.service         the machine's http.sys (wpfrun httpsys), where a program serves through it
//
// The state is writable by the group 'users', as ProgramData and HKLM are by a Windows machine's users.

using System.Formats.Tar;
using System.IO.Compression;
using System.Text;

namespace WpfWebGpu.Packager.Desktop;

public static partial class LinuxPackager
{
    /// <remarks>Where the manifest asks for SQL Server (manifest.SqlServer), a package of its own the installation's
    /// recommends (LinuxPackager.SqlServer.cs), and in the .tar.gz a part install.sh offers.</remarks>
    public static string Build(PackageManifest manifest, StagedState staged, PackFile runtimePack, string outDir, Action<string> log,
        IReadOnlyList<WebPages.Page>? pages = null)
    {
        bool sqlServer = manifest.SqlServer.Include;
        bool webView = UsesWebView(manifest);
        string product = manifest.Product.Name;
        string rid = runtimePack["rid"];
        string arch = rid.EndsWith("arm64", StringComparison.Ordinal) ? "arm64" : "amd64";
        string pkg = PackageName(product);
        string root = "/opt/" + product, state = "/var/opt/" + product + "/state";
        string work = Path.Combine(outDir, ".work", "linux-" + arch);
        string license = manifest.Product.License.Length > 0 ? License.Text(manifest.Product.License) : "";
        string question = license.Length > 0 ? LicenseQuestion(pkg, license) : "";
        if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        Directory.CreateDirectory(work);
        var excludes = new GlobSet(manifest.Exclude);

        // ---- the files, into data.tar.gz ----
        log($"Linux ({arch}): the installation into {root}");
        string data = Path.Combine(work, "data.tar.gz");
        long installedBytes = 0;
        var units = new List<(string Name, bool Enable)>();
        var maskedUnits = new List<string>();
        using (var file = File.Create(data))
        using (var gz = new GZipStream(file, CompressionLevel.Optimal))
        {
            var tar = new TarTree(gz);
            int files = 0;
            foreach (string f in Directory.EnumerateFiles(manifest.Inputs.InstallDir, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(manifest.Inputs.InstallDir, f).Replace('\\', '/');
                if (excludes.Matches(rel) || Path.GetFileName(f) == ".DS_Store") continue;
                installedBytes += tar.AddFile(f, $"{root}/app/{rel}", executable: false);
                files++;
            }
            string runtime = Path.Combine(runtimePack.Directory, "runtime");
            foreach (string f in Directory.EnumerateFiles(runtime, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(runtime, f).Replace('\\', '/');
                installedBytes += tar.AddFile(f, $"{root}/runtime/{rel}", executable: IsExecutable(f, rel));
            }
            foreach (string hive in Directory.EnumerateFiles(staged.RegistryDir, "*.reg"))
                installedBytes += tar.AddFile(hive, $"{root}/seed/registry/{Path.GetFileName(hive)}", executable: false);
            foreach (var (source, target) in staged.ProgramDataFiles)
                installedBytes += tar.AddFile(source, $"{root}/seed/ProgramData/{target}", executable: false);
            if (DesktopCommon.PathTable(manifest) is { } table)
            {
                string tableFile = Path.Combine(work, "paths.json");
                File.WriteAllText(tableFile, table);
                installedBytes += tar.AddFile(tableFile, $"{root}/seed/paths.json", executable: false);
            }
            log($"  {files} files of the installation, {staged.ProgramDataFiles.Count} of ProgramData");

            // the installation's web sites, as IIS serves them on its machine (the machine's applicationHost.config, installed
            // into its state, an upgrade's merged in), and the web server's daemon, root, which claims their URLs from http.sys
            // the apps' pages for the browser, which the web server serves (web/<folder>, one application each)
            foreach (var page in pages ?? Array.Empty<WebPages.Page>())
                foreach (string f in Directory.EnumerateFiles(page.Built, "*", SearchOption.AllDirectories))
                    installedBytes += tar.AddFile(f, $"{root}/web/{page.Folder}/{Path.GetRelativePath(page.Built, f).Replace('\\', '/')}", executable: false);
            string? iis = DesktopCommon.ApplicationHostConfig(manifest, root, state, pages);
            if (iis != null)
            {
                tar.AddText($"{root}/inetsrv/applicationHost.config", iis, executable: false);
                tar.AddText($"{root}/inetpub/wwwroot/iisstart.htm", DesktopCommon.StartPage(manifest.Product.Name), executable: false);
                tar.AddText($"/lib/systemd/system/{pkg}-iis.service", SystemUnit(manifest, pkg, root, state, "iis", "web server"), executable: false);
                log("  web server: the installation's IIS sites");
            }
            // the machine's http.sys: the ports the programs' URL prefixes name, owned apart from every program
            bool httpSys = DesktopCommon.UsesHttpSys(manifest) || iis != null;
            if (httpSys)
            {
                tar.AddText($"/lib/systemd/system/{pkg}-httpsys.service", SystemUnit(manifest, pkg, root, state, "httpsys", "http.sys"), executable: false);
                units.Add(($"{pkg}-httpsys.service", true));
                log("  http.sys service: a program serves through http.sys");
            }
            // each service's unit, a disabled one's too (masked, so that its start type can be changed); automatic ones
            // enabled, manual ones started when the service manager starts them
            var masked = new List<string>();
            if (iis != null) units.Add(($"{pkg}-iis.service", true));   // after http.sys, whose ports it claims
            foreach (var service in manifest.Services)
            {
                string unit = UnitName(pkg, service.Name);
                tar.AddText($"/lib/systemd/system/{unit}", Unit(manifest, service, pkg, root, state), executable: false);
                if (service.Start == StartType.Disabled) masked.Add(unit);
                else units.Add((unit, service.Start == StartType.Auto));
            }
            // the service manager: the services as registered, installed into the machine's state on every install, and
            // its daemon, root, which starts and stops them for callers that are not
            tar.AddText($"{root}/services.json", DesktopCommon.ServiceDatabase(manifest, root, sv => UnitName(pkg, sv.Name), Host(manifest, pkg, root, state)), executable: false);
            // the SQL Server Browser, for the SQL Server instance the installation names (SqlServer.cs)
            if (sqlServer)
            {
                tar.AddText($"/lib/systemd/system/{SqlBrowserUnitName(pkg)}.service", SqlBrowserUnit(manifest), executable: false);
                units.Add(($"{SqlBrowserUnitName(pkg)}.service", true));
            }
            if (manifest.Services.Count > 0)
            {
                tar.AddText($"/lib/systemd/system/{pkg}-scm.service", SystemUnit(manifest, pkg, root, state, "scm", "service manager"), executable: false);
                units.Insert(0, ($"{pkg}-scm.service", true));
            }
            maskedUnits = masked;
            foreach (var app in manifest.Apps.Where(a => a.Kind == AppKind.Gui && a.Shortcut))
            {
                string id = pkg + "-" + DesktopCommon.Segment(DesktopCommon.ProgramName(app));
                string launcher = $"{root}/bin/{id}";
                // (its windows' app id the entry's name: WaylandWindow.AppId)
                tar.AddText(launcher, Launcher(DesktopCommon.AppName(app), pkg, state, manifest.Product.OsIdentity,
                    $"exec {Quote(root + "/runtime/run")} {Quote(root + "/app/" + app.Exe)} \"$@\"", $"{WindowClass(manifest, app)}={id}"), executable: true);
                // its icon, the program's own; a program of none has the desktop's standard one, as Windows shows its
                // default program icon (an entry naming an icon that is not there shows none at all)
                var image = DesktopCommon.Icon(manifest, app);
                if (image != null)
                    tar.AddBytes($"/usr/share/icons/hicolor/256x256/apps/{id}.png", Icons.PngAt(image.Value, 256));
                tar.AddText($"/usr/share/applications/{id}.desktop", Desktop(app, image != null ? id : "application-x-executable", launcher, WindowClass(manifest, app)), executable: false);
            }
            // the installation's uninstaller in the menu, as Windows' Apps & features entry: asks, then its remover as an
            // administrator (pkexec, in the words of the remover's polkit action)
            string remover = $"{root}/bin/{pkg}-remove";
            tar.AddText($"{root}/bin/{pkg}-uninstall", GuiUninstaller("Uninstall " + manifest.Product.Name,
                $"Remove {manifest.Product.Name} from this computer?\n\nIts settings and data (the registry, ProgramData) can be kept for a later install, or removed with it." +
                (sqlServer ? $"\n\nSQL Server's instance {manifest.SqlServer.Instance} stays: Uninstall SQL Server {manifest.SqlServer.Instance.ToUpperInvariant()} removes it." : ""),
                $"{manifest.Product.Name} was removed.", remover), executable: true);
            tar.AddText(remover, Remover(manifest.Product.Name, pkg, root + "/uninstall.sh"), executable: true);
            tar.AddText($"/usr/share/polkit-1/actions/com.wpfwebgpu.uninstall.{pkg}.policy", RemoverPolicy($"com.wpfwebgpu.uninstall.{pkg}", manifest.Product.Name, remover), executable: false);
            tar.AddText($"/usr/share/applications/{pkg}-uninstall.desktop", UninstallerEntry("Uninstall " + manifest.Product.Name, $"{root}/bin/{pkg}-uninstall"), executable: false);
            // the services by their Windows names, through systemd
            var named = manifest.Services.Select(x => (x.Name, UnitName(pkg, x.Name))).ToList();
            if (httpSys) named.Insert(0, ("HTTP", $"{pkg}-httpsys.service"));
            tar.AddText($"{root}/services.sh", DesktopCommon.ServicesScript(manifest.Product.Name, named, mac: false), executable: true);
            // at log-on, what the apps put in the Startup folders, as Explorer starts it
            tar.AddText($"{root}/bin/{pkg}-startup", Launcher(manifest.Product.Name + " startup programs", pkg, state, manifest.Product.OsIdentity,
                $"exec {Quote(root + "/runtime/dotnet/dotnet")} {Quote(root + "/runtime/launcher/wpfrun.dll")} startup"), executable: true);
            tar.AddText($"/etc/xdg/autostart/{pkg}-startup.desktop", Autostart(manifest, $"{root}/bin/{pkg}-startup"), executable: false);
            // web content: WPE WebKit's apt source (LinuxPackager.WebView.cs)
            if (webView)
            {
                tar.AddText(WebViewSourcesPath(pkg), WebViewSources, executable: false);
                tar.AddText(WebViewPinPath(pkg), WebViewPin, executable: false);
                log("  web content: WPE WebKit, from Debian's archive pinned to its packages");
            }
            if (license.Length > 0)
            {
                tar.AddText($"/usr/share/doc/{pkg}/copyright", license + "\n", executable: false);
                log("  the license agreement: asked by debconf before installing, by install.sh in the .tar.gz");
            }
            tar.Finish();
        }

        // ---- control.tar.gz ----
        string control = Path.Combine(work, "control.tar.gz");
        using (var file = File.Create(control))
        using (var gz = new GZipStream(file, CompressionLevel.Optimal))
        {
            var tar = new TarTree(gz, directories: false);
            tar.AddText("./control", Control(manifest, pkg, arch, installedBytes, sqlServer, webView, license.Length > 0), executable: false);
            if (license.Length > 0)
            {
                tar.AddText("./templates", LicenseTemplates(question, product, license), executable: false);
                tar.AddText("./config", LicenseConfig(question), executable: true);
            }
            tar.AddText("./postinst", PostInst(root, state, units, maskedUnits, Accounts(manifest, pkg), webView ? WebViewPostInst(pkg, WebViewRecord(state)) : ""), executable: true);
            tar.AddText("./preinst", PreInst(pkg, license.Length > 0 ? LicensePreInst(question, product) : ""), executable: true);
            tar.AddText("./prerm", PreRm(units, root), executable: true);
            tar.AddText("./postrm", PostRm(state, units, maskedUnits, pkg, webView, license.Length > 0), executable: true);
            tar.Finish();
        }

        Directory.CreateDirectory(outDir);
        string deb = Path.Combine(outDir, $"{pkg}_{DebVersion(manifest.Product.Version)}_{arch}.deb");
        using (var output = File.Create(deb))
        {
            output.Write("!<arch>\n"u8);
            ArMember(output, "debian-binary", new MemoryStream("2.0\n"u8.ToArray()));
            using (var c = File.OpenRead(control)) ArMember(output, "control.tar.gz", c);
            using (var d = File.OpenRead(data)) ArMember(output, "data.tar.gz", d);
        }
        log($"{deb} ({new FileInfo(deb).Length / (1024 * 1024)} MB)");
        if (sqlServer) BuildSqlServer(manifest, arch, outDir, log);

        // the same for a Linux without dpkg (Fedora, RHEL, SUSE, Arch): the files as they are in the .deb, and
        // install.sh / uninstall.sh doing what its maintainer scripts do
        string archive = Path.Combine(outDir, $"{pkg}-{DebVersion(manifest.Product.Version)}-linux-{arch}.tar.gz");
        string folder = $"{pkg}-{DebVersion(manifest.Product.Version)}";
        using (var output = File.Create(archive))
        using (var gz = new GZipStream(output, CompressionLevel.Fastest))
        {
            var tar = new TarTree(gz, directories: false);
            tar.AddText($"./{folder}/install.sh", Install(pkg, root, state, units, maskedUnits, Accounts(manifest, pkg),
                (sqlServer ? SqlServerInstallStep(manifest, arch, pkg) : "") + (webView ? WebViewInstallStep(pkg, WebViewRecord(state)) : ""),
                license.Length > 0 ? LicenseInstallStep(product) : ""), executable: true);
            if (license.Length > 0) tar.AddText($"./{folder}/license.txt", license + "\n", executable: false);
            tar.AddText($"./{folder}/uninstall.sh", UninstallScript(pkg, root, state, units, maskedUnits,
                sqlServer ? SqlServerUninstallStep(manifest) : "", webView), executable: true);
            tar.AddFile(data, $"./{folder}/files.tar.gz", executable: false);
            if (sqlServer)
            {
                string sqlFiles = Path.Combine(work, "sqlserver.tar");
                using (var f = File.Create(sqlFiles)) WriteSqlServerFiles(f, manifest, arch);
                tar.AddFile(sqlFiles, $"./{folder}/sqlserver.tar", executable: false);
            }
            tar.Finish();
        }
        DesktopCommon.RemoveWork(work);
        log($"{archive} ({new FileInfo(archive).Length / (1024 * 1024)} MB)");
        return deb;
    }

    internal static string Install(string pkg, string root, string state, List<(string Name, bool Enable)> units, List<string> masked, string accounts,
        string sqlServerStep, string licenseStep = "") => $$"""
        #!/bin/sh
        # Installs {{pkg}} on a Linux without dpkg: its files into / (the list kept for uninstall.sh), then as the .deb's
        # postinst does -- the machine's state on the first install, the services enabled and started.
        set -e
        {{licenseStep}}[ "$(id -u)" = 0 ] || exec sudo "$0" "$@"
        cd "$(dirname "$0")"
        if [ -d /run/systemd/system ]; then :
        {{string.Concat(units.Select(u => $"  systemctl stop {u.Name} 2>/dev/null || true\n"))}}fi
        # into /lib, /bin, /sbin as they are: on a merged-/usr system each is a symlink to usr/, and an archive's
        # directory entry for one must not replace it with a directory (tar does unless told: every program on the
        # machine then lost its dynamic loader, /lib/ld-linux-*.so)
        tar xzf files.tar.gz -C / --no-same-owner --keep-directory-symlink
        mkdir -p "{{root}}"
        tar tzf files.tar.gz | sed -e 's|^\.||' > "{{root}}/installed-files.txt"
        cp uninstall.sh "{{root}}/uninstall.sh"
        chmod 755 "{{root}}/uninstall.sh"
        {{PostInstBody(root, state, units, masked, accounts)}}
        {{sqlServerStep}}
        echo "{{pkg}} installed into {{root}}"

        """;

    internal static string UninstallScript(string pkg, string root, string state, List<(string Name, bool Enable)> units, List<string> masked,
        string sqlServerStep, bool webView) => $$"""
        #!/bin/sh
        # Removes {{pkg}} as installed by install.sh: its services, its files, its caches and logs (the machine's and
        # every user's), and everything else it put on the machine: its state ({{state}}), the service accounts it
        # made, what it installed of WPE WebKit -- unless --keep-data is given, which keeps the state for a later install.
        [ "$(id -u)" = 0 ] || exec sudo "$0" "$@"
        ALL=1; for a in "$@"; do [ "$a" = --keep-data ] && ALL=; done
        if [ -d /run/systemd/system ]; then :
        {{string.Concat(units.Select(u => $"  systemctl disable --now {u.Name} 2>/dev/null || true\n"))}}fi
        {{PreRmBody(units, root)}}
        {{sqlServerStep}}
        if [ -f "{{root}}/installed-files.txt" ]; then
          while IFS= read -r f; do [ -f "$f" ] && rm -f "$f"; done < "{{root}}/installed-files.txt"
          # the folders the files were in, where nothing else is left in them
          sed -e 's|/[^/]*$||' "{{root}}/installed-files.txt" | sort -ru | while IFS= read -r d; do
            case "$d" in ""|/|/bin|/sbin|/lib|/lib64|/usr|/usr/*bin|/usr/lib|/usr/share|/usr/share/applications|/usr/share/icons|/etc|/etc/*.d|/opt|/var|/lib/systemd|/lib/systemd/system) continue;; esac
            rmdir "$d" 2>/dev/null || true
          done
        fi
        rm -rf "{{root}}"
        {{RemovedTogether(pkg, masked)}}
        if [ -n "$ALL" ]; then
        {{Purged(pkg, state, webView, background: false)}}
        fi
        echo "{{pkg}} removed"

        """;

    // what remove and uninstall.sh take besides the package's files: the services its programs created since, its
    // masked units' masks, its caches and logs (the machine's and every user's), and the desktop's caches of its entries
    private static string RemovedTogether(string pkg, List<string> masked) => $$"""
        if [ -d /run/systemd/system ]; then :
          for f in /etc/systemd/system/{{pkg}}-*.service; do
            [ -f "$f" ] && [ ! -L "$f" ] || continue
            systemctl disable --now "$(basename "$f")" >/dev/null 2>&1 || true
            rm -f "$f"
          done
        {{string.Concat(masked.Select(u => $"  systemctl unmask {u} >/dev/null 2>&1 || true\n"))}}  systemctl daemon-reload || true
        fi
        {{Caches(pkg)}}
        rm -rf "/var/log/{{pkg}}"
        for h in /root /home/*; do [ -d "$h/.local/state/{{pkg}}" ] && rm -rf "$h/.local/state/{{pkg}}"; done
        command -v update-desktop-database >/dev/null && update-desktop-database -q /usr/share/applications || true
        command -v gtk-update-icon-cache >/dev/null && gtk-update-icon-cache -q /usr/share/icons/hicolor || true
        """;

    // purge's and uninstall.sh's (unless --keep-data): the machine's state, the inotify limits, the service accounts (where no other unit runs as
    // one), what the install added of WPE WebKit
    private static string Purged(string pkg, string state, bool webView, bool background) => $$"""
        {{(webView ? Indent(WebViewPurge(pkg, WebViewRecord(state), background), "  ") : "")}}
          rm -rf "{{Path.GetDirectoryName(state)}}" "/etc/sysctl.d/60-{{SysctlName(state)}}.conf"
          for a in {{DesktopCommon.SystemAccount(ServiceAccount.NetworkService, mac: false)}} {{DesktopCommon.SystemAccount(ServiceAccount.LocalService, mac: false)}}; do
            getent passwd "$a" >/dev/null || continue
            grep -qsx "User=$a" /lib/systemd/system/*.service /etc/systemd/system/*.service && continue
            userdel "$a" >/dev/null 2>&1 || deluser --system "$a" >/dev/null 2>&1 || true
          done
        """;

    private static string SysctlName(string state) => Path.GetFileName(Path.GetDirectoryName(state))!.ToLowerInvariant();

    // ---- what is installed ---------------------------------------------------------------------------------

    // a daemon of the stack's own (wpfrun <command>: the http.sys service, the service manager), root, always running
    private static string SystemUnit(PackageManifest manifest, string pkg, string root, string state, string command, string title) => $"""
        [Unit]
        Description={title} ({manifest.Product.Name})
        After=network-online.target
        Wants=network-online.target

        [Service]
        Type=simple
        ExecStart={Quote(root + "/runtime/dotnet/dotnet")} {Quote(root + "/runtime/launcher/wpfrun.dll")} {command}
        Environment=WPFRUN_STATE={state}
        Environment=WPFRUN_CACHE=/var/cache/{pkg}
        Environment=WPFRUN_LOGS=/var/log/{pkg}
        Restart=always
        RestartSec=5
        SyslogIdentifier={pkg}-{command}

        [Install]
        WantedBy=multi-user.target

        """;

    private static string Unit(PackageManifest manifest, ServiceEntry service, string pkg, string root, string state) =>
        WpfWebGpu.Services.ServiceUnits.Systemd(Host(manifest, pkg, root, state), new WpfWebGpu.Services.ServiceDefinition
        {
            Unit = UnitName(pkg, service.Name),
            Title = service.DisplayName.Length > 0 ? service.DisplayName : service.Name,
            Program = root + "/app/" + service.Exe.Replace('\\', '/'),
            Args = new List<string>(service.Args),
            WorkingDirectory = root + "/app/" + Path.GetDirectoryName(service.Exe.Replace('\\', '/'))!,
            Environment = DesktopCommon.ServiceEnvironment(service, manifest.Product.OsIdentity),
            Account = DesktopCommon.SystemAccount(service.Account, mac: false),
            Start = DesktopCommon.StartOf(manifest, service),
            RestartAfterFailure = service.RestartAfterFailure,
            DependsOn = service.DependsOn.Select(d => UnitName(pkg, d)).ToList(),
            LogName = Path.GetFileNameWithoutExtension(service.Exe),
        });

    /// <summary>The installation as its services run in it (services.json keeps it, for those created later).</summary>
    private static WpfWebGpu.Services.ServiceHost Host(PackageManifest manifest, string pkg, string root, string state) => new()
    {
        Root = root, State = state, Cache = "/var/cache/" + pkg, Logs = "/var/log/" + pkg,
        Product = manifest.Product.Name, UnitPrefix = pkg + "-", Environment = DesktopCommon.HostEnvironment(manifest.Product.OsIdentity),
    };

    // what a user starts of the installation: the machine's state, and the user's own files of this product under its
    // name (XDG's cache and state folders), where an upgrade and the removal find them
    private static string Launcher(string what, string pkg, string state, string osIdentity, string exec, string? desktopId = null) => $$"""
        #!/bin/sh
        # {{what}}: the Windows program run on this machine (WPF and WinForms on WebGPU)
        export WPFRUN_STATE="{{state}}"{{(osIdentity.Length > 0 ? $"\nexport WPFWEBGPU_OS_IDENTITY={osIdentity}" : "")}}{{(desktopId != null ? $"\nexport WPFRUN_DESKTOP_ID=\"{desktopId}\"" : "")}}
        export WPFRUN_CACHE="${XDG_CACHE_HOME:-$HOME/.cache}/{{pkg}}"
        export WPFRUN_LOGS="${XDG_STATE_HOME:-$HOME/.local/state}/{{pkg}}"
        {{exec}}

        """;

    // the app's assembly name, as AppDomain.FriendlyName has it: the app id its windows give the compositor unless its
    // launcher names the entry (WPFRUN_DESKTOP_ID), and the entry's StartupWMClass, by which GNOME also finds the
    // entry -- its icon, its name -- for a window (without either, the dock showed a generic gear for every app)
    private static string WindowClass(PackageManifest manifest, AppEntry app)
    {
        string exe = Path.Combine(manifest.Inputs.InstallDir, app.Exe);
        return Path.GetFileNameWithoutExtension(AppHost.AppDll(exe) ?? exe).Replace(' ', '-');
    }

    private static string Desktop(AppEntry app, string icon, string launcher, string windowClass) => $"""
        [Desktop Entry]
        Type=Application
        Name={DesktopCommon.AppName(app)}
        Exec={Quote(launcher)}
        Icon={icon}
        StartupWMClass={windowClass}
        Terminal=false
        Categories=Utility;

        """;

    /// <summary>A menu entry's uninstaller: asked first (zenity, or kdialog, else in a terminal), then its remover run
    /// as an administrator through pkexec -- which says, by the remover's polkit action, what the password is for.</summary>
    internal static string GuiUninstaller(string title, string question, string done, string remover) => $$"""
        #!/bin/sh
        # {{title}}: asked first, then removed as an administrator
        T="{{title}}"
        Q="{{question}}"
        if command -v zenity >/dev/null 2>&1; then
          answer=$(zenity --question --title="$T" --icon=dialog-warning --text="$Q" --ok-label="Remove Everything" \
            --cancel-label="Cancel" --extra-button="Keep Data" 2>/dev/null) && answer=all
        elif command -v kdialog >/dev/null 2>&1; then
          kdialog --title "$T" --warningyesnocancel "$Q" --yes-label "Remove Everything" --no-label "Keep Data"
          case $? in 0) answer=all;; 1) answer="Keep Data";; *) answer=;; esac
        else
          # no dialog to ask with: the removal in a terminal, asked there
          for t in x-terminal-emulator konsole gnome-terminal xterm; do
            command -v $t >/dev/null 2>&1 && exec $t -e sh -c "printf '%s\n\n' \"\$0\"; printf 'Keep the data? [y/N] '; read k; case \"\$k\" in [Yy]*) sudo '{{remover}}' --keep-data;; *) sudo '{{remover}}';; esac; printf 'Press Enter to close. '; read x" "$Q"
          done
          exit 1
        fi
        case "$answer" in
          all) set -- ;;
          "Keep Data") set -- --keep-data ;;
          *) exit 0 ;;
        esac
        log=$(mktemp)
        pkexec "{{remover}}" "$@" >"$log" 2>&1
        status=$?
        if [ $status = 126 ] || [ $status = 127 ]; then rm -f "$log"; exit 0; fi   # (the password not given)
        if command -v zenity >/dev/null 2>&1; then
          if [ $status = 0 ]; then zenity --info --title="$T" --text="{{done}}" 2>/dev/null
          else zenity --text-info --title="$T: it failed" --filename="$log" 2>/dev/null; fi
        elif command -v kdialog >/dev/null 2>&1; then
          if [ $status = 0 ]; then kdialog --title "$T" --msgbox "{{done}}"; else kdialog --title "$T" --textbox "$log"; fi
        fi
        rm -f "$log"
        exit 0

        """;

    /// <summary>The remover the uninstaller runs as an administrator: apt where the package manager installed it
    /// (purge, or remove to keep the data), else the uninstall script the .tar.gz wrote (with --keep-data).</summary>
    internal static string Remover(string what, string package, string script) => $$"""
        #!/bin/sh
        # Removes {{what}} (its menu entry's uninstaller runs this as an administrator, through pkexec)
        keep=; [ "$1" = --keep-data ] && keep=1
        if dpkg-query -W -f='${Status}' {{package}} 2>/dev/null | grep -q 'ok installed'; then
          if [ -n "$keep" ]; then what=remove; else what=purge; fi
          DEBIAN_FRONTEND=noninteractive exec /usr/bin/apt-get -y -o DPkg::Lock::Timeout=300 $what {{package}}
        elif [ -x "{{script}}" ]; then
          if [ -n "$keep" ]; then exec "{{script}}" --keep-data; else exec "{{script}}"; fi
        fi
        echo "{{what}} is not installed" >&2
        exit 1

        """;

    /// <summary>The remover's polkit action: pkexec asks for an administrator's password in these words, not with the
    /// command line.</summary>
    internal static string RemoverPolicy(string action, string what, string remover) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE policyconfig PUBLIC "-//freedesktop//DTD PolicyKit Policy Configuration 1.0//EN"
          "http://www.freedesktop.org/standards/PolicyKit/1/policyconfig.dtd">
        <policyconfig>
          <action id="{X(action)}">
            <description>Uninstall {X(what)}</description>
            <message>Authentication is required to uninstall {X(what)}</message>
            <icon_name>edit-delete</icon_name>
            <defaults>
              <allow_any>auth_admin</allow_any>
              <allow_inactive>auth_admin</allow_inactive>
              <allow_active>auth_admin</allow_active>
            </defaults>
            <annotate key="org.freedesktop.policykit.exec.path">{X(remover)}</annotate>
          </action>
        </policyconfig>

        """;

    private static string X(string s) => System.Security.SecurityElement.Escape(s) ?? "";

    internal static string UninstallerEntry(string name, string program) => $"""
        [Desktop Entry]
        Type=Application
        Name={name}
        Exec={Quote(program)}
        Icon=edit-delete
        Terminal=false
        Categories=System;

        """;

    private static string Autostart(PackageManifest manifest, string launcher) => $"""
        [Desktop Entry]
        Type=Application
        Name={manifest.Product.Name} startup programs
        Exec={Quote(launcher)}
        NoDisplay=true
        Terminal=false
        X-GNOME-Autostart-enabled=true

        """;

    private static string Control(PackageManifest manifest, string pkg, string arch, long bytes, bool sqlServer, bool webView, bool license) => $"""
        Package: {pkg}
        Version: {DebVersion(manifest.Product.Version)}
        Architecture: {arch}
        Maintainer: {(manifest.Product.Publisher.Length > 0 ? manifest.Product.Publisher : manifest.Product.Name)} <noreply@localhost>
        Installed-Size: {Math.Max(1, bytes / 1024)}{(license ? "\nPre-Depends: debconf (>= 0.5) | debconf-2.0" : "")}
        Depends: libc6, zlib1g, libstdc++6, libgcc-s1, libssl3 | libssl1.1, libicu78 | libicu77 | libicu76 | libicu75 | libicu74 | libicu72 | libicu70 | libicu67 | libicu66, acl
        Recommends: libvulkan1, mesa-vulkan-drivers, libwayland-client0, libxkbcommon0, libfontconfig1{(sqlServer ? ", " + SqlServerPackageName(manifest) : "")}{(webView ? ", debian-archive-keyring, " + WpePackages.Replace(" ", ", ") : "")}
        Section: misc
        Priority: optional
        Description: {manifest.Product.Name}
         The Windows installation, run on Linux by WPF and WinForms on WebGPU.

        """;

    // the machine's service accounts the services run as (NETWORK SERVICE, LOCAL SERVICE), made where missing, in the
    // group the state is writable by and the one the system journal is readable by; the services' logs and caches
    // writable by them
    private static string Accounts(PackageManifest manifest, string pkg)
    {
        var made = manifest.Services.Where(x => x.Start != StartType.Disabled).Select(x => x.Account).Distinct()
            .Where(a => a != ServiceAccount.LocalSystem).ToList();
        var sb = new System.Text.StringBuilder();
        foreach (var a in made)
        {
            string user = DesktopCommon.SystemAccount(a, mac: false)!;
            sb.Append($"getent passwd {user} >/dev/null || useradd --system --no-create-home --home-dir /nonexistent --shell /usr/sbin/nologin --gid users --comment \"{DesktopCommon.AccountTitle(a)}\" {user} "
                + $"|| adduser --system --no-create-home --home /nonexistent --shell /usr/sbin/nologin --ingroup users {user}\n");
            // the machine's log readable by it, as every account reads Windows' Application log (the event log's
            // entries are journald's: EventLog reads them back from there)
            sb.Append($"getent group systemd-journal >/dev/null && usermod -a -G systemd-journal {user} || true\n");
        }
        sb.Append($"mkdir -p /var/log/{pkg} /var/cache/{pkg}\n");
        sb.Append($"chgrp -R users /var/log/{pkg} /var/cache/{pkg} 2>/dev/null || true\n");
        sb.Append($"chmod -R g+rwX /var/log/{pkg} /var/cache/{pkg}\n");
        sb.Append($"find /var/log/{pkg} /var/cache/{pkg} -type d -exec chmod g+s {{}} + 2>/dev/null || true");
        return sb.ToString();
    }

    internal static string PostInst(string root, string state, List<(string Name, bool Enable)> units, List<string> masked, string accounts,
        string webViewStep) =>
        "#!/bin/sh\n# the machine's state made from the seed on the first install (the registry's paths this installation's) and\n"
        + "# kept on an upgrade; then the services enabled and started\nset -e\n" + PostInstBody(root, state, units, masked, accounts) + webViewStep + "exit 0\n";

    private static string PostInstBody(string root, string state, List<(string Name, bool Enable)> units, List<string> masked, string accounts) => $$"""
        STATE="{{state}}"
        SEED="{{root}}/seed"
        mkdir -p "$STATE"
        if [ ! -d "$STATE/registry" ] && [ -d "$SEED/registry" ]; then
          mkdir -p "$STATE/registry"
          for f in "$SEED"/registry/*.reg; do
            [ -f "$f" ] || continue
            sed -e "s|\${WPFRUN_INSTALLDIR}|{{root}}/app|g" -e "s|\${WPFRUN_PROGRAMDATA}|$STATE/ProgramData|g" \
                -e "s|\${WPFRUN_APPDIR}|{{root}}/app|g" "$f" > "$STATE/registry/$(basename "$f")"
          done
        fi
        if [ ! -d "$STATE/ProgramData" ] && [ -d "$SEED/ProgramData" ]; then cp -a "$SEED/ProgramData" "$STATE/ProgramData"; fi
        # where the installation's Windows folders are, for the paths the application names itself: this version's
        if [ -f "$SEED/paths.json" ]; then
          sed -e "s|\${WPFRUN_INSTALLDIR}|{{root}}/app|g" -e "s|\${WPFRUN_PROGRAMDATA}|$STATE/ProgramData|g" \
              -e "s|\${WPFRUN_APPDIR}|{{root}}/app|g" "$SEED/paths.json" > "$STATE/paths.json"
        fi
        # the service manager's database: the services this version registers, replaced on every install
        if [ -f "{{root}}/services.json" ]; then cp "{{root}}/services.json" "$STATE/services.json"; fi
        {{DesktopCommon.IisInstallStep(root)}}
        # every user's, as Windows' ProgramData and HKLM are writable by its users' programs: an inherited access list
        # (as the product's own folder has on Windows), so what any account makes there -- a display a user saves, a
        # file the elevated setup writes -- stays every account's whatever its umask. Ubuntu puts no desktop user in
        # group users, so the group alone left a user's save denied. Without ACLs, the files as they are.
        chgrp -R users "$STATE" 2>/dev/null || true
        chmod -R g+rwX "$STATE"
        find "$STATE" -type d -exec chmod g+s {} + 2>/dev/null || true
        if command -v setfacl >/dev/null 2>&1 && setfacl -R -m u::rwX,g::rwX,o::rwX "$STATE" 2>/dev/null; then
          find "$STATE" -type d -exec setfacl -d -m u::rwX,g::rwX,o::rwX {} + 2>/dev/null || true
        else
          chmod -R o+rwX "$STATE"
        fi
        {{accounts}}
        if [ -d /run/systemd/system ]; then :
          systemctl daemon-reload || true
        {{string.Concat(units.Select(u => $"  systemctl unmask {u.Name} >/dev/null 2>&1 || true\n"))}}{{string.Concat(units.Where(u => u.Enable).Select(u => $"  systemctl enable --now {u.Name} || true\n"))}}{{string.Concat(masked.Select(u => $"  systemctl mask {u} || true\n"))}}fi
        # a service per program, each watching its configuration: more inotify instances than Linux allows a user by
        # default (128); Windows has no such limit
        printf 'fs.inotify.max_user_instances = 8192\nfs.inotify.max_user_watches = 524288\n' > /etc/sysctl.d/60-{{SysctlName(state)}}.conf
        sysctl -q -p /etc/sysctl.d/60-{{SysctlName(state)}}.conf || true
        command -v update-desktop-database >/dev/null && update-desktop-database -q /usr/share/applications || true
        command -v gtk-update-icon-cache >/dev/null && gtk-update-icon-cache -q /usr/share/icons/hicolor || true

        """;

    // the product's caches: the machine's (its services') and each user's (its apps'), which a new version would only
    // make again beside the old ones
    private static string Caches(string pkg) => $$"""
        rm -rf "/var/cache/{{pkg}}"
        for h in /root /home/*; do [ -d "$h/.cache/{{pkg}}" ] && rm -rf "$h/.cache/{{pkg}}"; done
        """;

    internal static string PreInst(string pkg, string licenseStep = "") => $$"""
        #!/bin/sh
        {{licenseStep}}
        # an upgrade: what the previous version prepared for its programs goes
        if [ "$1" = upgrade ]; then
        {{Caches(pkg)}}
        fi
        exit 0

        """;

    internal static string PreRm(List<(string Name, bool Enable)> units, string root) => $"""
        #!/bin/sh
        # the services stopped before their files go (an upgrade starts them again in postinst), and what users still
        # run of the installation ended: asked first, then made to
        {PreRmBody(units, root)}
        exit 0

        """;

    // what runs of the installation: its runtime's processes, and the apps users started from the menu, which run
    // from their copies in each user's cache (${XDG_CACHE_HOME:-~/.cache}/<pkg>/apps/<app>-<hash>/, the launcher's)
    private static string PreRmBody(List<(string Name, bool Enable)> units, string root) => $"""
        if [ -d /run/systemd/system ]; then :
        {string.Concat(units.Select(u => $"  systemctl stop --no-block {u.Name} || true\n"))}fi
        RUNNING='{Running(root)}'
        pkill -TERM -f "$RUNNING" 2>/dev/null
        for i in 1 2 3 4 5 6 7 8 9 10; do pgrep -f "$RUNNING" >/dev/null || break; sleep 0.5; done
        pkill -KILL -f "$RUNNING" 2>/dev/null
        """;

    private static string Running(string root) =>
        EreLiteral(root + "/runtime/") + "|/" + EreLiteral(PackageName(Path.GetFileName(root))) + "/apps/[^/ ]+-[0-9a-f]{12}/";

    // text matched as itself in an extended regular expression (pkill's)
    private static string EreLiteral(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text, @"[.^$*+?()\[\]{}|\\]", @"\$0");

    internal static string PostRm(string state, List<(string Name, bool Enable)> units, List<string> masked, string pkg, bool webView, bool license = false) => $"""
        #!/bin/sh
        # remove takes the caches and logs (the machine's and every user's) and keeps the machine's state (the registry,
        # ProgramData), as Windows keeps ProgramData; purge removes that too, the service accounts and what the install
        # added of WPE WebKit
        if [ "$1" = remove ] || [ "$1" = purge ]; then
          if [ -d /run/systemd/system ]; then :
        {string.Concat(units.Select(u => $"    systemctl disable {u.Name} >/dev/null 2>&1 || true\n"))}  fi
        {RemovedTogether(pkg, masked)}
        fi
        if [ "$1" = purge ]; then
        {(license ? LicensePurge(pkg) : "")}{Purged(pkg, state, webView, background: true)}
        fi
        exit 0

        """;

    // ---- names --------------------------------------------------------------------------------------------

    /// <summary>Debian's package name: lower case letters, digits, '+', '-', '.'.</summary>
    public static string PackageName(string product)
    {
        string name = new string(product.ToLowerInvariant().Where(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.').ToArray());
        return name.Length < 2 || !char.IsAsciiLetterOrDigit(name[0]) ? "app" + name : name;
    }

    private static string UnitName(string pkg, string service) => $"{pkg}-{WpfWebGpu.Services.ServiceUnits.Segment(service)}.service";

    private static string DebVersion(string v)
    {
        string s = new string(v.Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '+' or '~' or '-').ToArray());
        return s.Length == 0 || !char.IsAsciiDigit(s[0]) ? "0" + s : s;
    }

    private static string Quote(string s) => s.IndexOfAny(new[] { ' ', '"', '\'', '\\' }) < 0 ? s : "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static bool IsExecutable(string file, string rel)
    {
        if (rel is "run" or "dotnet/dotnet") return true;
        return !OperatingSystem.IsWindows() && (File.GetUnixFileMode(file) & UnixFileMode.UserExecute) != 0;
    }

    private static void ArMember(Stream output, string name, Stream content)
    {
        long size = content.Length;
        string header = $"{name,-16}{0,-12}{0,-6}{0,-6}{"100644",-8}{size,-10}`\n";
        output.Write(Encoding.ASCII.GetBytes(header));
        content.CopyTo(output);
        if (size % 2 == 1) output.WriteByte((byte)'\n');
    }

    /// <summary>A tar of absolute paths, written as ./path with their parent folders first, owned by root.</summary>
    private sealed class TarTree(Stream output, bool directories = true)
    {
        private readonly TarWriter _tar = new(output, TarEntryFormat.Gnu, leaveOpen: true);
        private readonly HashSet<string> _dirs = new(StringComparer.Ordinal);
        private static readonly DateTimeOffset s_time = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private const UnixFileMode File644 = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
        private const UnixFileMode Exec755 = File644 | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

        public long AddFile(string source, string path, bool executable)
        {
            using var s = File.OpenRead(source);
            Add(path, s, executable);
            return s.Length;
        }

        public void AddText(string path, string text, bool executable) =>
            Add(path, new MemoryStream(Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n"))), executable);

        public void AddBytes(string path, byte[] bytes) => Add(path, new MemoryStream(bytes), executable: false);

        private void Add(string path, Stream content, bool executable)
        {
            string name = path.StartsWith("./", StringComparison.Ordinal) ? path : "." + path;
            if (directories) Parents(name);
            var entry = new GnuTarEntry(TarEntryType.RegularFile, name)
            {
                Mode = executable ? Exec755 : File644, Uid = 0, Gid = 0, UserName = "root", GroupName = "root",
                ModificationTime = s_time, DataStream = content,
            };
            _tar.WriteEntry(entry);
        }

        private void Parents(string name)
        {
            int slash = name.LastIndexOf('/');
            if (slash <= 1) return;
            string dir = name[..slash];
            if (_dirs.Contains(dir)) return;
            Parents(dir);
            _dirs.Add(dir);
            _tar.WriteEntry(new GnuTarEntry(TarEntryType.Directory, dir + "/")
            {
                Mode = Exec755, Uid = 0, Gid = 0, UserName = "root", GroupName = "root", ModificationTime = s_time,
            });
        }

        public void Finish() => _tar.Dispose();
    }
}
