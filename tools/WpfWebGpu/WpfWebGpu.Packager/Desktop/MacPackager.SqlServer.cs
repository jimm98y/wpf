// SQL Server on a Mac: one instance of a name on the machine, shared by every installation that names it, as on
// Windows -- where a second product's setup finds the instance (MSSQL$SQLEXPRESS) already there and uses it, and
// uninstalling a product leaves SQL Server installed (it has its own entry in Apps & features). A choice of the
// installer package, chosen by default ("Customize" leaves it out), of two components --
//
//   com.apple.container-installer     Apple's container (sqlserver-macos.pack: its signed installer), which runs Linux
//                                     containers in lightweight VMs; macOS 26 on Apple silicon
//   <id>.sqlserver                    the installation's SQL Server Browser (<id>-sqlbrowser: its own wpfrun sqlbrowser,
//                                     as nobody, answering UDP 1434 for every instance), and a postinstall that makes
//                                     the instance where it is not there yet:
//
//       /Library/LaunchDaemons/com.wpfwebgpu.sqlserver.<instance>.plist   the daemon that runs SQL Server 2025 Express
//                                                         in container (Rosetta), as the container sqlserver-<instance>
//       /Library/Application Support/WpfWebGpu/SqlServer/<INSTANCE>/
//           run.sh                                        the daemon's script
//           sqlserver.env                                 its sa password, port and databases' volume (root only)
//           sqlserver.txt                                 the same for the Mac's administrators to read
//           installations/<id>                            the installations that use it (each its product's name)
//           uninstall.sh                                  removes it (and Apple's container, where nothing else uses it)
//           Uninstall SQL Server <INSTANCE>.app           asks, then runs uninstall.sh as an administrator: the
//                                                         uninstaller next to what it removes, as a database server's
//                                                         own on a Mac is (Windows: its entry in Apps & features)
//       /Applications/<Product>/Uninstall SQL Server.app  the same, beside each installation's own uninstaller, where
//                                                         it is looked for (gone with the installation; the instance's
//                                                         uninstall removes every one)
//       /Library/Application Support/WpfWebGpu/SqlBrowser/<instance>     "<instance> <port>", for the browsers
//
// The port is the installation's configured one (1433) where it is free, else the next free one, as a named instance
// on Windows takes a port of its own; a client naming the instance ((local)\SQLEXPRESS) finds it through the browser.
// The postinstall starts container (which fetches its own Linux kernel then) and pulls Microsoft's image -- carried by
// neither: a Linux system and kernel are GPL. The daemon keeps the container running (container's own run command
// does not stop the container when it is ended) and stops it when launchd stops the daemon.
//
// Earlier versions brought an installation's own SQL Server (<id>-sqlserver, its files in the installation's
// sqlserver/ folder); the instance takes its password and databases.

using System.Xml.Linq;
using WpfWebGpu.Packager.SqlServer;

namespace WpfWebGpu.Packager.Desktop;

public static partial class MacPackager
{
    /// <summary>The instances' folders, one each.</summary>
    internal const string SqlServerInstances = "/Library/Application Support/WpfWebGpu/SqlServer";

    /// <summary>Where every installation's browser finds the instances (a file each, "instance port").</summary>
    internal const string SqlBrowserInstances = "/Library/Application Support/WpfWebGpu/SqlBrowser";

    /// <summary>The instance's daemon: outside every installation's &lt;id&gt;.*, which an upgrade stops and removes.</summary>
    internal static string SqlServerLabel(string instance) => "com.wpfwebgpu.sqlserver." + DesktopCommon.SqlInstanceKey(instance);

    /// <summary>The instance's folder.</summary>
    internal static string SqlServerFolder(string instance) => SqlServerInstances + "/" + instance.ToUpperInvariant();

    /// <summary>Windows' SQL Server Browser, the installation's (wpfrun sqlbrowser): a client naming an instance
    /// ((local)\SQLEXPRESS) asks UDP 1434 for its port. Its label is outside &lt;id&gt;.* as SQL Server's is.</summary>
    private static string SqlBrowserLabel(string id) => id + "-sqlbrowser";

    /// <summary>The installation's own SQL Server of earlier versions: its daemon.</summary>
    private static string EarlierSqlServerLabel(string id) => id + "-sqlserver";

    /// <summary>The name of the installation's own SQL Server's container of earlier versions (and, with -data, its
    /// volume's).</summary>
    internal static string EarlierSqlServerContainer(string product)
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
        string payload = Path.Combine(work, "sqlserver-root"), scripts = Path.Combine(work, "sqlserver-scripts");
        Directory.CreateDirectory(Path.Combine(payload, "Library", "LaunchDaemons"));
        Directory.CreateDirectory(scripts);
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
        File.WriteAllText(Path.Combine(scripts, "postinstall"), SqlServerPostInstall(manifest, id, root));
        Executable(Path.Combine(scripts, "postinstall"));
        string component = Path.Combine(work, "sqlserver.pkg");
        kbytes = MacComponent.Write(payload, scripts, id + ".sqlserver", manifest.Product.Version, compress: true, component, log);
        return component;
    }

    // the instance's daemon: its run.sh keeps SQL Server's container running
    private static string SqlServerDaemon(string instance) => $$"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
          <key>Label</key><string>{{X(SqlServerLabel(instance))}}</string>
          <key>ProgramArguments</key>
          <array>
            <string>/bin/sh</string>
            <string>{{X(SqlServerFolder(instance))}}/run.sh</string>
          </array>
          <key>RunAtLoad</key><true/>
          <key>KeepAlive</key><true/>
          <key>ThrottleInterval</key><integer>30</integer>
          <key>ExitTimeOut</key><integer>60</integer>
          <key>StandardOutPath</key><string>/Library/Logs/WpfWebGpu/sqlserver-{{X(DesktopCommon.SqlInstanceKey(instance))}}.log</string>
          <key>StandardErrorPath</key><string>/Library/Logs/WpfWebGpu/sqlserver-{{X(DesktopCommon.SqlInstanceKey(instance))}}.log</string>
        </dict>
        </plist>
        """;

    // keeps SQL Server's container running: container started (and its kernel fetched) where it is not yet, the image
    // pulled where it is not there, the container started again whenever it is not running; stopped when launchd
    // stops this (container's run command, ended, leaves its container running)
    private static string SqlServerRun(SqlServerImage sql, string instance)
    {
        string key = DesktopCommon.SqlInstanceKey(instance);
        return $$"""
            #!/bin/sh
            # {{sql.Engine}}, instance {{instance}}, in Apple's container (launchd's {{SqlServerLabel(instance)}})
            export HOME=/var/root
            C=/usr/local/bin/container
            NAME=sqlserver-{{key}}
            . "{{SqlServerFolder(instance)}}/sqlserver.env"
            export MSSQL_SA_PASSWORD
            stop() { "$C" stop -t 30 "$NAME" >/dev/null 2>&1; exit 0; }
            trap stop TERM INT
            until "$C" system status >/dev/null 2>&1; do "$C" system start --enable-kernel-install --timeout 300 || { sleep 30 & wait $!; }; done
            until "$C" image inspect {{sql.Image}} >/dev/null 2>&1; do "$C" image pull --platform {{sql.Platform}} {{sql.Image}} || { sleep 60 & wait $!; }; done
            while :; do
              if ! "$C" list --quiet 2>/dev/null | grep -qx "$NAME"; then
                "$C" delete --force "$NAME" >/dev/null 2>&1
                echo "$(date): starting $NAME"
                "$C" run -d --rm --name "$NAME" --user root --rosetta --arch amd64 -m 2048M -p 127.0.0.1:"$SQL_PORT":1433 \
                  -v "$SQL_VOLUME:/var/opt/mssql" -e ACCEPT_EULA={{sql.Eula}} -e MSSQL_PID={{sql.Pid}} -e MSSQL_SA_PASSWORD {{sql.Image}} >/dev/null || true
              fi
              sleep 30 & wait $!
            done

            """;
    }

    // the instance's own uninstall, as SQL Server's entry in Apps & features on Windows: its daemon and container gone,
    // its databases and password too unless --keep-data; the image and Apple's container where nothing else uses them
    internal static string SqlInstanceUninstall(string instance)
    {
        var sql = SqlServerImage.X64;
        string key = DesktopCommon.SqlInstanceKey(instance), dir = SqlServerFolder(instance), label = SqlServerLabel(instance);
        return $$"""
            #!/bin/sh
            # Removes SQL Server's instance {{instance}}: its databases and password too, unless --keep-data is given. The
            # installations that use it keep their configuration; they reach no database until it is installed again.
            if [ "$(id -u)" != 0 ]; then exec sudo "$0" "$@"; fi
            ALL=1; for a in "$@"; do [ "$a" = --keep-data ] && ALL=; done
            D="{{dir}}"
            export HOME=/var/root
            C=/usr/local/bin/container
            users=$(cat "$D/installations/"* 2>/dev/null | paste -sd, - | sed 's/,/, /g')
            [ -n "$users" ] && echo "SQL Server's instance {{instance}} is used by: $users"
            # its uninstaller beside each installation's
            cat "$D/installations/"* 2>/dev/null | while IFS= read -r p; do
              [ -n "$p" ] && rm -rf "/Applications/$p/{{SqlUninstallAppInProduct}}"
            done
            launchctl bootout system/{{label}} >/dev/null 2>&1 || true
            rm -f /Library/LaunchDaemons/{{label}}.plist
            VOL=$(sed -n 's/^SQL_VOLUME=//p' "$D/sqlserver.env" 2>/dev/null)
            [ -x "$C" ] && "$C" delete --force sqlserver-{{key}} >/dev/null 2>&1 || true
            rm -f "{{SqlBrowserInstances}}/{{key}}"
            rmdir "{{SqlBrowserInstances}}" 2>/dev/null || true
            rm -f /Library/Logs/WpfWebGpu/sqlserver-{{key}}.log; rmdir /Library/Logs/WpfWebGpu 2>/dev/null || true
            if [ -n "$ALL" ]; then
              [ -x "$C" ] && "$C" volume delete "${VOL:-sqlserver-{{key}}-data}" >/dev/null 2>&1 || true
              rm -rf "$D"
              # the image, where no other instance is left to run it
              if [ -x "$C" ] && [ -z "$(ls "{{SqlServerInstances}}" 2>/dev/null)" ]; then
                "$C" image delete {{sql.Image}} >/dev/null 2>&1 || true
              fi
            else
              find "$D" -mindepth 1 -maxdepth 1 ! -name sqlserver.env -exec rm -rf {} +
            fi
            rmdir "{{SqlServerInstances}}" "{{Path.GetDirectoryName(SqlServerInstances)}}" 2>/dev/null || true
            # Apple's container, which the instance brought: gone too where nothing else uses it (no other instance's
            # daemon, no container, no image left)
            if [ -n "$ALL" ] && [ -x "$C" ] && ! ls /Library/LaunchDaemons/com.wpfwebgpu.sqlserver.*.plist /Library/LaunchDaemons/*-sqlserver.plist >/dev/null 2>&1 \
               && [ -z "$("$C" list --all --quiet 2>/dev/null)" ] && [ -z "$("$C" image list --quiet 2>/dev/null)" ]; then
              "$C" system stop >/dev/null 2>&1 || true
              # (its uninstaller refuses while any of its services is still loaded: what its stop leaves, booted out)
              i=0; while launchctl list 2>/dev/null | grep -q 'com\.apple\.container\W' && [ $i -lt 15 ]; do sleep 1; i=$((i+1)); done
              for l in $(launchctl list 2>/dev/null | awk '{print $3}' | grep '^com\.apple\.container\.'); do
                launchctl bootout system/"$l" >/dev/null 2>&1 || true
              done
              if [ -x /usr/local/bin/uninstall-container.sh ]; then
                /usr/local/bin/uninstall-container.sh -d 2>&1 | grep -v '^Removed\|^Removing' || true
              fi
              [ -x "$C" ] && echo "Apple's container is left (/usr/local/bin/uninstall-container.sh -d removes it)" \
                || pkgutil --forget com.apple.container-installer >/dev/null 2>&1 || true
            fi
            echo "SQL Server's instance {{instance}} removed"

            """;
    }

    /// <summary>The instance's uninstaller app, in its folder.</summary>
    internal static string SqlInstanceUninstallAppName(string instance) => $"Uninstall SQL Server {instance.ToUpperInvariant()}.app";

    /// <summary>Its copy beside each installation's own uninstaller, in /Applications/&lt;Product&gt;.</summary>
    internal const string SqlUninstallAppInProduct = "Uninstall SQL Server.app";

    // the app's program: asked first -- which installations still use the instance said -- then uninstall.sh run as an
    // administrator (the databases kept with "Keep Databases")
    internal static string SqlInstanceUninstallApp(string instance)
    {
        string dir = SqlServerFolder(instance), name = instance.ToUpperInvariant();
        return $$"""
            #!/bin/sh
            # Uninstall SQL Server's instance {{name}}: asked first, then its uninstall.sh run as an administrator
            D="{{dir}}"
            users=$(cat "$D/installations/"* 2>/dev/null | tr -d '"\\' | paste -sd, - | sed 's/,/, /g')
            used=""
            case "$users" in
              *,*) used="\n\n$users use it, and have no database until it is installed again." ;;
              ?*) used="\n\n$users uses it, and has no database until it is installed again." ;;
            esac
            choice=$(osascript -e "button returned of (display dialog \"Remove SQL Server's instance {{name}} from this Mac?$used\n\nIts databases can be kept for a later install, or removed with it.\" buttons {\"Cancel\", \"Keep Databases\", \"Remove Everything\"} default button \"Remove Everything\" cancel button \"Cancel\" with title \"Uninstall SQL Server {{name}}\" with icon caution)" 2>/dev/null) || exit 0
            arg=""; [ "$choice" = "Keep Databases" ] && arg=" --keep-data"
            osascript -e "do shell script \"'$D/uninstall.sh'$arg\" with administrator privileges" >/dev/null 2>&1 \
              && osascript -e 'display dialog "SQL Server'"'"'s instance {{name}} was removed." buttons {"OK"} default button "OK" with title "Uninstall SQL Server {{name}}"' >/dev/null 2>&1
            exit 0

            """;
    }

    private static string SqlInstanceUninstallInfo(string instance, string version) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
          <key>CFBundleIdentifier</key><string>{X("com.wpfwebgpu.sqlserver." + DesktopCommon.SqlInstanceKey(instance) + ".uninstall")}</string>
          <key>CFBundleName</key><string>{X("Uninstall SQL Server " + instance.ToUpperInvariant())}</string>
          <key>CFBundleExecutable</key><string>uninstall</string>
          <key>CFBundlePackageType</key><string>APPL</string>
          <key>CFBundleShortVersionString</key><string>{X(version)}</string>
          <key>LSMinimumSystemVersion</key><string>12.0</string>
        </dict>
        </plist>
        """;

    // the instance made where it is not there yet -- the installation's own SQL Server of before taken over (its
    // password and databases), the port the configured one where free, else the next free one -- its daemon, run
    // script and uninstall written, Rosetta where it is missing, container started (fetching its kernel), the image
    // pulled -- without the network then, the daemon pulls it as it starts -- the volume, the daemons
    internal static string SqlServerPostInstall(PackageManifest manifest, string id, string root)
    {
        var sql = SqlServerImage.X64;
        string instance = manifest.SqlServer.Instance, key = DesktopCommon.SqlInstanceKey(instance), label = SqlServerLabel(instance);
        string earlier = EarlierSqlServerContainer(Path.GetFileName(root));
        return $$"""
            #!/bin/sh
            [ -n "$WPFPKG_ROOT" ] && exit 0
            D="{{SqlServerFolder(instance)}}"
            export HOME=/var/root
            C=/usr/local/bin/container
            mkdir -p "$D/installations" "/Library/Logs/{{manifest.Product.Name}}" /Library/Logs/WpfWebGpu
            # this installation's own SQL Server of before: stopped, its container gone (its volume and password taken).
            # Its daemon is asked of launchd, not of its file: the installer has removed the files the earlier
            # component had and this one has not (its receipt's obsoleting) before this runs, the job still loaded
            if launchctl print system/{{EarlierSqlServerLabel(id)}} >/dev/null 2>&1; then
              launchctl bootout system/{{EarlierSqlServerLabel(id)}} >/dev/null 2>&1 || true
              i=0; while launchctl print system/{{EarlierSqlServerLabel(id)}} >/dev/null 2>&1 && [ $i -lt 60 ]; do sleep 1; i=$((i+1)); done
            fi
            rm -f /Library/LaunchDaemons/{{EarlierSqlServerLabel(id)}}.plist
            "$C" delete --force {{earlier}} >/dev/null 2>&1 || true
            OLDPW=
            [ -f "{{root}}/sqlserver/sqlserver.env" ] && OLDPW=$(sed -n 's/^MSSQL_SA_PASSWORD=//p' "{{root}}/sqlserver/sqlserver.env")
            rm -rf "{{root}}/sqlserver"; rm -f "{{SqlBrowserInstances}}/{{id}}"
            if [ ! -f "$D/sqlserver.env" ]; then
              PW="$OLDPW"; [ -n "$PW" ] || PW="Sq1-$(LC_ALL=C tr -dc 'A-Za-z0-9' </dev/urandom | head -c 20)"
              VOL=sqlserver-{{key}}-data
              # (its databases only with their password: sa's is in them)
              [ -n "$OLDPW" ] && "$C" volume inspect {{earlier}}-data >/dev/null 2>&1 && VOL={{earlier}}-data
              # the port: the configured one where nothing listens on it and no other instance has it, else the next
              P={{manifest.SqlServer.Port}}
              # (not 1434: the dedicated admin connection's by convention, the browser's on UDP)
              while [ "$P" = 1434 ] || /usr/sbin/lsof -nP -iTCP:"$P" -sTCP:LISTEN >/dev/null 2>&1 || grep -qs " $P\$" "{{SqlBrowserInstances}}"/*; do P=$((P+1)); done
              (umask 077; printf 'MSSQL_SA_PASSWORD=%s\nSQL_PORT=%s\nSQL_VOLUME=%s\n' "$PW" "$P" "$VOL" > "$D/sqlserver.env")
            fi
            chown root:wheel "$D/sqlserver.env"; chmod 600 "$D/sqlserver.env"
            . "$D/sqlserver.env"
            printf '{{sql.Engine}}, instance {{instance}}\nserver: (local)\\{{instance}} or 127.0.0.1,%s\nlogin: sa\npassword: %s\n' "$SQL_PORT" "$MSSQL_SA_PASSWORD" > "$D/sqlserver.txt"
            chown root:admin "$D/sqlserver.txt"; chmod 640 "$D/sqlserver.txt"
            printf '%s\n' "{{manifest.Product.Name}}" > "$D/installations/{{id}}"
            # the daemon, its script and the instance's uninstall, as this version writes them
            cat > "$D/run.sh.new" <<'RUN'
            {{SqlServerRun(sql, instance)}}RUN
            cat > /Library/LaunchDaemons/{{label}}.plist.new <<'PLIST'
            {{SqlServerDaemon(instance)}}
            PLIST
            CHANGED=
            cmp -s "$D/run.sh.new" "$D/run.sh" && cmp -s /Library/LaunchDaemons/{{label}}.plist.new /Library/LaunchDaemons/{{label}}.plist || CHANGED=1
            mv -f "$D/run.sh.new" "$D/run.sh"; chmod 755 "$D/run.sh"
            mv -f /Library/LaunchDaemons/{{label}}.plist.new /Library/LaunchDaemons/{{label}}.plist
            chown root:wheel /Library/LaunchDaemons/{{label}}.plist; chmod 644 /Library/LaunchDaemons/{{label}}.plist
            cat > "$D/uninstall.sh" <<'UNINSTALL'
            {{SqlInstanceUninstall(instance)}}UNINSTALL
            chmod 755 "$D/uninstall.sh"
            APP="$D/{{SqlInstanceUninstallAppName(instance)}}"
            rm -rf "$APP"; mkdir -p "$APP/Contents/MacOS"
            cat > "$APP/Contents/MacOS/uninstall" <<'APPRUN'
            {{SqlInstanceUninstallApp(instance)}}APPRUN
            cat > "$APP/Contents/Info.plist" <<'APPINFO'
            {{SqlInstanceUninstallInfo(instance, "2025.1")}}
            APPINFO
            chmod 755 "$APP/Contents/MacOS/uninstall"
            # and beside the installation's own uninstaller
            rm -rf "/Applications/{{manifest.Product.Name}}/{{SqlUninstallAppInProduct}}"
            [ -d "/Applications/{{manifest.Product.Name}}" ] && ditto "$APP" "/Applications/{{manifest.Product.Name}}/{{SqlUninstallAppInProduct}}"
            # the instance, for the SQL Server Browser of whichever installation answers UDP 1434
            mkdir -p "{{SqlBrowserInstances}}"
            printf '%s %s\n' "{{instance}}" "$SQL_PORT" > "{{SqlBrowserInstances}}/{{key}}"
            chmod 755 "{{SqlBrowserInstances}}"; chmod 644 "{{SqlBrowserInstances}}/{{key}}"
            # SQL Server is an x64 program: Rosetta runs it
            /usr/bin/pgrep -q oahd || /usr/sbin/softwareupdate --install-rosetta --agree-to-license >/dev/null 2>&1 || true
            "$C" system start --enable-kernel-install --timeout 300 >/dev/null 2>&1 || true
            "$C" image pull --platform {{sql.Platform}} {{sql.Image}} >/dev/null 2>&1 || echo "{{sql.Image}} not pulled now: the daemon pulls it as it starts" >&2
            "$C" volume inspect "$SQL_VOLUME" >/dev/null 2>&1 || "$C" volume create "$SQL_VOLUME" >/dev/null 2>&1 || true
            # the daemons: the instance's started where it does not run (again where this version changed it), the
            # browser again (an upgrade); each once launchd has let go of the one before -- it does only once that has
            # stopped (SQL Server's container, up to 30 s more) -- launchd retried until it takes it
            start() {
              launchctl bootout system/"$1" >/dev/null 2>&1 || true
              i=0; while launchctl print system/"$1" >/dev/null 2>&1 && [ $i -lt 60 ]; do sleep 1; i=$((i+1)); done
              launchctl enable system/"$1" 2>/dev/null || true
              i=0
              until launchctl bootstrap system /Library/LaunchDaemons/"$1".plist 2>/dev/null; do
                i=$((i+1))
                if [ $i -ge 30 ]; then launchctl print system/"$1" >/dev/null 2>&1 || echo "could not start $1" >&2; break; fi
                sleep 1
              done
            }
            if [ -n "$CHANGED" ] || ! launchctl print system/{{label}} >/dev/null 2>&1; then start {{label}}; fi
            start {{SqlBrowserLabel(id)}}
            exit 0

            """;
    }

    // uninstall.sh's part: the installation's SQL Server Browser gone, and the installation from the instance's users;
    // the instance stays, as Windows leaves SQL Server installed when a product goes (other installations may use it)
    private static string SqlServerUninstall(string id, string instance) => $$"""
        launchctl bootout system/{{SqlBrowserLabel(id)}} >/dev/null 2>&1 || true
        rm -f /Library/LaunchDaemons/{{SqlBrowserLabel(id)}}.plist "{{SqlServerFolder(instance)}}/installations/{{id}}"
        pkgutil --forget "{{id}}.sqlserver" >/dev/null 2>&1 || true
        if [ -x "{{SqlServerFolder(instance)}}/uninstall.sh" ]; then
          echo "SQL Server's instance {{instance}} stays, with its databases: '{{SqlServerFolder(instance)}}/{{SqlInstanceUninstallAppName(instance)}}' (or its uninstall.sh) removes it"
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
                new XAttribute("description", $"The database server {product} keeps its configuration in, run by Apple's container (installed with it), and shared with the other programs that name the same instance. Its image is downloaded from Microsoft when installing. Needs macOS 26."),
                new XAttribute("start_selected", available), new XAttribute("enabled", available),
                new XElement("pkg-ref", new XAttribute("id", containerPkgId)),
                new XElement("pkg-ref", new XAttribute("id", id + ".sqlserver"))));
    }

    /// <summary>The installer's last page: where SQL Server is and its login, for the product's own SQL configuration,
    /// which on Windows connects to its instance with Windows authentication -- not something a container's SQL Server
    /// takes.</summary>
    private static void SqlServerConclusion(XDocument dist, PackageManifest manifest, string resources)
    {
        string product = manifest.Product.Name, instance = manifest.SqlServer.Instance;
        Directory.CreateDirectory(resources);
        File.WriteAllText(Path.Combine(resources, "conclusion.html"), $"""
            <!DOCTYPE html>
            <html><head><meta charset="utf-8"></head>
            <body>
            <p>{X(product)} is installed.</p>
            <p>If {X(SqlServerImage.X64.Engine)} was installed with it, it starts in the background now; downloading it
            the first time takes a few minutes. Point {X(product)}'s SQL configuration at it:</p>
            <p>server <code>(local)\{X(instance)}</code>, SQL Server authentication, login <code>sa</code>, its password
            (and the instance's port) in <code>{X(SqlServerFolder(instance))}/sqlserver.txt</code> (readable by
            administrators).</p>
            </body></html>
            """);
        dist.Root!.Add(new XElement("conclusion", new XAttribute("file", "conclusion.html"), new XAttribute("mime-type", "text/html")));
    }
}
