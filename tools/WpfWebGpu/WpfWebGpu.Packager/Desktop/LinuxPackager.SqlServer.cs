// The installation's SQL Server on Linux, a package of its own (<pkg>-sqlserver) the installation's recommends -- so
// apt installs it with the installation unless told not to (--no-install-recommends, or removing it), as Windows'
// Setup installs SQL Server Express unless it is left out:
//
//   /lib/systemd/system/<pkg>-sqlserver.service   Podman running SQL Server's image (SqlServerImages: SQL Server 2025
//                                            Express on x64, Azure SQL Edge on Arm), its databases in the volume
//                                            <pkg>-sqlserver-data
//   /etc/<pkg>/sqlserver.env                 its sa password, made on the first install, readable by root only
//
// The image is pulled from Microsoft's registry as the package is installed (the package carries none: an image is a
// Linux system, its userland GPL); without the network then, the service pulls it when it starts. It listens on
// 127.0.0.1, as SQL Server Express is reached from the machine itself unless its firewall is opened. Removing the
// package keeps the databases (the volume), as removing SQL Server keeps them on Windows; purging removes them.

using System.IO.Compression;
using WpfWebGpu.Packager.SqlServer;

namespace WpfWebGpu.Packager.Desktop;

public static partial class LinuxPackager
{
    /// <summary>The SQL Server package's name for the installation's.</summary>
    public static string SqlServerPackageName(string pkg) => pkg + "-sqlserver";

    /// <summary>Writes &lt;pkg&gt;-sqlserver_&lt;version&gt;_&lt;arch&gt;.deb.</summary>
    public static string BuildSqlServer(PackageManifest manifest, string arch, string outDir, Action<string> log)
    {
        var sql = SqlServerImage.ForLinux(arch);
        string pkg = PackageName(manifest.Product.Name), spkg = SqlServerPackageName(pkg);
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
                Version: {DebVersion(manifest.Product.Version)}
                Architecture: {arch}
                Maintainer: {(manifest.Product.Publisher.Length > 0 ? manifest.Product.Publisher : manifest.Product.Name)} <noreply@localhost>
                Installed-Size: 4
                Depends: podman
                Section: database
                Priority: optional
                Description: {sql.Engine} for {manifest.Product.Name}
                 The database server {manifest.Product.Name} keeps its configuration in, in a container on
                 127.0.0.1:{manifest.SqlServer.Port}. Its image ({sql.Image}) is pulled from Microsoft's registry
                 when the package is installed.

                """, executable: false);
            tar.AddText("./postinst", "#!/bin/sh\nset -e\n" + SqlServerPostInst(manifest, sql, pkg) + "exit 0\n", executable: true);
            tar.AddText("./prerm", $"#!/bin/sh\n[ -d /run/systemd/system ] && systemctl stop {spkg}.service 2>/dev/null || true\nexit 0\n", executable: true);
            tar.AddText("./postrm", "#!/bin/sh\n" + SqlServerPostRm(sql, pkg) + "exit 0\n", executable: true);
            tar.Finish();
        }

        string deb = Path.Combine(outDir, $"{spkg}_{DebVersion(manifest.Product.Version)}_{arch}.deb");
        using (var output = File.Create(deb))
        {
            output.Write("!<arch>\n"u8);
            ArMember(output, "debian-binary", new MemoryStream("2.0\n"u8.ToArray()));
            using (var c = File.OpenRead(control)) ArMember(output, "control.tar.gz", c);
            using (var d = File.OpenRead(data)) ArMember(output, "data.tar.gz", d);
        }
        Directory.Delete(work, recursive: true);
        log($"{deb}: {sql.Engine} ({sql.Image}, pulled when installed)");
        return deb;
    }

    /// <summary>The package's files as a tar (the .deb's data, the .tar.gz's sqlserver.tar): the unit.</summary>
    internal static void WriteSqlServerFiles(Stream output, PackageManifest manifest, string arch)
    {
        string pkg = PackageName(manifest.Product.Name), spkg = SqlServerPackageName(pkg);
        var tar = new TarTree(output);
        tar.AddText($"/lib/systemd/system/{spkg}.service", SqlServerUnit(manifest, SqlServerImage.ForLinux(arch), pkg), executable: false);
        tar.Finish();
    }

    private static string SqlServerUnit(PackageManifest manifest, SqlServerImage sql, string pkg)
    {
        string spkg = SqlServerPackageName(pkg);
        return $"""
            [Unit]
            Description={sql.Engine} ({manifest.Product.Name})
            Wants=network-online.target
            After=network-online.target

            [Service]
            Type=simple
            EnvironmentFile=/etc/{pkg}/sqlserver.env
            Environment=ACCEPT_EULA={sql.Eula} MSSQL_PID={sql.Pid}
            ExecStartPre=/bin/sh -c 'podman image exists {sql.Image} || podman pull -q --platform {sql.Platform} {sql.Image}'
            ExecStartPre=-/usr/bin/podman rm -f -i {spkg}
            ExecStart=/usr/bin/podman run --rm --name {spkg} --pull=never -p 127.0.0.1:{manifest.SqlServer.Port}:1433 -v {spkg}-data:/var/opt/mssql -e ACCEPT_EULA -e MSSQL_PID -e MSSQL_SA_PASSWORD {sql.Image}
            ExecStop=/usr/bin/podman stop -i -t 30 {spkg}
            Restart=on-failure
            RestartSec=30
            TimeoutStartSec=1800

            [Install]
            WantedBy=multi-user.target

            """;
    }

    // the sa password on the first install (random, meeting SQL Server's policy: letters of both cases, digits, a
    // symbol), the image pulled (or, without the network, left for the service to pull as it starts), the service
    // started; the password said where it can be read again
    private static string SqlServerPostInst(PackageManifest manifest, SqlServerImage sql, string pkg)
    {
        string spkg = SqlServerPackageName(pkg);
        return $$"""
            ENVFILE=/etc/{{pkg}}/sqlserver.env
            mkdir -p /etc/{{pkg}}
            NEW=
            if [ ! -f "$ENVFILE" ]; then
              PW="Sq1-$(tr -dc 'A-Za-z0-9' </dev/urandom | head -c 20)"
              (umask 077; printf 'MSSQL_SA_PASSWORD=%s\n' "$PW" > "$ENVFILE")
              NEW=1
            fi
            chmod 600 "$ENVFILE"
            echo "{{sql.Engine}}: pulling {{sql.Image}} from Microsoft's registry"
            if ! podman pull -q --platform {{sql.Platform}} {{sql.Image}} >/dev/null; then
              echo "  not pulled now (no network?): the service pulls it as it starts" >&2
            fi
            if [ -d /run/systemd/system ]; then
              systemctl daemon-reload || true
              systemctl enable {{spkg}}.service || true
              systemctl start --no-block {{spkg}}.service || true
            fi
            echo "{{sql.Engine}} for {{manifest.Product.Name}}: 127.0.0.1,{{manifest.SqlServer.Port}} (login sa)"
            if [ -n "$NEW" ]; then echo "  sa password: $PW"; fi
            echo "  the password is kept in $ENVFILE (readable by root)"
            echo "  point {{manifest.Product.Name}}'s SQL configuration at 127.0.0.1,{{manifest.SqlServer.Port}} with that login"

            """;
    }

    // remove keeps the databases and the password (a reinstall finds them), as Windows keeps SQL Server's databases
    // when it is uninstalled; purge removes them, the image and the password
    private static string SqlServerPostRm(SqlServerImage sql, string pkg)
    {
        string spkg = SqlServerPackageName(pkg);
        return $$"""
            if [ "$1" = remove ] || [ "$1" = purge ]; then
              [ -d /run/systemd/system ] && { systemctl disable {{spkg}}.service >/dev/null 2>&1 || true; systemctl daemon-reload || true; }
              command -v podman >/dev/null && podman rm -f -i {{spkg}} >/dev/null 2>&1 || true
            fi
            if [ "$1" = purge ]; then
              if command -v podman >/dev/null; then
                podman volume rm -f {{spkg}}-data >/dev/null 2>&1 || true
                podman rmi -f {{sql.Image}} >/dev/null 2>&1 || true
              fi
              rm -f /etc/{{pkg}}/sqlserver.env
              rmdir /etc/{{pkg}} 2>/dev/null || true
            fi

            """;
    }

    // install.sh's part: SQL Server too, unless the person installing says no (or passes --no-sqlserver); without a
    // terminal to ask, it is installed, as apt installs the package by default
    private static string SqlServerInstallStep(PackageManifest manifest, string arch, string pkg)
    {
        var sql = SqlServerImage.ForLinux(arch);
        return $$"""
            SQL=
            for a in "$@"; do case "$a" in --no-sqlserver) SQL=no;; --sqlserver) SQL=yes;; esac; done
            if [ -z "$SQL" ]; then
              SQL=yes
              if [ -t 0 ]; then
                printf 'Install {{sql.Engine}} for {{manifest.Product.Name}}'"'"'s databases (downloaded from Microsoft)? [Y/n] '
                read -r answer || answer=
                case "$answer" in [Nn]*) SQL=no;; esac
              fi
            fi
            if [ "$SQL" = yes ]; then
              if ! command -v podman >/dev/null; then
                echo "{{sql.Engine}} needs Podman: install it (apt install podman, dnf install podman, ...) and run install.sh again" >&2
              else
                tar xf sqlserver.tar -C / --no-same-owner --keep-directory-symlink
                tar tf sqlserver.tar | sed -e 's|^\.||' >> "{{"/opt/" + manifest.Product.Name}}/installed-files.txt"
            {{Indent(SqlServerPostInst(manifest, sql, pkg), "    ")}}
              fi
            fi

            """;
    }

    // uninstall.sh's part: the service stopped and its container gone (its files go with the rest of the list); the
    // databases and the password kept unless --all
    private static string SqlServerUninstallStep(string arch, string pkg)
    {
        var sql = SqlServerImage.ForLinux(arch);
        string spkg = SqlServerPackageName(pkg);
        return $$"""
            if [ -f /lib/systemd/system/{{spkg}}.service ]; then
              [ -d /run/systemd/system ] && systemctl disable --now {{spkg}}.service 2>/dev/null || true
              command -v podman >/dev/null && podman rm -f -i {{spkg}} >/dev/null 2>&1 || true
              if [ "$1" = "--all" ] && command -v podman >/dev/null; then
                podman volume rm -f {{spkg}}-data >/dev/null 2>&1 || true
                podman rmi -f {{sql.Image}} >/dev/null 2>&1 || true
                rm -f /etc/{{pkg}}/sqlserver.env; rmdir /etc/{{pkg}} 2>/dev/null || true
              fi
            fi

            """;
    }

    private static string Indent(string text, string by) =>
        string.Join("\n", text.Split('\n').Select(l => l.Length > 0 ? by + l : l));
}
