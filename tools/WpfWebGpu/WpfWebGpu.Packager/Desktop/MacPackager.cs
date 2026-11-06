// The whole installation as a macOS installer package (.pkg):
//
//   /Library/Application Support/<Product>/
//       app/            the Program Files folder, as it is
//       runtime/        what runs it with nothing installed (desktop-osx-<arch>.pack: .NET, wpfrun)
//       seed/           the registry (its paths as tokens) and ProgramData, made the machine's state on first install
//       state/          the machine's state (WPFRUN_STATE): registry/ and ProgramData/, as Windows keeps them
//       uninstall.sh
//   /Library/LaunchDaemons/<id>.<service>.plist      each service, started by launchd as the SCM starts it
//   /Library/LaunchAgents/<id>.startup.plist           at each log-on, the Startup folders' shortcuts (wpfrun startup)
//   /Library/LaunchDaemons/<id>.httpsys.plist          the machine's http.sys (wpfrun httpsys), where a program serves through it
//   /Applications/<Product>/<App>.app                 each window app, with its icon
//
// The state is made on the first install only: an upgrade keeps what the machine has done with its registry and
// its data, as a Windows installer leaves ProgramData and HKLM's values. It is writable by every user of the Mac
// (group staff), as ProgramData is by every user of a Windows machine.
//
// The package is made with pkgbuild and productbuild, which come with macOS: a Mac needs nothing installed.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;

namespace WpfWebGpu.Packager.Desktop;

public static class MacPackager
{
    public static string Build(PackageManifest manifest, StagedState staged, PackFile runtimePack, string outDir, Action<string> log)
    {
        if (!OperatingSystem.IsMacOS()) throw new PackagerError("a macOS package is made on a Mac (pkgbuild)");
        string product = manifest.Product.Name;
        string arch = runtimePack["rid"].Split('-')[1];
        string id = manifest.Product.IdPrefix + "." + DesktopCommon.Segment(product);
        string root = "/Library/Application Support/" + product;

        string work = Path.Combine(outDir, ".work", "macos-" + arch);
        if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        string payload = Path.Combine(work, "root"), scripts = Path.Combine(work, "scripts");
        string home = Path.Combine(payload, root.TrimStart('/'));
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(scripts);

        var excludes = new GlobSet(manifest.Exclude);
        log($"macOS ({arch}): the installation into {root}");
        int files = CloneTree(manifest.Inputs.InstallDir, Path.Combine(home, "app"), excludes);
        CloneTree(Path.Combine(runtimePack.Directory, "runtime"), Path.Combine(home, "runtime"), new GlobSet(Array.Empty<string>()));
        foreach (string hive in Directory.EnumerateFiles(staged.RegistryDir, "*.reg"))
            Clone(hive, Path.Combine(home, "seed", "registry", Path.GetFileName(hive)));
        foreach (var (source, target) in staged.ProgramDataFiles)
            Clone(source, Path.Combine(home, "seed", "ProgramData", target));
        log($"  {files} files of the installation, {staged.ProgramDataFiles.Count} of ProgramData");

        // the services
        var daemons = new List<string>();
        foreach (var service in manifest.Services.Where(s => s.Start != StartType.Disabled))
        {
            string label = id + "." + DesktopCommon.Segment(service.Name);
            string plist = Path.Combine(payload, "Library", "LaunchDaemons", label + ".plist");
            Directory.CreateDirectory(Path.GetDirectoryName(plist)!);
            File.WriteAllText(plist, Daemon(label, root, product, service, manifest.Product.OsIdentity));
            daemons.Add(label);
        }

        // the machine's http.sys: the ports the programs' URL prefixes name, owned apart from every program
        if (DesktopCommon.UsesHttpSys(manifest))
        {
            string label = id + ".httpsys";
            Directory.CreateDirectory(Path.Combine(payload, "Library", "LaunchDaemons"));
            File.WriteAllText(Path.Combine(payload, "Library", "LaunchDaemons", label + ".plist"), HttpSysDaemon(label, root, product));
            daemons.Insert(0, label);
            log("  http.sys service: a program serves through http.sys");
        }

        // at log-on, what the apps put in the Startup folders, as Explorer starts it
        string agent = Path.Combine(payload, "Library", "LaunchAgents", id + ".startup.plist");
        Directory.CreateDirectory(Path.GetDirectoryName(agent)!);
        File.WriteAllText(agent, StartupAgent(id + ".startup", root, manifest.Product.OsIdentity));

        // the window apps
        var image = DesktopCommon.Icon(manifest);
        foreach (var app in manifest.Apps.Where(a => a.Kind == AppKind.Gui && a.Shortcut))
            WriteAppStub(Path.Combine(payload, "Applications", product), root, id, app, manifest, image);

        File.WriteAllText(Path.Combine(home, "uninstall.sh"), Uninstall(root, product, id, daemons));
        Executable(Path.Combine(home, "uninstall.sh"));
        File.WriteAllText(Path.Combine(scripts, "preinstall"), PreInstall(daemons));
        File.WriteAllText(Path.Combine(scripts, "postinstall"), PostInstall(root, daemons));
        Executable(Path.Combine(scripts, "preinstall"));
        Executable(Path.Combine(scripts, "postinstall"));

        // pkgbuild takes the folder's permissions as the installed files': readable by all, the scripts runnable
        Run("/bin/chmod", "-R", "u+rwX,go+rX,go-w", payload);
        string component = Path.Combine(work, "component.pkg");
        log("  pkgbuild (compressing the payload takes a while)");
        Run("/usr/bin/pkgbuild", "--root", payload, "--scripts", scripts, "--identifier", id, "--version", manifest.Product.Version,
            "--install-location", "/", "--ownership", "recommended", component);

        Directory.CreateDirectory(outDir);
        string pkg = Path.Combine(outDir, $"{DesktopCommon.FileName(product)}-{manifest.Product.Version}-macos-{arch}.pkg");
        var args = new List<string> { "--package", component, "--identifier", id, "--version", manifest.Product.Version };
        if (manifest.Signing.DeveloperIdInstaller.Length > 0) { args.Add("--sign"); args.Add(manifest.Signing.DeveloperIdInstaller); }
        args.Add(pkg);
        Run("/usr/bin/productbuild", args.ToArray());
        Directory.Delete(work, recursive: true);
        log($"{pkg} ({new FileInfo(pkg).Length / (1024 * 1024)} MB)");
        return pkg;
    }

    // ---- what is installed --------------------------------------------------------------------------------

    private static IEnumerable<KeyValuePair<string, string>> ServiceEnvironment(string? osIdentity, ServiceEntry service)
    {
        var env = new Dictionary<string, string>(service.Environment);
        if (!string.IsNullOrEmpty(osIdentity)) env.TryAdd("WPFWEBGPU_OS_IDENTITY", osIdentity);
        return env;
    }

    private static string Daemon(string label, string root, string product, ServiceEntry service, string osIdentity)
    {
        var args = new List<string> { root + "/runtime/run", root + "/app/" + service.Exe };
        args.AddRange(service.Args);
        string log = $"/Library/Logs/{product}/{Path.GetFileNameWithoutExtension(service.Exe)}.log";
        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>Label</key><string>{X(label)}</string>
              <key>ProgramArguments</key>
              <array>
            {string.Concat(args.Select(a => $"    <string>{X(a)}</string>\n"))}  </array>
              <key>EnvironmentVariables</key>
              <dict>
                <key>WPFRUN_STATE</key><string>{X(root)}/state</string>
                <key>WPFRUN_CACHE</key><string>/Library/Caches/{X(product)}</string>
                <key>WPFRUN_LOGS</key><string>/Library/Logs/{X(product)}</string>
            {string.Concat(ServiceEnvironment(osIdentity, service).Select(e => $"    <key>{X(e.Key)}</key><string>{X(e.Value)}</string>\n"))}
              </dict>
              <key>WorkingDirectory</key><string>{X(root + "/app/" + Path.GetDirectoryName(service.Exe)!.Replace('\\', '/'))}</string>
              <key>RunAtLoad</key><{(service.Start == StartType.Auto ? "true" : "false")}/>
              <key>KeepAlive</key><dict><key>SuccessfulExit</key><false/></dict>
              <key>ThrottleInterval</key><integer>10</integer>
              <key>ExitTimeOut</key><integer>30</integer>
              <key>Umask</key><integer>2</integer>
              <key>StandardOutPath</key><string>{X(log)}</string>
              <key>StandardErrorPath</key><string>{X(log)}</string>
            </dict>
            </plist>
            """;
    }

    private static string HttpSysDaemon(string label, string root, string product) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
          <key>Label</key><string>{X(label)}</string>
          <key>ProgramArguments</key>
          <array>
            <string>{X(root)}/runtime/dotnet/dotnet</string>
            <string>{X(root)}/runtime/launcher/wpfrun.dll</string>
            <string>httpsys</string>
          </array>
          <key>EnvironmentVariables</key>
          <dict>
            <key>WPFRUN_STATE</key><string>{X(root)}/state</string>
          </dict>
          <key>RunAtLoad</key><true/>
          <key>KeepAlive</key><true/>
          <key>ThrottleInterval</key><integer>5</integer>
          <key>StandardOutPath</key><string>/Library/Logs/{X(product)}/httpsys.log</string>
          <key>StandardErrorPath</key><string>/Library/Logs/{X(product)}/httpsys.log</string>
        </dict>
        </plist>
        """;

    private static string StartupAgent(string label, string root, string osIdentity) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
          <key>Label</key><string>{X(label)}</string>
          <key>ProgramArguments</key>
          <array>
            <string>{X(root)}/runtime/dotnet/dotnet</string>
            <string>{X(root)}/runtime/launcher/wpfrun.dll</string>
            <string>startup</string>
          </array>
          <key>EnvironmentVariables</key>
          <dict>
            <key>WPFRUN_STATE</key><string>{X(root)}/state</string>
        {(osIdentity.Length > 0 ? $"    <key>WPFWEBGPU_OS_IDENTITY</key><string>{X(osIdentity)}</string>\n" : "")}  </dict>
          <key>RunAtLoad</key><true/>
          <key>LimitLoadToSessionType</key><string>Aqua</string>
        </dict>
        </plist>
        """;

    private static void WriteAppStub(string folder, string root, string id, AppEntry app, PackageManifest manifest, (byte[] Rgba, int Width, int Height)? productIcon)
    {
        string name = app.Name.Length > 0 ? app.Name : Path.GetFileNameWithoutExtension(app.Exe);
        string bundle = Path.Combine(folder, DesktopCommon.FileName(name) + ".app", "Contents");
        Directory.CreateDirectory(Path.Combine(bundle, "MacOS"));
        Directory.CreateDirectory(Path.Combine(bundle, "Resources"));
        string exeName = "launch";
        File.WriteAllText(Path.Combine(bundle, "MacOS", exeName), $"""
            #!/bin/sh
            # {name}: the Windows program run on this Mac (WPF and WinForms on WebGPU)
            export WPFRUN_STATE="{root}/state"{(manifest.Product.OsIdentity.Length > 0 ? $"\nexport WPFWEBGPU_OS_IDENTITY={manifest.Product.OsIdentity}" : "")}
            exec "{root}/runtime/run" "{root}/app/{app.Exe}" "$@"

            """);
        Executable(Path.Combine(bundle, "MacOS", exeName));
        var image = (PeFile.ReadIcon(Path.Combine(manifest.Inputs.InstallDir, app.Exe)) is { } ico ? Icons.Largest(ico) : null) ?? productIcon;
        if (image != null) File.WriteAllBytes(Path.Combine(bundle, "Resources", "icon.icns"), Icons.Icns(image.Value));
        File.WriteAllText(Path.Combine(bundle, "Info.plist"), $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>CFBundleIdentifier</key><string>{X(id + "." + DesktopCommon.Segment(Path.GetFileNameWithoutExtension(app.Exe)))}</string>
              <key>CFBundleName</key><string>{X(name)}</string>
              <key>CFBundleDisplayName</key><string>{X(name)}</string>
              <key>CFBundleExecutable</key><string>{exeName}</string>
              <key>CFBundleIconFile</key><string>icon</string>
              <key>CFBundlePackageType</key><string>APPL</string>
              <key>CFBundleShortVersionString</key><string>{X(manifest.Product.Version)}</string>
              <key>CFBundleVersion</key><string>{X(manifest.Product.Version)}</string>
              <key>LSMinimumSystemVersion</key><string>12.0</string>
              <key>NSHighResolutionCapable</key><true/>
            </dict>
            </plist>
            """);
    }

    private static string PreInstall(List<string> daemons) => $"""
        #!/bin/sh
        # an upgrade: the services stopped before their files are replaced
        {string.Concat(daemons.Select(d => $"launchctl bootout system/{d} 2>/dev/null || true\n"))}exit 0

        """;

    private static string PostInstall(string root, List<string> daemons) => $$"""
        #!/bin/sh
        # The machine's state made from the seed on the first install -- the registry's paths made this installation's --
        # and kept on an upgrade; then the services started. WPFPKG_ROOT: a test's root in place of /.
        set -e
        VOLUME="${3:-/}"
        TARGET="${WPFPKG_ROOT:-$VOLUME}"
        ROOT="${TARGET%/}{{root}}"
        STATE="$ROOT/state"
        SEED="$ROOT/seed"
        mkdir -p "$STATE"
        if [ ! -d "$STATE/registry" ] && [ -d "$SEED/registry" ]; then
          mkdir -p "$STATE/registry"
          for f in "$SEED"/registry/*.reg; do
            [ -f "$f" ] || continue
            sed -e "s|\${WPFRUN_INSTALLDIR}|{{root}}/app|g" -e "s|\${WPFRUN_PROGRAMDATA}|{{root}}/state/ProgramData|g" \
                -e "s|\${WPFRUN_APPDIR}|{{root}}/app|g" "$f" > "$STATE/registry/$(basename "$f")"
          done
        fi
        if [ ! -d "$STATE/ProgramData" ] && [ -d "$SEED/ProgramData" ]; then
          mv "$SEED/ProgramData" "$STATE/ProgramData"
        fi
        rm -rf "$SEED"
        # every user's, as Windows' ProgramData and HKLM are writable by its users' programs
        chgrp -R staff "$STATE" 2>/dev/null || true
        chmod -R g+rwX "$STATE"
        mkdir -p "/Library/Logs/{{Path.GetFileName(root)}}" 2>/dev/null || true
        if [ -z "$WPFPKG_ROOT" ]; then
        {{string.Concat(daemons.Select(d => $"  launchctl bootstrap system \"/Library/LaunchDaemons/{d}.plist\" 2>/dev/null || true\n"))}}fi
        exit 0

        """;

    private static string Uninstall(string root, string product, string id, List<string> daemons) => $"""
        #!/bin/sh
        # Removes {product}: its services, its apps and its files. The machine's state (state/: the registry and
        # ProgramData) is kept unless --all is given, as Windows keeps ProgramData after an uninstall.
        if [ "$(id -u)" != 0 ]; then exec sudo "$0" "$@"; fi
        {string.Concat(daemons.Select(d => $"launchctl bootout system/{d} 2>/dev/null || true\nrm -f \"/Library/LaunchDaemons/{d}.plist\"\n"))}rm -f "/Library/LaunchAgents/{id}.startup.plist"
        rm -rf "/Applications/{product}"
        rm -rf "{root}/app" "{root}/runtime" "{root}/seed"
        if [ "$1" = "--all" ]; then rm -rf "{root}" "/Library/Logs/{product}"; fi
        pkgutil --forget "{id}" >/dev/null 2>&1 || true
        echo "{product} removed"

        """;

    // ---- files ----------------------------------------------------------------------------------------------

    private static int CloneTree(string from, string to, GlobSet excludes)
    {
        int n = 0;
        foreach (string f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(from, f).Replace('\\', '/');
            if (excludes.Matches(rel) || Path.GetFileName(f) == ".DS_Store") continue;
            Clone(f, Path.Combine(to, rel));
            n++;
        }
        return n;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int clonefile(string src, string dst, int flags);

    // APFS: a clone shares the source's blocks, so the payload costs no time and no space until pkgbuild reads it
    private static void Clone(string source, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (File.Exists(target)) File.Delete(target);
        if (clonefile(source, target, 0) != 0) File.Copy(source, target);
    }

    private static void Executable(string path) =>
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead
            | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

    private static void Run(string tool, params string[] args)
    {
        var psi = new ProcessStartInfo(tool) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new PackagerError($"{Path.GetFileName(tool)} failed ({p.ExitCode}): {output.Trim()}");
    }

    private static string X(string s) => SecurityElement.Escape(s)!;
}
