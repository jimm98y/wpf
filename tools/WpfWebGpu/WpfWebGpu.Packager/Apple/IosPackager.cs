// An app as an iOS / iPadOS app from the template (ios.pack): the template .app copied, the app's folder put in
// app/ (the host loads its assemblies from there), the registry and ProgramData as the seed the host copies into
// the app's Library on first start, Info.plist given the app's id, name, version and icon -- and sealed again:
//
//   simulator   <App>.app, signed ad hoc (codesign: the simulator is a Mac's)
//   device      <App>.ipa, signed with the manifest's identity and provisioning profile (codesign, on a Mac)

using System.Diagnostics;
using System.IO.Compression;
using System.Text;

namespace WpfWebGpu.Packager.Apple;

public static class IosPackager
{
    private static readonly (string File, int Size)[] s_icons =
    {
        ("AppIcon60x60@2x.png", 120), ("AppIcon60x60@3x.png", 180), ("AppIcon76x76@2x~ipad.png", 152), ("AppIcon83.5x83.5@2x~ipad.png", 167),
    };

    public static List<string> Build(PackageManifest manifest, AppEntry entry, StagedState staged, PackFile pack, string outDir, Action<string> log)
    {
        var made = new List<string>();
        string work = Path.Combine(outDir, ".work", "ios-" + Path.GetFileNameWithoutExtension(entry.Exe));
        Directory.CreateDirectory(work);
        string refs = Path.Combine(pack.Directory, "refs");
        var provided = new HashSet<string>(Directory.EnumerateFiles(refs, "*.dll").Select(Path.GetFileNameWithoutExtension).OfType<string>(), StringComparer.OrdinalIgnoreCase);
        var app = MobileApp.Prepare(manifest.Inputs.InstallDir, entry, provided, new[] { refs }, work, log);
        string label = entry.Name.Length > 0 ? entry.Name : app.Name;
        string id = BundleId(manifest, entry);

        // the simulator's
        string sim = Path.Combine(outDir, "ios-simulator", FileName(label) + ".app");
        Fill(Path.Combine(pack.Directory, "simulator", "WpfRunBundle.app"), sim, manifest, entry, staged, app, id, label);
        if (OperatingSystem.IsMacOS()) Run("/usr/bin/codesign", "--force", "--sign", "-", "--timestamp=none", sim);
        log($"{sim} ({id}; xcrun simctl install booted \"{sim}\")");
        made.Add(sim);

        // a device's, when there is an identity to sign it with
        var signing = manifest.Signing;
        if (signing.AppleSigningIdentity.Length == 0 || signing.AppleProvisioningProfile.Length == 0)
        {
            log("  no device .ipa: signing.appleSigningIdentity and signing.appleProvisioningProfile name the certificate and profile it is signed with");
            return made;
        }
        if (!OperatingSystem.IsMacOS()) throw new PackagerError("a device .ipa is signed on a Mac for now (codesign)");
        string payload = Path.Combine(work, "Payload");
        if (Directory.Exists(payload)) Directory.Delete(payload, recursive: true);
        string device = Path.Combine(payload, FileName(label) + ".app");
        Fill(Path.Combine(pack.Directory, "device", "WpfRunBundle.app"), device, manifest, entry, staged, app, id, label);
        File.Copy(signing.AppleProvisioningProfile, Path.Combine(device, "embedded.mobileprovision"), overwrite: true);
        string entitlements = Path.Combine(work, "entitlements.plist");
        File.WriteAllBytes(entitlements, Entitlements(signing.AppleProvisioningProfile, id));
        // what the bundle holds that is code of its own first (frameworks, dylibs), then the bundle
        foreach (string nested in Directory.EnumerateFileSystemEntries(device, "*", SearchOption.AllDirectories)
                     .Where(p => p.EndsWith(".dylib", StringComparison.Ordinal) || p.EndsWith(".framework", StringComparison.Ordinal))
                     .OrderByDescending(p => p.Length))
            Run("/usr/bin/codesign", "--force", "--sign", signing.AppleSigningIdentity, "--timestamp=none", nested);
        Run("/usr/bin/codesign", "--force", "--sign", signing.AppleSigningIdentity, "--entitlements", entitlements, "--timestamp=none", device);
        string ipa = Path.Combine(outDir, "ios", FileName(label) + ".ipa");
        Directory.CreateDirectory(Path.GetDirectoryName(ipa)!);
        if (File.Exists(ipa)) File.Delete(ipa);
        ZipFile.CreateFromDirectory(payload, ipa, CompressionLevel.Optimal, includeBaseDirectory: true);
        log($"{ipa} ({new FileInfo(ipa).Length / (1024 * 1024)} MB)");
        made.Add(ipa);
        return made;
    }

    private static void Fill(string template, string app, PackageManifest manifest, AppEntry entry, StagedState staged, MobileApp mobile, string id, string label)
    {
        if (Directory.Exists(app)) Directory.Delete(app, recursive: true);
        CopyTree(template, app);
        foreach (var (source, target) in mobile.Files) Copy(source, Path.Combine(app, "app", target));
        foreach (string hive in Directory.EnumerateFiles(staged.RegistryDir, "*.reg"))
        {
            string to = Path.Combine(app, "wpfrun-registry", Path.GetFileName(hive));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.WriteAllText(to, mobile.MapRegistry(File.ReadAllText(hive)));
        }
        var excludes = new GlobSet(manifest.MobileExclude);
        foreach (var (source, target) in staged.ProgramDataFiles)
            if (!excludes.Matches(target)) Copy(source, Path.Combine(app, "wpfrun-state", "ProgramData", target));
        File.WriteAllText(Path.Combine(app, "wpfrun-app.txt"), $"name={mobile.Name}\nexe={mobile.Exe}\nfolder=app\n");

        string infoPath = Path.Combine(app, "Info.plist");
        var info = (PlistDict)Plist.Read(File.ReadAllBytes(infoPath));
        info["CFBundleIdentifier"] = id;
        info["CFBundleName"] = label.Length > 15 ? label[..15] : label;
        info["CFBundleDisplayName"] = label;
        info["CFBundleShortVersionString"] = manifest.Product.Version;
        info["CFBundleVersion"] = manifest.Product.Version;
        if (Desktop.DesktopCommon.Icon(manifest, entry) is { } image)
        {
            foreach (var (file, size) in s_icons) File.WriteAllBytes(Path.Combine(app, file), Icons.PngAt(image, size));
            info["CFBundleIcons"] = new PlistDict { new("CFBundlePrimaryIcon", new PlistDict { new("CFBundleIconFiles", new List<object> { "AppIcon60x60" }) }) };
            info["CFBundleIcons~ipad"] = new PlistDict
            {
                new("CFBundlePrimaryIcon", new PlistDict { new("CFBundleIconFiles", new List<object> { "AppIcon60x60", "AppIcon76x76", "AppIcon83.5x83.5" }) }),
            };
        }
        File.WriteAllBytes(infoPath, Plist.WriteXml(info));
    }

    /// <summary>The entitlements the profile grants, its application identifier made this bundle's.</summary>
    private static byte[] Entitlements(string profile, string id)
    {
        var psi = new ProcessStartInfo("/usr/bin/security") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in new[] { "cms", "-D", "-i", profile }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        string xml = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new PackagerError($"{profile}: not a provisioning profile");
        var decoded = (PlistDict)Plist.Read(Encoding.UTF8.GetBytes(xml));
        var entitlements = decoded["Entitlements"] as PlistDict ?? new PlistDict();
        if (entitlements["application-identifier"] is string appId && appId.EndsWith(".*", StringComparison.Ordinal))
            entitlements["application-identifier"] = appId[..^1] + id;
        return Plist.WriteXml(entitlements);
    }

    public static string BundleId(PackageManifest manifest, AppEntry entry) =>
        Android.AndroidPackager.PackageId(manifest, entry).Replace('_', '-');

    private static void CopyTree(string from, string to)
    {
        foreach (string f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            Copy(f, Path.Combine(to, Path.GetRelativePath(from, f)));
    }

    private static void Copy(string from, string to)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        File.Copy(from, to, overwrite: true);
    }

    private static void Run(string tool, params string[] args)
    {
        var psi = new ProcessStartInfo(tool) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new PackagerError($"{Path.GetFileName(tool)} failed: {output.Trim()}");
    }

    private static string FileName(string s) => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ' ' ? '_' : c));
}
