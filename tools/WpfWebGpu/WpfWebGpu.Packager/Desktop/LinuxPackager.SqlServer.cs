// SQL Server on Linux: one instance of a name on the machine, shared by every installation that names it, as on
// Windows -- where a second product's setup finds the instance (MSSQL$SQLEXPRESS) already there and uses it, and
// uninstalling a product leaves SQL Server installed (it has its own entry in Apps & features). Its package,
// wpfwebgpu-sqlserver-<instance>, is the installations' recommendation, so apt installs it with the first of them
// unless told not to (--no-install-recommends), and removing an installation leaves it:
//
//   /lib/systemd/system/wpfwebgpu-sqlserver-<instance>.service   Podman running SQL Server's image (SqlServerImages:
//                                            SQL Server 2025 Express on x64, Azure SQL Edge on Arm) as the container
//                                            sqlserver-<instance>
//   /etc/wpfwebgpu/sqlserver/<instance>.env  its sa password, port and databases' volume, made on the first install,
//                                            readable by root only
//   /var/lib/wpfwebgpu/sqlbrowser/<instance> "<instance> <port>", for the SQL Server Browser
//
// The port is the installation's configured one (1433) where it is free, else the next free one -- as a named
// instance on Windows takes a port of its own -- and the SQL Server Browser (every installation's <pkg>-sqlbrowser,
// the installation's own wpfrun sqlbrowser, as nobody, which answers UDP 1434 for all) names it to a client that
// names the instance ((local)\SQLEXPRESS). The image is pulled from Microsoft's registry as the package is installed
// (the package carries none: an image is a Linux system, its userland GPL); without the network then, the service
// pulls it when it starts. It listens on 127.0.0.1, as SQL Server Express is reached from the machine itself unless
// its firewall is opened. Removing the package keeps the databases (the volume) and the password, as removing SQL
// Server keeps the databases' files on Windows; purging removes them.
//
// Earlier versions brought an installation's own SQL Server (<pkg>-sqlserver); the instance's package replaces it,
// taking its databases and password.

using System.IO.Compression;
using WpfWebGpu.Packager.SqlServer;

namespace WpfWebGpu.Packager.Desktop;

public static partial class LinuxPackager
{
    /// <summary>The SQL Server package of the instance the installation names (one on the machine for every
    /// installation naming it).</summary>
    public static string SqlServerPackageName(PackageManifest manifest) => "wpfwebgpu-sqlserver-" + DesktopCommon.SqlInstanceKey(manifest.SqlServer.Instance);

    /// <summary>The installation's own SQL Server package of earlier versions, which the instance's replaces.</summary>
    internal static string EarlierSqlServerPackageName(string pkg) => pkg + "-sqlserver";

    /// <summary>The installation's SQL Server Browser unit.</summary>
    public static string SqlBrowserUnitName(string pkg) => pkg + "-sqlbrowser";

    /// <summary>The instances every installation's SQL Server Browser answers for, a file an instance.</summary>
    public const string SqlBrowserInstances = "/var/lib/wpfwebgpu/sqlbrowser";

    /// <summary>The instances' passwords, ports and volumes, a file an instance.</summary>
    public const string SqlServerConfig = "/etc/wpfwebgpu/sqlserver";

    // the instance's package is the same whichever installation brings it: its version is its own, not a product's
    private const string SqlServerPackageVersion = "2025.1";

    private static string SqlServerUnitName(string key) => "wpfwebgpu-sqlserver-" + key;

    // the instance's menu entry and the program it runs
    private static string SqlUninstallProgram(string key) => $"/usr/lib/wpfwebgpu/sqlserver-{key}-uninstall";
    private static string SqlUninstallEntry(string key) => $"/usr/share/applications/wpfwebgpu-sqlserver-{key}-uninstall.desktop";

    /// <summary>Writes wpfwebgpu-sqlserver-&lt;instance&gt;_&lt;version&gt;_&lt;arch&gt;.deb.</summary>
    public static string BuildSqlServer(PackageManifest manifest, string arch, string outDir, Action<string> log)
    {
        var sql = SqlServerImage.ForLinux(arch);
        string pkg = PackageName(manifest.Product.Name), spkg = SqlServerPackageName(manifest), old = EarlierSqlServerPackageName(pkg);
        string key = DesktopCommon.SqlInstanceKey(manifest.SqlServer.Instance);
        string work = Path.Combine(outDir, ".work", "linux-sqlserver-" + arch);
        if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        Directory.CreateDirectory(work);

        string data = Path.Combine(work, "data.tar.gz");
        using (var file = File.Create(data))
        using (var gz = new GZipStream(file, CompressionLevel.Optimal))
            WriteSqlServerFiles(gz, manifest, arch);
        string control = Path.Combine(work, "control.tar.gz");
        using (var file = File.Create(control))
        using (var gz = new GZipStream(file, CompressionLevel.Optimal))
        {
            var tar = new TarTree(gz, directories: false);
            tar.AddText("./control", $"""
                Package: {spkg}
                Version: {SqlServerPackageVersion}
                Architecture: {arch}
                Maintainer: {(manifest.Product.Publisher.Length > 0 ? manifest.Product.Publisher : manifest.Product.Name)} <noreply@localhost>
                Installed-Size: 4
                Depends: podman
                Conflicts: {old}
                Replaces: {old}
                Section: database
                Priority: optional
                Description: {sql.Engine}, instance {manifest.SqlServer.Instance}
                 The SQL Server instance {manifest.SqlServer.Instance}, in a container on 127.0.0.1, for every
                 installation that names it. Its image ({sql.Image}) is pulled from Microsoft's registry when the
                 package is installed.

                """, executable: false);
            tar.AddText("./postinst", "#!/bin/sh\nset -e\n" + SqlServerPostInst(manifest, sql, pkg) + "exit 0\n", executable: true);
            tar.AddText("./prerm", $"#!/bin/sh\n[ -d /run/systemd/system ] && systemctl stop {SqlServerUnitName(key)}.service 2>/dev/null || true\nexit 0\n", executable: true);
            tar.AddText("./postrm", "#!/bin/sh\n" + SqlServerPostRm(manifest, sql) + "exit 0\n", executable: true);
            tar.Finish();
        }

        string deb = Path.Combine(outDir, $"{spkg}_{SqlServerPackageVersion}_{arch}.deb");
        using (var output = File.Create(deb))
        {
            output.Write("!<arch>\n"u8);
            ArMember(output, "debian-binary", new MemoryStream("2.0\n"u8.ToArray()));
            using (var c = File.OpenRead(control)) ArMember(output, "control.tar.gz", c);
            using (var d = File.OpenRead(data)) ArMember(output, "data.tar.gz", d);
        }
        Directory.Delete(work, recursive: true);
        log($"{deb}: {sql.Engine}, instance {manifest.SqlServer.Instance} ({sql.Image}, pulled when installed)");
        return deb;
    }

    /// <summary>The package's files as a tar (the .deb's data, the .tar.gz's sqlserver.tar): the unit.</summary>
    internal static void WriteSqlServerFiles(Stream output, PackageManifest manifest, string arch)
    {
        string key = DesktopCommon.SqlInstanceKey(manifest.SqlServer.Instance);
        var tar = new TarTree(output);
        tar.AddText($"/lib/systemd/system/{SqlServerUnitName(key)}.service", SqlServerUnit(manifest, SqlServerImage.ForLinux(arch)), executable: false);
        // its uninstaller in the menu, as SQL Server's own entry in Apps & features on Windows
        string name = manifest.SqlServer.Instance.ToUpperInvariant();
        tar.AddText(SqlUninstallProgram(key), GuiUninstaller("Uninstall SQL Server " + name,
            $"Remove SQL Server's instance {name} from this computer?\n\nThe programs that use it have no database until it is installed again. Its databases can be kept for a later install, or removed with it.",
            SqlServerPackageName(manifest), SqlServerUninstallScript(key)), executable: true);
        tar.AddText(SqlUninstallEntry(key), UninstallerEntry("Uninstall SQL Server " + name, SqlUninstallProgram(key)), executable: false);
        tar.Finish();
    }

    private static string SqlServerUnit(PackageManifest manifest, SqlServerImage sql)
    {
        string key = DesktopCommon.SqlInstanceKey(manifest.SqlServer.Instance);
        return $$"""
            [Unit]
            Description={{sql.Engine}} ({{manifest.SqlServer.Instance}})
            Wants=network-online.target
            After=network-online.target

            [Service]
            Type=simple
            EnvironmentFile={{SqlServerConfig}}/{{key}}.env
            Environment=ACCEPT_EULA={{sql.Eula}} MSSQL_PID={{sql.Pid}}
            ExecStartPre=/bin/sh -c 'podman image exists {{sql.Image}} || podman pull -q --platform {{sql.Platform}} {{sql.Image}}'
            ExecStartPre=-/usr/bin/podman rm -f -i -v sqlserver-{{key}}
            ExecStart=/usr/bin/podman run --rm --name sqlserver-{{key}} --pull=never -p 127.0.0.1:${SQL_PORT}:1433 -v ${SQL_VOLUME}:/var/opt/mssql -e ACCEPT_EULA -e MSSQL_PID -e MSSQL_SA_PASSWORD {{sql.Image}}
            ExecStop=/usr/bin/podman stop -i -t 30 sqlserver-{{key}}
            Restart=on-failure
            RestartSec=30
            TimeoutStartSec=1800

            [Install]
            WantedBy=multi-user.target

            """;
    }

    // the SQL Server Browser, in the installation's package: its own wpfrun, as nobody (allowed UDP 1434, a port below
    // 1024, and nothing else); started once some instance is there to answer for (SQL Server's package starts it then)
    internal static string SqlBrowserUnit(PackageManifest manifest)
    {
        string root = "/opt/" + manifest.Product.Name;
        return $"""
            [Unit]
            Description=SQL Server Browser ({manifest.Product.Name})
            After=network.target
            ConditionDirectoryNotEmpty={SqlBrowserInstances}

            [Service]
            Type=simple
            ExecStart={Quote(root + "/runtime/dotnet/dotnet")} {Quote(root + "/runtime/launcher/wpfrun.dll")} sqlbrowser --instances {SqlBrowserInstances}
            User=nobody
            AmbientCapabilities=CAP_NET_BIND_SERVICE
            CapabilityBoundingSet=CAP_NET_BIND_SERVICE
            NoNewPrivileges=yes
            Environment=DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
            Restart=always
            RestartSec=30

            [Install]
            WantedBy=multi-user.target

            """;
    }

    // the instance made on its first install: the sa password (random, meeting SQL Server's policy: letters of both
    // cases, digits, a symbol), the port (the configured one where free, else the next free one), the databases'
    // volume -- the password and databases of the installation's own SQL Server of before, where there was one. Then
    // the image pulled (or, without the network, left for the service to pull as it starts), the service and the
    // browsers started; where its login can be read said.
    internal static string SqlServerPostInst(PackageManifest manifest, SqlServerImage sql, string pkg)
    {
        string key = DesktopCommon.SqlInstanceKey(manifest.SqlServer.Instance), unit = SqlServerUnitName(key);
        return $$"""
            ENVFILE={{SqlServerConfig}}/{{key}}.env
            mkdir -p {{SqlServerConfig}}
            NEW=
            if [ ! -f "$ENVFILE" ]; then
              NEW=1
              PW=
              VOL=sqlserver-{{key}}-data
              # the installation's own SQL Server of before ({{EarlierSqlServerPackageName(pkg)}}): its password, its databases
              [ -f /etc/{{pkg}}/sqlserver.env ] && PW=$(sed -n 's/^MSSQL_SA_PASSWORD=//p' /etc/{{pkg}}/sqlserver.env)
              # (its databases only with their password: sa's is in them)
              [ -n "$PW" ] && command -v podman >/dev/null && podman volume exists {{pkg}}-sqlserver-data 2>/dev/null && VOL={{pkg}}-sqlserver-data
              [ -n "$PW" ] || PW="Sq1-$(tr -dc 'A-Za-z0-9' </dev/urandom | head -c 20)"
              # the port: the configured one where nothing listens on it and no other instance has it, else the next
              P={{manifest.SqlServer.Port}}
              # (not 1434: the dedicated admin connection's by convention, the browser's on UDP)
              while [ "$P" = 1434 ] || ss -Hltn "sport = :$P" 2>/dev/null | grep -q . || grep -qs " $P\$" {{SqlBrowserInstances}}/*; do P=$((P+1)); done
              (umask 077; printf 'MSSQL_SA_PASSWORD=%s\nSQL_PORT=%s\nSQL_VOLUME=%s\n' "$PW" "$P" "$VOL" > "$ENVFILE")
            fi
            chmod 600 "$ENVFILE"
            rm -f /etc/{{pkg}}/sqlserver.env; rmdir /etc/{{pkg}} 2>/dev/null || true
            # that package, removed for this one, is left to be purged (dpkg's "rc"): its purge would take the volume
            # and the image -- forced, with the container that uses them, now the instance's -- so it purges nothing
            if [ -f /var/lib/dpkg/info/{{EarlierSqlServerPackageName(pkg)}}.postrm ]; then
              printf '#!/bin/sh\n# (its SQL Server is the instance {{manifest.SqlServer.Instance}} now: {{SqlServerPackageName(manifest)}})\nexit 0\n' > /var/lib/dpkg/info/{{EarlierSqlServerPackageName(pkg)}}.postrm
            fi
            PORT=$(sed -n 's/^SQL_PORT=//p' "$ENVFILE")
            # the instance, for the SQL Server Browser of whichever installation answers UDP 1434
            mkdir -p {{SqlBrowserInstances}}
            printf '%s %s\n' "{{manifest.SqlServer.Instance}}" "$PORT" > {{SqlBrowserInstances}}/{{key}}
            chmod 755 {{SqlBrowserInstances}}; chmod 644 {{SqlBrowserInstances}}/{{key}}
            echo "{{sql.Engine}}: pulling {{sql.Image}} from Microsoft's registry"
            if ! podman pull -q --platform {{sql.Platform}} {{sql.Image}} >/dev/null; then
              echo "  not pulled now (no network?): the service pulls it as it starts" >&2
            fi
            if [ -d /run/systemd/system ]; then
              systemctl daemon-reload || true
              systemctl enable {{unit}}.service || true
              systemctl start --no-block {{unit}}.service || true
              for b in $(systemctl list-unit-files --no-legend '*-sqlbrowser.service' 2>/dev/null | awk '{print $1}'); do
                systemctl restart --no-block "$b" || true
              done
            fi
            command -v update-desktop-database >/dev/null && update-desktop-database -q /usr/share/applications || true
            # installed as an installation's recommendation, apt counts it automatic: autoremove would take it once no
            # installation recommends it. It stays, as SQL Server stays on Windows: installed by hand, once apt has
            # let go of its lock
            if dpkg-query -W -f='${Status}' {{SqlServerPackageName(manifest)}} 2>/dev/null | grep -q 'install ok' \
               && [ -d /run/systemd/system ] && command -v systemd-run >/dev/null 2>&1; then
              systemctl reset-failed {{unit}}-manual.service >/dev/null 2>&1 || true
              systemd-run --quiet --collect --unit={{unit}}-manual --description="{{SqlServerPackageName(manifest)}} kept from autoremove" --on-active=2 \
                /usr/bin/apt-mark -o DPkg::Lock::Timeout=1800 manual {{SqlServerPackageName(manifest)}} >/dev/null 2>&1 || true
            fi
            echo "{{sql.Engine}}, instance {{manifest.SqlServer.Instance}}: (local)\\{{manifest.SqlServer.Instance}} or 127.0.0.1,$PORT (login sa)"
            if [ -n "$NEW" ]; then echo "  sa password: $PW"; fi
            echo "  the password is kept in $ENVFILE (readable by root)"

            """;
    }

    // remove keeps the databases and the password (a reinstall finds them), as Windows keeps SQL Server's databases
    // when it is uninstalled; purge removes them, and the image where no other instance needs it
    internal static string SqlServerPostRm(PackageManifest manifest, SqlServerImage sql)
    {
        string key = DesktopCommon.SqlInstanceKey(manifest.SqlServer.Instance), unit = SqlServerUnitName(key);
        return $$"""
            if [ "$1" = remove ] || [ "$1" = purge ]; then
              [ -d /run/systemd/system ] && { systemctl disable {{unit}}.service >/dev/null 2>&1 || true; systemctl daemon-reload || true; }
              command -v podman >/dev/null && podman rm -f -i -v sqlserver-{{key}} >/dev/null 2>&1 || true
              rm -f {{SqlBrowserInstances}}/{{key}}; rmdir {{SqlBrowserInstances}} /var/lib/wpfwebgpu 2>/dev/null || true
            fi
            if [ "$1" = purge ]; then
              ENVFILE={{SqlServerConfig}}/{{key}}.env
              VOL=$(sed -n 's/^SQL_VOLUME=//p' "$ENVFILE" 2>/dev/null)
              if command -v podman >/dev/null; then
                podman volume rm -f "${VOL:-sqlserver-{{key}}-data}" >/dev/null 2>&1 || true
                ls {{SqlServerConfig}}/*.env 2>/dev/null | grep -vqx "$ENVFILE" || podman rmi -f {{sql.Image}} >/dev/null 2>&1 || true
              fi
              rm -f "$ENVFILE"
              rmdir {{SqlServerConfig}} /etc/wpfwebgpu 2>/dev/null || true
            fi

            """;
    }

    // install.sh's part: SQL Server's instance too, unless the person installing says no (or passes --no-sqlserver);
    // without a terminal to ask, it is installed, as apt installs the package by default. Not the installation's
    // files: an uninstall of the installation leaves it; its own uninstall script removes it.
    internal static string SqlServerInstallStep(PackageManifest manifest, string arch, string pkg)
    {
        var sql = SqlServerImage.ForLinux(arch);
        string key = DesktopCommon.SqlInstanceKey(manifest.SqlServer.Instance);
        return $$"""
            SQL=
            for a in "$@"; do case "$a" in --no-sqlserver) SQL=no;; --sqlserver) SQL=yes;; esac; done
            if [ -z "$SQL" ]; then
              SQL=yes
              if [ -t 0 ]; then
                printf 'Install {{sql.Engine}} (instance {{manifest.SqlServer.Instance}}) for {{manifest.Product.Name}}'"'"'s databases (downloaded from Microsoft)? [Y/n] '
                read -r answer || answer=
                case "$answer" in [Nn]*) SQL=no;; esac
              fi
            fi
            if [ "$SQL" = yes ]; then
              if ! command -v podman >/dev/null; then
                echo "{{sql.Engine}} needs Podman: install it (apt install podman, dnf install podman, ...) and run install.sh again" >&2
              else
                tar xf sqlserver.tar -C / --no-same-owner --keep-directory-symlink
            {{Indent(SqlServerPostInst(manifest, sql, pkg), "    ")}}
                cat > {{SqlServerUninstallScript(key)}} <<'UNINSTALL'
            #!/bin/sh
            # Removes SQL Server's instance {{manifest.SqlServer.Instance}}, and its databases unless --keep-data is given.
            if [ "$(id -u)" != 0 ]; then exec sudo "$0" "$@"; fi
            [ -d /run/systemd/system ] && systemctl stop {{SqlServerUnitName(key)}}.service 2>/dev/null
            ALL=1; for a in "$@"; do [ "$a" = --keep-data ] && ALL=; done
            set -- remove
            {{SqlServerPostRm(manifest, sql).Replace("\"$1\" = purge", "-n \"$ALL\"")}}
            rm -f /lib/systemd/system/{{SqlServerUnitName(key)}}.service {{SqlServerUninstallScript(key)}} {{SqlUninstallProgram(key)}} {{SqlUninstallEntry(key)}}
            rmdir /usr/lib/wpfwebgpu 2>/dev/null
            command -v update-desktop-database >/dev/null && update-desktop-database -q /usr/share/applications
            [ -d /run/systemd/system ] && systemctl daemon-reload
            echo "SQL Server's instance {{manifest.SqlServer.Instance}} removed"
            UNINSTALL
                chmod 700 {{SqlServerUninstallScript(key)}}
              fi
            fi

            """;
    }

    private static string SqlServerUninstallScript(string key) => $"{SqlServerConfig}/{key}-uninstall.sh";

    // uninstall.sh's part: SQL Server's instance stays, as Windows leaves SQL Server installed when a product goes
    // (other installations may use it); where its removal is said
    internal static string SqlServerUninstallStep(PackageManifest manifest)
    {
        string key = DesktopCommon.SqlInstanceKey(manifest.SqlServer.Instance);
        return $$"""
            if [ -f {{SqlServerConfig}}/{{key}}.env ]; then
              if [ -x {{SqlServerUninstallScript(key)}} ]; then
                echo "SQL Server's instance {{manifest.SqlServer.Instance}} stays, with its databases: {{SqlServerUninstallScript(key)}} removes it"
              else
                echo "SQL Server's instance {{manifest.SqlServer.Instance}} stays, with its databases: its package ({{SqlServerPackageName(manifest)}}) removes it"
              fi
            fi

            """;
    }

    private static string Indent(string text, string by) =>
        string.Join("\n", text.Split('\n').Select(l => l.Length > 0 ? by + l : l));
}
