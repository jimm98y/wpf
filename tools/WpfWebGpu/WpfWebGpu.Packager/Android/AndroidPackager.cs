// An app as an Android APK (phones, tablets, Meta Quest and Android XR headsets) from the template (android.pack):
// the template's entries copied as they are, its manifest given the app's id, name and version, its icon the
// app's, the app's folder, the registry and ProgramData added as assets (which the host unpacks on first start),
// and the whole signed. No Android SDK, no JDK, no build.

using System.Text;
using WpfWebGpu.Packager.Archives;

namespace WpfWebGpu.Packager.Android;

public static class AndroidPackager
{
    // already compressed: stored, as aapt stores them
    private static readonly HashSet<string> s_stored = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".mp3", ".mp4", ".m4a", ".ogg", ".zip", ".gz", ".br", ".7z", ".woff", ".woff2", ".jar", ".apk",
    };

    public static string Build(PackageManifest manifest, AppEntry entry, StagedState staged, PackFile pack, string outDir, Action<string> log)
    {
        string work = Path.Combine(outDir, ".work", "android-" + Path.GetFileNameWithoutExtension(entry.Exe));
        Directory.CreateDirectory(work);
        string refs = Path.Combine(pack.Directory, "refs");
        var provided = new HashSet<string>(Directory.EnumerateFiles(refs, "*.dll").Select(Path.GetFileNameWithoutExtension).OfType<string>(), StringComparer.OrdinalIgnoreCase);
        var app = MobileApp.Prepare(manifest.Inputs.InstallDir, entry, provided, new[] { refs }, work, log, Path.Combine(pack.Directory, "frameworks"));
        log($"{app.Name}: {app.Files.Count} files{(app.LeftOut.Count > 0 ? $" ({app.LeftOut.Count} left out: {string.Join(", ", app.LeftOut.Take(6))}{(app.LeftOut.Count > 6 ? ", ..." : "")})" : "")}");

        string id = PackageId(manifest, entry);
        string label = entry.Name.Length > 0 ? entry.Name : app.Name;
        string unsigned = Path.Combine(work, "unsigned.apk");
        long cdOffset;
        var assets = new List<string>();
        using (var template = new ZipReader(Path.Combine(pack.Directory, "template.apk")))
        using (var file = File.Create(unsigned))
        {
            var zip = new ZipWriter(file);
            var image = Icon(manifest, entry);
            foreach (var e in template.Entries)
            {
                if (e.Name.StartsWith("META-INF/", StringComparison.Ordinal) && IsSignatureFile(e.Name)) continue;   // the template's signature
                if (e.Name is "assets/wpfrun-app.txt" or "assets/wpfrun-assets.txt") continue;
                if (e.Name == "AndroidManifest.xml")
                {
                    zip.Add(e.Name, Manifest(template.Read(e), pack, id, label, manifest.Product.Version), compress: true);
                    continue;
                }
                if (image != null && e.Name.StartsWith("res/mipmap-", StringComparison.Ordinal) && e.Name.EndsWith("/appicon.png", StringComparison.Ordinal)
                    && Icons.AndroidMipmaps.FirstOrDefault(m => e.Name.StartsWith("res/mipmap-" + m.Density + "-", StringComparison.Ordinal) || e.Name.StartsWith("res/mipmap-" + m.Density + "/", StringComparison.Ordinal)) is { Size: > 0 } mip)
                {
                    zip.Add(e.Name, Icons.PngAt(image.Value, mip.Size), compress: false);
                    continue;
                }
                zip.CopyFrom(template, e);
            }

            void Asset(string source, string path)
            {
                using var s = File.OpenRead(source);
                zip.Add("assets/" + path, s, compress: !s_stored.Contains(Path.GetExtension(path)));
                assets.Add(path);
            }
            foreach (var (source, target) in app.Files) Asset(source, "app/" + target);
            foreach (string hive in Directory.EnumerateFiles(staged.RegistryDir, "*.reg"))
            {
                byte[] text = Encoding.UTF8.GetBytes(app.MapRegistry(File.ReadAllText(hive)));
                zip.Add("assets/registry/" + Path.GetFileName(hive), text, compress: true);
                assets.Add("registry/" + Path.GetFileName(hive));
            }
            var mobileExcludes = new GlobSet(manifest.MobileExclude);
            foreach (var (source, target) in staged.ProgramDataFiles)
                if (!mobileExcludes.Matches(target)) Asset(source, "state/ProgramData/" + target);

            zip.Add("assets/wpfrun-assets.txt", Encoding.UTF8.GetBytes(string.Join("\n", assets) + "\n"), compress: true);
            zip.Add("assets/wpfrun-app.txt", Encoding.UTF8.GetBytes($"name={app.Name}\nexe={app.Exe}\nstamp={Guid.NewGuid():N}\nosidentity={manifest.Product.OsIdentity}\n"), compress: false);
            cdOffset = zip.Finish();
        }

        Directory.CreateDirectory(outDir);
        string apk = Path.Combine(outDir, FileName(label) + ".apk");
        ApkSigner.Sign(unsigned, cdOffset, ApkSigner.LoadKey(manifest.Signing.AndroidKeystore, manifest.Signing.AndroidKeyAlias), apk);
        File.Delete(unsigned);
        log($"{apk} ({new FileInfo(apk).Length / (1024 * 1024)} MB, {id})");
        return apk;
    }

    /// <summary>The installation's mobile apps as one APK: each app a slot of the template (its own launcher icon,
    /// process and task), its folder where it is in the installation (install/&lt;folder&gt;), the registry and
    /// ProgramData once, shared by them all as the machine's are on Windows; a file the apps have alike stored once.
    /// Process.Start of one app's .exe by another starts that app (the host's StartApp, through wpfrun-apps.txt).</summary>
    public static string BuildInstallation(PackageManifest manifest, IReadOnlyList<AppEntry> entries, StagedState staged, PackFile pack, string outDir, Action<string> log)
    {
        string work = Path.Combine(outDir, ".work", "android-installation");
        Directory.CreateDirectory(work);
        string refs = Path.Combine(pack.Directory, "refs");
        var provided = new HashSet<string>(Directory.EnumerateFiles(refs, "*.dll").Select(Path.GetFileNameWithoutExtension).OfType<string>(), StringComparer.OrdinalIgnoreCase);
        string ns = pack["templateNamespace"];
        var apps = new List<(int Slot, AppEntry Entry, MobileApp App, string Label)>();
        foreach (var entry in entries)
        {
            var app = MobileApp.Prepare(manifest.Inputs.InstallDir, entry, provided, new[] { refs }, Path.Combine(work, Path.GetFileNameWithoutExtension(entry.Exe)), log, Path.Combine(pack.Directory, "frameworks"));
            log($"{app.Name}: {app.Files.Count} files in {app.Folder}{(app.LeftOut.Count > 0 ? $" ({app.LeftOut.Count} left out)" : "")}");
            apps.Add((apps.Count + 1, entry, app, entry.Name.Length > 0 ? entry.Name : app.Name));
        }

        string id = InstallationId(manifest);
        string stamp = Guid.NewGuid().ToString("N");
        string unsigned = Path.Combine(work, "unsigned.apk");
        long cdOffset;
        var assets = new List<string>();
        using (var template = new ZipReader(Path.Combine(pack.Directory, "template.apk")))
        using (var file = File.Create(unsigned))
        {
            var zip = new ZipWriter(file);
            var icons = apps.ToDictionary(a => a.Slot, a => Icon(manifest, a.Entry));
            foreach (var e in template.Entries)
            {
                if (e.Name.StartsWith("META-INF/", StringComparison.Ordinal) && IsSignatureFile(e.Name)) continue;
                if (e.Name.StartsWith("assets/wpfrun-app", StringComparison.Ordinal) || e.Name == "assets/wpfrun-assets.txt") continue;
                if (e.Name == "AndroidManifest.xml")
                {
                    zip.Add(e.Name, InstallationManifest(template.Read(e), pack, ns, id, apps.Select(a => (a.Slot, a.Label)).ToList(), manifest.Product.Version), compress: true);
                    continue;
                }
                // a slot's icon: res/mipmap-<density>/appicon<slot>.png (the first slot's appicon.png)
                if (e.Name.StartsWith("res/mipmap-", StringComparison.Ordinal) && System.Text.RegularExpressions.Regex.Match(e.Name, @"/appicon(\d*)\.png$") is { Success: true } m)
                {
                    int slot = m.Groups[1].Value.Length == 0 ? 1 : int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                    if (icons.TryGetValue(slot, out var image) && image != null
                        && Icons.AndroidMipmaps.FirstOrDefault(mm => e.Name.StartsWith("res/mipmap-" + mm.Density + "-", StringComparison.Ordinal) || e.Name.StartsWith("res/mipmap-" + mm.Density + "/", StringComparison.Ordinal)) is { Size: > 0 } mip)
                    {
                        zip.Add(e.Name, Icons.PngAt(image.Value, mip.Size), compress: false);
                        continue;
                    }
                }
                zip.CopyFrom(template, e);
            }

            // each distinct file once; another path with the same bytes names the stored one
            // (a file two apps both use -- a shared assembly -- is the installation's one file, put in once)
            var stored = new Dictionary<(long, string), string>();
            var paths = new HashSet<string>(StringComparer.Ordinal);
            void Asset(string source, string path)
            {
                if (!paths.Add(path)) return;
                var info = new FileInfo(source);
                string hash;
                using (var h = File.OpenRead(source)) hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(h));
                if (stored.TryGetValue((info.Length, hash), out string? first)) { assets.Add(path + "\t" + first); return; }
                using var s = File.OpenRead(source);
                zip.Add("assets/" + path, s, compress: !s_stored.Contains(Path.GetExtension(path)));
                stored[(info.Length, hash)] = path;
                assets.Add(path);
            }
            foreach (var a in apps)
            {
                string folder = a.App.Folder == "." ? "" : a.App.Folder.Trim('/') + "/";
                foreach (var (source, target) in a.App.Files) Asset(source, "install/" + folder + target);
            }
            // the installation's registry as staged: its paths under the installation (install/) are every app's
            foreach (string hive in Directory.EnumerateFiles(staged.RegistryDir, "*.reg"))
            {
                zip.Add("assets/registry/" + Path.GetFileName(hive), File.ReadAllBytes(hive), compress: true);
                assets.Add("registry/" + Path.GetFileName(hive));
            }
            var mobileExcludes = new GlobSet(manifest.MobileExclude);
            foreach (var (source, target) in staged.ProgramDataFiles)
                if (!mobileExcludes.Matches(target)) Asset(source, "state/ProgramData/" + target);

            zip.Add("assets/wpfrun-assets.txt", Encoding.UTF8.GetBytes(string.Join("\n", assets) + "\n"), compress: true);
            foreach (var a in apps)
            {
                string folder = a.App.Folder == "." ? "" : a.App.Folder.Trim('/');
                zip.Add(a.Slot == 1 ? "assets/wpfrun-app.txt" : $"assets/wpfrun-app{a.Slot}.txt",
                    Encoding.UTF8.GetBytes($"name={a.App.Name}\nexe={a.App.Exe}\nfolder={folder}\nstamp={stamp}\nosidentity={manifest.Product.OsIdentity}\n"), compress: false);
            }
            zip.Add("assets/wpfrun-apps.txt", Encoding.UTF8.GetBytes(string.Concat(apps.Select(a =>
                $"{a.Slot}\t{(a.App.Folder == "." ? "" : a.App.Folder.Trim('/'))}\t{a.App.Exe}\n"))), compress: false);
            cdOffset = zip.Finish();
        }

        Directory.CreateDirectory(outDir);
        string apk = Path.Combine(outDir, FileName(manifest.Product.Name) + ".apk");
        ApkSigner.Sign(unsigned, cdOffset, ApkSigner.LoadKey(manifest.Signing.AndroidKeystore, manifest.Signing.AndroidKeyAlias), apk);
        File.Delete(unsigned);
        log($"{apk} ({new FileInfo(apk).Length / (1024 * 1024)} MB, {id}: {string.Join(", ", apps.Select(a => a.Label))})");
        return apk;
    }

    private static byte[] InstallationManifest(byte[] axml, PackFile pack, string ns, string id, IReadOnlyList<(int Slot, string Label)> apps, string version)
    {
        var m = new Axml(axml);
        if (m.ReplacePrefix(pack["templateId"], id) == 0) throw new PackagerError("the template's manifest does not name its package id");
        foreach (var (slot, label) in apps)
        {
            if (slot == 1) { m.ReplaceWithin(pack["templateLabel"], label); continue; }
            if (m.ReplaceWithin("wpfrun-slot-" + slot, label) == 0 || !m.EnableElement($"{ns}.WpfRunApp{slot}"))
                throw new PackagerError($"the Android template has no slot {slot} for {label}: rebuild android.pack (wpfrun pack --android)");
        }
        m.SetIntAttribute("versionCode", VersionCode(version));
        for (int i = 0; i < m.Strings.Count; i++)
            if (m.Strings[i] == "1.0") m.Strings[i] = version;
        return m.ToArray();
    }

    /// <summary>The installation's package id: the id prefix and the product's name.</summary>
    public static string InstallationId(PackageManifest manifest)
    {
        string Segment(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s.ToLowerInvariant()) if (char.IsAsciiLetterOrDigit(c) || c == '_') sb.Append(c);
            string seg = sb.ToString();
            return seg.Length == 0 || char.IsAsciiDigit(seg[0]) ? "app" + seg : seg;
        }
        string prefix = string.Join('.', manifest.Product.IdPrefix.Split('.', StringSplitOptions.RemoveEmptyEntries).Select(Segment));
        return (prefix.Length > 0 ? prefix + "." : "") + Segment(manifest.Product.Name);
    }

    private static bool IsSignatureFile(string name) =>
        name == "META-INF/MANIFEST.MF" || name.EndsWith(".SF", StringComparison.Ordinal) || name.EndsWith(".RSA", StringComparison.Ordinal)
        || name.EndsWith(".DSA", StringComparison.Ordinal) || name.EndsWith(".EC", StringComparison.Ordinal);

    private static byte[] Manifest(byte[] axml, PackFile pack, string id, string label, string version)
    {
        var m = new Axml(axml);
        if (m.ReplacePrefix(pack["templateId"], id) == 0) throw new PackagerError("the template's manifest does not name its package id");
        m.ReplaceWithin(pack["templateLabel"], label);
        m.SetIntAttribute("versionCode", VersionCode(version));
        for (int i = 0; i < m.Strings.Count; i++)
            if (m.Strings[i] == "1.0") m.Strings[i] = version;                    // versionName, as the template has it
        return m.ToArray();
    }

    /// <summary>1.2.3 as 1002003: a later version installs over an earlier one.</summary>
    public static int VersionCode(string version)
    {
        var parts = version.Split('.', '-', '+').Select(p => int.TryParse(p, out int v) ? Math.Clamp(v, 0, 999) : 0).Concat(new[] { 0, 0, 0 }).Take(3).ToArray();
        return Math.Max(1, Math.Min(2_100_000_000, parts[0] * 1_000_000 + parts[1] * 1_000 + parts[2]));
    }

    public static string PackageId(PackageManifest manifest, AppEntry entry)
    {
        string Segment(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s.ToLowerInvariant()) if (char.IsAsciiLetterOrDigit(c) || c == '_') sb.Append(c);
            string seg = sb.ToString();
            return seg.Length == 0 || char.IsAsciiDigit(seg[0]) ? "app" + seg : seg;
        }
        string prefix = string.Join('.', manifest.Product.IdPrefix.Split('.', StringSplitOptions.RemoveEmptyEntries).Select(Segment));
        // the app's own name: the assembly its .exe starts (App for App_ARM64.exe), not the file's architecture variant
        string exe = Path.Combine(manifest.Inputs.InstallDir, entry.Exe);
        return (prefix.Length > 0 ? prefix + "." : "") + Segment(Path.GetFileNameWithoutExtension(AppHost.AppDll(exe) ?? exe));
    }

    private static (byte[] Rgba, int Width, int Height)? Icon(PackageManifest manifest, AppEntry entry)
    {
        byte[]? ico = PeFile.ReadIcon(Path.Combine(manifest.Inputs.InstallDir, entry.Exe));
        if (ico == null && manifest.Product.Icon.Length > 0)
        {
            string p = Path.Combine(manifest.Inputs.InstallDir, manifest.Product.Icon);
            ico = p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? PeFile.ReadIcon(p) : File.Exists(p) ? File.ReadAllBytes(p) : null;
        }
        return ico == null ? null : Icons.Largest(ico);
    }

    private static string FileName(string s) => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ' ' ? '_' : c));
}
