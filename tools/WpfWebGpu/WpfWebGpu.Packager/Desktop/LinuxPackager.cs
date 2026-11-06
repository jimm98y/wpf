// The whole installation as a Debian package (.deb), written in managed code -- an ar archive of debian-binary,
// control.tar.gz and data.tar.gz -- so it is made on any machine, with nothing installed:
//
//   /opt/<product>/app/          the Program Files folder, as it is
//   /opt/<product>/runtime/      what runs it (desktop-linux-<arch>.pack: .NET, wpfrun)
//   /opt/<product>/seed/         the registry (paths as tokens) and ProgramData: made the machine's state on first install
//   /var/opt/<product>/state/    the machine's state (WPFRUN_STATE), kept on upgrades and on remove (purge removes it)
//   /lib/systemd/system/<pkg>-<service>.service       each service, as the SCM runs it
//   /usr/share/applications/<pkg>-<app>.desktop       each window app, with its icon
//
// The state is writable by the group 'users', as ProgramData and HKLM are by a Windows machine's users.

using System.Formats.Tar;
using System.IO.Compression;
using System.Text;

namespace WpfWebGpu.Packager.Desktop;

public static class LinuxPackager
{
    public static string Build(PackageManifest manifest, StagedState staged, PackFile runtimePack, string outDir, Action<string> log)
    {
        string product = manifest.Product.Name;
        string rid = runtimePack["rid"];
        string arch = rid.EndsWith("arm64", StringComparison.Ordinal) ? "arm64" : "amd64";
        string pkg = PackageName(product);
        string root = "/opt/" + product, state = "/var/opt/" + product + "/state";
        string work = Path.Combine(outDir, ".work", "linux-" + arch);
        if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        Directory.CreateDirectory(work);
        var excludes = new GlobSet(manifest.Exclude);

        // ---- the files, into data.tar.gz ----
        log($"Linux ({arch}): the installation into {root}");
        string data = Path.Combine(work, "data.tar.gz");
        long installedBytes = 0;
        var units = new List<(string Name, bool Enable)>();
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
            log($"  {files} files of the installation, {staged.ProgramDataFiles.Count} of ProgramData");

            foreach (var service in manifest.Services.Where(s => s.Start != StartType.Disabled))
            {
                string unit = UnitName(pkg, service.Name);
                tar.AddText($"/lib/systemd/system/{unit}", Unit(manifest, service, pkg, root, state), executable: false);
                units.Add((unit, service.Start == StartType.Auto));
            }
            foreach (var app in manifest.Apps.Where(a => a.Kind == AppKind.Gui && a.Shortcut))
            {
                string id = pkg + "-" + DesktopCommon.Segment(Path.GetFileNameWithoutExtension(app.Exe));
                tar.AddText($"/usr/share/applications/{id}.desktop", Desktop(app, id, root, state), executable: false);
                if (DesktopCommon.Icon(manifest, app) is { } image)
                    tar.AddBytes($"/usr/share/icons/hicolor/256x256/apps/{id}.png", Icons.PngAt(image, 256));
            }
            tar.Finish();
        }

        // ---- control.tar.gz ----
        string control = Path.Combine(work, "control.tar.gz");
        using (var file = File.Create(control))
        using (var gz = new GZipStream(file, CompressionLevel.Optimal))
        {
            var tar = new TarTree(gz, directories: false);
            tar.AddText("./control", Control(manifest, pkg, arch, installedBytes), executable: false);
            tar.AddText("./postinst", PostInst(root, state, units), executable: true);
            tar.AddText("./prerm", PreRm(units), executable: true);
            tar.AddText("./postrm", PostRm(state, units), executable: true);
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
        Directory.Delete(work, recursive: true);
        log($"{deb} ({new FileInfo(deb).Length / (1024 * 1024)} MB)");
        return deb;
    }

    // ---- what is installed ---------------------------------------------------------------------------------

    private static string Unit(PackageManifest manifest, ServiceEntry service, string pkg, string root, string state)
    {
        string exe = $"{root}/app/{service.Exe}";
        string args = string.Concat(service.Args.Select(a => " " + Quote(a)));
        var deps = service.DependsOn.Select(d => UnitName(pkg, d)).ToList();
        string after = string.Join(" ", new[] { "network-online.target" }.Concat(deps));
        return $"""
            [Unit]
            Description={(service.DisplayName.Length > 0 ? service.DisplayName : service.Name)} ({manifest.Product.Name})
            After={after}
            Wants=network-online.target
            {(deps.Count > 0 ? "Requires=" + string.Join(" ", deps) + "\n" : "")}
            [Service]
            Type=simple
            ExecStart={Quote(root + "/runtime/run")} {Quote(exe)}{args}
            WorkingDirectory={root}/app/{Path.GetDirectoryName(service.Exe)!.Replace('\\', '/')}
            Environment=WPFRUN_STATE={state}
            Environment=WPFRUN_CACHE=/var/cache/{pkg}
            Environment=WPFRUN_LOGS=/var/log/{pkg}
            UMask=0002
            Restart=on-failure
            RestartSec=10
            TimeoutStopSec=30
            SyslogIdentifier={Path.GetFileNameWithoutExtension(service.Exe)}

            [Install]
            WantedBy=multi-user.target

            """;
    }

    private static string Desktop(AppEntry app, string id, string root, string state) => $"""
        [Desktop Entry]
        Type=Application
        Name={DesktopCommon.AppName(app)}
        Exec=env WPFRUN_STATE={state} {Quote(root + "/runtime/run")} {Quote(root + "/app/" + app.Exe)}
        Icon={id}
        Terminal=false
        Categories=Utility;

        """;

    private static string Control(PackageManifest manifest, string pkg, string arch, long bytes) => $"""
        Package: {pkg}
        Version: {DebVersion(manifest.Product.Version)}
        Architecture: {arch}
        Maintainer: {(manifest.Product.Publisher.Length > 0 ? manifest.Product.Publisher : manifest.Product.Name)} <noreply@localhost>
        Installed-Size: {Math.Max(1, bytes / 1024)}
        Depends: libc6, zlib1g, libstdc++6, libgcc-s1, libssl3 | libssl1.1, libicu74 | libicu72 | libicu70 | libicu67 | libicu66
        Recommends: libvulkan1, mesa-vulkan-drivers, libwayland-client0, libxkbcommon0, libfontconfig1
        Section: misc
        Priority: optional
        Description: {manifest.Product.Name}
         The Windows installation, run on Linux by WPF and WinForms on WebGPU.

        """;

    private static string PostInst(string root, string state, List<(string Name, bool Enable)> units) => $$"""
        #!/bin/sh
        # the machine's state made from the seed on the first install (the registry's paths this installation's) and
        # kept on an upgrade; then the services enabled and started
        set -e
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
        # every user's, as Windows' ProgramData and HKLM are writable by its users' programs
        chgrp -R users "$STATE" 2>/dev/null || true
        chmod -R g+rwX "$STATE"
        find "$STATE" -type d -exec chmod g+s {} + 2>/dev/null || true
        if [ -d /run/systemd/system ]; then
          systemctl daemon-reload || true
        {{string.Concat(units.Where(u => u.Enable).Select(u => $"  systemctl enable --now {u.Name} || true\n"))}}fi
        command -v update-desktop-database >/dev/null && update-desktop-database -q /usr/share/applications || true
        command -v gtk-update-icon-cache >/dev/null && gtk-update-icon-cache -q /usr/share/icons/hicolor || true
        exit 0

        """;

    private static string PreRm(List<(string Name, bool Enable)> units) => $"""
        #!/bin/sh
        # the services stopped before their files go (an upgrade starts them again in postinst)
        if [ -d /run/systemd/system ]; then
        {string.Concat(units.Select(u => $"  systemctl stop {u.Name} || true\n"))}fi
        exit 0

        """;

    private static string PostRm(string state, List<(string Name, bool Enable)> units) => $"""
        #!/bin/sh
        # remove keeps the machine's state (the registry, ProgramData), as Windows keeps ProgramData; purge removes it
        if [ "$1" = remove ] || [ "$1" = purge ]; then
          if [ -d /run/systemd/system ]; then
        {string.Concat(units.Select(u => $"    systemctl disable {u.Name} >/dev/null 2>&1 || true\n"))}    systemctl daemon-reload || true
          fi
        fi
        if [ "$1" = purge ]; then rm -rf "{Path.GetDirectoryName(state)}"; fi
        exit 0

        """;

    // ---- names --------------------------------------------------------------------------------------------

    /// <summary>Debian's package name: lower case letters, digits, '+', '-', '.'.</summary>
    public static string PackageName(string product)
    {
        string name = new string(product.ToLowerInvariant().Where(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.').ToArray());
        return name.Length < 2 || !char.IsAsciiLetterOrDigit(name[0]) ? "app" + name : name;
    }

    private static string UnitName(string pkg, string service) => $"{pkg}-{DesktopCommon.Segment(service)}.service";

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
