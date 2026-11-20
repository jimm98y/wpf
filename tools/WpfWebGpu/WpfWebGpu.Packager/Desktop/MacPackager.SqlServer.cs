// The installation's SQL Server on a Mac: a choice of the installer package, chosen by default ("Customize" leaves it
// out), of two components --
//
//   com.apple.container-installer     Apple's container (sqlserver-macos.pack: its signed installer), which runs Linux
//                                     containers in lightweight VMs; macOS 26 on Apple silicon
//   <id>.sqlserver                    the daemon that runs SQL Server 2025 Express in it, in Rosetta:
//       /Library/LaunchDaemons/<id>-sqlserver.plist
//       /Library/Application Support/<Product>/sqlserver/run.sh
//       /Library/Application Support/<Product>/sqlserver/sqlserver.env   its sa password (root only), and
//                                                         sqlserver.txt for the Mac's administrators to read
//
// Its postinstall starts container (which fetches its own Linux kernel then), pulls Microsoft's image -- carried by
// neither: a Linux system and kernel are GPL -- and starts the daemon, which keeps the container running (container's
// own run command does not stop the container when it is ended) and stops it when launchd stops the daemon. The
// databases are in a volume of container's; an uninstall removes them, and the password, unless --keep-data.
// The daemon's label is outside the product's <id>.* -- which an upgrade of the installation stops and removes --
// so that the database server outlives the installation's own upgrades.

using System.Xml.Linq;
using WpfWebGpu.Packager.SqlServer;

namespace WpfWebGpu.Packager.Desktop;

public static partial class MacPackager
{
    private static string SqlServerLabel(string id) => id + "-sqlserver";

    /// <summary>Windows' SQL Server Browser for the instance (wpfrun sqlbrowser): a client naming it
    /// ((local)\SQLEXPRESS) asks UDP 1434 for its port. Its label is outside &lt;id&gt;.* as SQL Server's is.</summary>
    private static string SqlBrowserLabel(string id) => id + "-sqlbrowser";

    /// <summary>Where every installation's browser finds the instances (a file each, "instance port").</summary>
    internal const string SqlBrowserInstances = "/Library/Application Support/WpfWebGpu/SqlBrowser";

    /// <summary>The name of SQL Server's container (and, with -data, its volume's): container takes none but lower case
    /// letters, digits and '-' to 63 characters (a launchd label of a long id is none: "not a valid container ID").</summary>
    internal static string SqlServerContainer(string product)
    {
        var sb = new System.Text.StringBuilder();
        foreach (char c in product.ToLowerInvariant())
            if (char.IsAsciiLetterOrDigit(c)) sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        string name = sb.ToString().Trim('-');
        if (name.Length > 48) name = name[..48].TrimEnd('-');
        return (name.Length == 0 ? "app" : name) + "-sqlserver";
    }

    /// <summary>Writes the SQL Server component (&lt;id&gt;.sqlserver) into <paramref name="work"/> and returns it.</summary>
    private static string BuildSqlServerComponent(PackageManifest manifest, string id, string root, string work, out long kbytes, Action<string> log)
    {
        var sql = SqlServerImage.X64;
        string label = SqlServerLabel(id), dir = root + "/sqlserver";
        string payload = Path.Combine(work, "sqlserver-root"), scripts = Path.Combine(work, "sqlserver-scripts");
        Directory.CreateDirectory(Path.Combine(payload, "Library", "LaunchDaemons"));
        Directory.CreateDirectory(Path.Combine(payload, dir.TrimStart('/')));
        Directory.CreateDirectory(scripts);
        File.WriteAllText(Path.Combine(payload, "Library", "LaunchDaemons", label + ".plist"), $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>Label</key><string>{X(label)}</string>
              <key>ProgramArguments</key>
              <array>
                <string>/bin/sh</string>
                <string>{X(dir)}/run.sh</string>
              </array>
              <key>RunAtLoad</key><true/>
              <key>KeepAlive</key><true/>
              <key>ThrottleInterval</key><integer>30</integer>
              <key>ExitTimeOut</key><integer>60</integer>
              <key>StandardOutPath</key><string>/Library/Logs/{X(manifest.Product.Name)}/sqlserver.log</string>
              <key>StandardErrorPath</key><string>/Library/Logs/{X(manifest.Product.Name)}/sqlserver.log</string>
            </dict>
            </plist>
            """);
        File.WriteAllText(Path.Combine(payload, "Library", "LaunchDaemons", SqlBrowserLabel(id) + ".plist"), $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>Label</key><string>{X(SqlBrowserLabel(id))}</string>
              <key>ProgramArguments</key>
              <array>
                <string>{X(root)}/runtime/dotnet/dotnet</string>
                <string>{X(root)}/runtime/launcher/wpfrun.dll</string>
                <string>sqlbrowser</string>
                <string>--instances</string>
                <string>{X(SqlBrowserInstances)}</string>
              </array>
              <key>UserName</key><string>{X(WpfWebGpu.Services.ServiceUnits.MacServiceUser)}</string>
              <key>GroupName</key><string>staff</string>
              <key>RunAtLoad</key><true/>
              <key>KeepAlive</key><true/>
              <key>ThrottleInterval</key><integer>30</integer>
              <key>StandardOutPath</key><string>/Library/Logs/{X(manifest.Product.Name)}/sqlbrowser.log</string>
              <key>StandardErrorPath</key><string>/Library/Logs/{X(manifest.Product.Name)}/sqlbrowser.log</string>
            </dict>
            </plist>
            """);
        string run = Path.Combine(payload, dir.TrimStart('/'), "run.sh");
        File.WriteAllText(run, SqlServerRun(manifest, sql, SqlServerContainer(manifest.Product.Name), label, dir));
        Executable(run);
        File.WriteAllText(Path.Combine(scripts, "postinstall"), SqlServerPostInstall(manifest, sql, label, SqlServerContainer(manifest.Product.Name), dir, id));
        Executable(Path.Combine(scripts, "postinstall"));
        string component = Path.Combine(work, "sqlserver.pkg");
        kbytes = MacComponent.Write(payload, scripts, id + ".sqlserver", manifest.Product.Version, compress: true, component, log);
        return component;
    }

    // keeps SQL Server's container running: container started (and its kernel fetched) where it is not yet, the image
    // pulled where it is not there, the container started again whenever it is not running; stopped when launchd
    // stops this (container's run command, ended, leaves its container running)
    private static string SqlServerRun(PackageManifest manifest, SqlServerImage sql, string name, string label, string dir) => $$"""
        #!/bin/sh
        # {{sql.Engine}} for {{manifest.Product.Name}}, in Apple's container (launchd's {{label}})
        export HOME=/var/root
        C=/usr/local/bin/container
        NAME={{name}}
        . "{{dir}}/sqlserver.env"
        export MSSQL_SA_PASSWORD
        stop() { "$C" stop -t 30 "$NAME" >/dev/null 2>&1; exit 0; }
        trap stop TERM INT
        until "$C" system status >/dev/null 2>&1; do "$C" system start --enable-kernel-install --timeout 300 || { sleep 30 & wait $!; }; done
        until "$C" image inspect {{sql.Image}} >/dev/null 2>&1; do "$C" image pull --platform {{sql.Platform}} {{sql.Image}} || { sleep 60 & wait $!; }; done
        while :; do
          if ! "$C" list --quiet 2>/dev/null | grep -qx "$NAME"; then
            "$C" delete --force "$NAME" >/dev/null 2>&1
            echo "$(date): starting $NAME"
            "$C" run -d --rm --name "$NAME" --user root --rosetta --arch amd64 -m 2048M -p 127.0.0.1:{{manifest.SqlServer.Port}}:1433 \
              -v "$NAME-data:/var/opt/mssql" -e ACCEPT_EULA={{sql.Eula}} -e MSSQL_PID={{sql.Pid}} -e MSSQL_SA_PASSWORD {{sql.Image}} >/dev/null || true
          fi
          sleep 30 & wait $!
        done

        """;

    // the sa password on the first install (kept on reinstalls, as the databases are), Rosetta where it is missing,
    // container started (fetching its kernel), the image pulled -- without the network then, the daemon pulls it as it
    // starts -- the databases' volume, and the daemon
    private static string SqlServerPostInstall(PackageManifest manifest, SqlServerImage sql, string label, string name, string dir, string id) => $$"""
        #!/bin/sh
        [ -n "$WPFPKG_ROOT" ] && exit 0
        DIR="{{dir}}"
        mkdir -p "$DIR" "/Library/Logs/{{manifest.Product.Name}}"
        if [ ! -f "$DIR/sqlserver.env" ]; then
          PW="Sq1-$(LC_ALL=C tr -dc 'A-Za-z0-9' </dev/urandom | head -c 20)"
          (umask 077; printf 'MSSQL_SA_PASSWORD=%s\n' "$PW" > "$DIR/sqlserver.env")
          printf '{{sql.Engine}} for {{manifest.Product.Name}}\nserver: 127.0.0.1,{{manifest.SqlServer.Port}}\nlogin: sa\npassword: %s\n' "$PW" > "$DIR/sqlserver.txt"
        fi
        chown root:wheel "$DIR/sqlserver.env"; chmod 600 "$DIR/sqlserver.env"
        chown root:admin "$DIR/sqlserver.txt"; chmod 640 "$DIR/sqlserver.txt"
        # SQL Server is an x64 program: Rosetta runs it
        /usr/bin/pgrep -q oahd || /usr/sbin/softwareupdate --install-rosetta --agree-to-license >/dev/null 2>&1 || true
        export HOME=/var/root
        C=/usr/local/bin/container
        "$C" system start --enable-kernel-install --timeout 300 >/dev/null 2>&1 || true
        "$C" image pull --platform {{sql.Platform}} {{sql.Image}} >/dev/null 2>&1 || echo "{{sql.Image}} not pulled now: the daemon pulls it as it starts" >&2
        "$C" volume inspect {{name}}-data >/dev/null 2>&1 || "$C" volume create {{name}}-data >/dev/null 2>&1 || true
        # the instance, for the SQL Server Browser of whichever installation answers UDP 1434
        mkdir -p "{{SqlBrowserInstances}}"
        printf '%s %s\n' "{{manifest.SqlServer.Instance}}" "{{manifest.SqlServer.Port}}" > "{{SqlBrowserInstances}}/{{id}}"
        chmod 755 "{{SqlBrowserInstances}}"; chmod 644 "{{SqlBrowserInstances}}/{{id}}"
        # the daemons (again, on an upgrade): the one before gone first -- launchd lets go of it only once it has
        # stopped its container (up to 30 s more) -- then each started, launchd retried until it takes it
        for l in {{label}} {{SqlBrowserLabel(id)}}; do
          launchctl bootout system/"$l" >/dev/null 2>&1 || true
          i=0; while launchctl print system/"$l" >/dev/null 2>&1 && [ $i -lt 60 ]; do sleep 1; i=$((i+1)); done
          launchctl enable system/"$l" 2>/dev/null || true
          i=0
          until launchctl bootstrap system /Library/LaunchDaemons/"$l".plist 2>/dev/null; do
            i=$((i+1))
            if [ $i -ge 30 ]; then launchctl print system/"$l" >/dev/null 2>&1 || echo "could not start $l" >&2; break; fi
            sleep 1
          done
        done
        exit 0

        """;

    // uninstall.sh's part: the daemon and its container gone; the databases and the password kept with --keep-data
    // (the names before container's own rule was kept to -- the daemon's label -- removed too)
    private static string SqlServerUninstall(string id, string root) => $$"""
        # the SQL Server Browser: this installation's instance no more, its daemon gone
        launchctl bootout system/{{SqlBrowserLabel(id)}} >/dev/null 2>&1 || true
        rm -f /Library/LaunchDaemons/{{SqlBrowserLabel(id)}}.plist "{{SqlBrowserInstances}}/{{id}}"
        rmdir "{{SqlBrowserInstances}}" "{{Path.GetDirectoryName(SqlBrowserInstances)}}" 2>/dev/null || true
        if [ -f /Library/LaunchDaemons/{{SqlServerLabel(id)}}.plist ]; then
          launchctl bootout system/{{SqlServerLabel(id)}} >/dev/null 2>&1 || true
          rm -f /Library/LaunchDaemons/{{SqlServerLabel(id)}}.plist
          for n in {{SqlServerContainer(Path.GetFileName(root))}} {{SqlServerLabel(id)}}; do
            HOME=/var/root /usr/local/bin/container delete --force "$n" >/dev/null 2>&1 || true
          done
          if [ -n "$ALL" ]; then
            for n in {{SqlServerContainer(Path.GetFileName(root))}} {{SqlServerLabel(id)}}; do
              HOME=/var/root /usr/local/bin/container volume delete "$n-data" >/dev/null 2>&1 || true
            done
            HOME=/var/root /usr/local/bin/container image delete {{SqlServerImage.X64.Image}} >/dev/null 2>&1 || true
          fi
          pkgutil --forget "{{id}}.sqlserver" >/dev/null 2>&1 || true
          # Apple's container, which the installation brought for it: gone too where nothing else uses it
          # (no other installation's database daemon, no container, no image left)
          if [ -n "$ALL" ] && [ -x /usr/local/bin/container ] && ! ls /Library/LaunchDaemons/*-sqlserver.plist >/dev/null 2>&1 \
             && [ -z "$(HOME=/var/root /usr/local/bin/container list --all --quiet 2>/dev/null)" ] \
             && [ -z "$(HOME=/var/root /usr/local/bin/container image list --quiet 2>/dev/null)" ]; then
            HOME=/var/root /usr/local/bin/container system stop >/dev/null 2>&1 || true
            # (its uninstaller refuses while any of its services is still loaded: what its stop leaves, booted out)
            i=0; while launchctl list 2>/dev/null | grep -q 'com\.apple\.container\W' && [ $i -lt 15 ]; do sleep 1; i=$((i+1)); done
            for l in $(launchctl list 2>/dev/null | awk '{print $3}' | grep '^com\.apple\.container\.'); do
              launchctl bootout system/"$l" >/dev/null 2>&1 || true
            done
            if [ -x /usr/local/bin/uninstall-container.sh ]; then
              HOME=/var/root /usr/local/bin/uninstall-container.sh -d 2>&1 | grep -v '^Removed\|^Removing' || true
            fi
            [ -x /usr/local/bin/container ] && echo "Apple's container is left (/usr/local/bin/uninstall-container.sh -d removes it)" \
              || pkgutil --forget com.apple.container-installer >/dev/null 2>&1 || true
          fi
        fi

        """;

    /// <summary>The Distribution's choices: the installation, always; SQL Server (container and its daemon), chosen
    /// by default where container runs (macOS 26 on Apple silicon).</summary>
    private static void SqlServerChoices(XDocument dist, string product, string id, string containerPkgId)
    {
        var root = dist.Root!;
        root.Element("choices-outline")?.Remove();
        root.Elements("choice").Remove();
        root.Element("options")?.SetAttributeValue("customize", "allow");
        const string available = "system.compareVersions(system.version.ProductVersion, '26.0') >= 0";
        root.Add(
            new XElement("choices-outline",
                new XElement("line", new XAttribute("choice", "installation")),
                new XElement("line", new XAttribute("choice", "sqlserver"))),
            new XElement("choice", new XAttribute("id", "installation"), new XAttribute("title", product),
                new XAttribute("enabled", "false"), new XAttribute("selected", "true"),
                new XElement("pkg-ref", new XAttribute("id", id))),
            new XElement("choice", new XAttribute("id", "sqlserver"), new XAttribute("title", SqlServerImage.X64.Engine),
                new XAttribute("description", $"The database server {product} keeps its configuration in, run by Apple's container (installed with it). Its image is downloaded from Microsoft when installing. Needs macOS 26."),
                new XAttribute("start_selected", available), new XAttribute("enabled", available),
                new XElement("pkg-ref", new XAttribute("id", containerPkgId)),
                new XElement("pkg-ref", new XAttribute("id", id + ".sqlserver"))));
    }

    /// <summary>The installer's last page: where SQL Server is and its login, for the product's own SQL configuration,
    /// which on Windows connects to its instance with Windows authentication -- not something a container's SQL Server
    /// takes.</summary>
    private static void SqlServerConclusion(XDocument dist, PackageManifest manifest, string root, string resources)
    {
        string product = manifest.Product.Name;
        Directory.CreateDirectory(resources);
        File.WriteAllText(Path.Combine(resources, "conclusion.html"), $"""
            <!DOCTYPE html>
            <html><head><meta charset="utf-8"></head>
            <body>
            <p>{X(product)} is installed.</p>
            <p>If {X(SqlServerImage.X64.Engine)} was installed with it, it starts in the background now; downloading it
            the first time takes a few minutes. Point {X(product)}'s SQL configuration at it:</p>
            <p>server <code>127.0.0.1,{manifest.SqlServer.Port}</code>, SQL Server authentication, login <code>sa</code>,
            its password in <code>{X(root)}/sqlserver/sqlserver.txt</code> (readable by administrators).</p>
            </body></html>
            """);
        dist.Root!.Add(new XElement("conclusion", new XAttribute("file", "conclusion.html"), new XAttribute("mime-type", "text/html")));
    }
}
