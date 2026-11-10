// An app as an iOS / iPadOS app from the template (ios.pack): the template .app copied, the app's folder put where
// it is in the installation (install/<folder>: the host loads its assemblies from there), the registry and
// ProgramData as the seed the host copies on first start into the installation's App Group container -- the
// machine's state, shared by every app of the installation as on Windows -- Info.plist given the app's id, name,
// version and icon, its URL scheme and the installation's apps' (Process.Start of one opens it) -- and sealed again:
//
//   simulator   <App>.app, signed ad hoc (codesign: the simulator is a Mac's)
//   device      <App>.ipa, signed with the manifest's identity and provisioning profile (codesign, on a Mac)
//
// Apple Vision Pro the same way, from visionos.pack: the same host made a visionOS app (visionos-simulator/,
// visionos/), its windows in the Shared Space and a 3D view able to fill the eyes in an immersive space.

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

    public static List<string> Build(PackageManifest manifest, AppEntry entry, IReadOnlyList<AppEntry> installation, StagedState staged, PackFile pack, string outDir, Action<string> log,
        bool visionOs = false)
    {
        var made = new List<string>();
        string platform = visionOs ? "visionos" : "ios";
        string work = Path.Combine(outDir, ".work", platform + "-" + Path.GetFileNameWithoutExtension(entry.Exe));
        Directory.CreateDirectory(work);
        string refs = Path.Combine(pack.Directory, "refs");
        var provided = new HashSet<string>(Directory.EnumerateFiles(refs, "*.dll").Select(Path.GetFileNameWithoutExtension).OfType<string>(), StringComparer.OrdinalIgnoreCase);
        var app = MobileApp.Prepare(manifest.Inputs.InstallDir, entry, provided, new[] { refs }, work, log, Path.Combine(pack.Directory, "frameworks"));
        string label = entry.Name.Length > 0 ? entry.Name : app.Name;
        string id = BundleId(manifest, entry);
        string group = AppGroup(manifest);
        var apps = installation.ToDictionary(a => Path.GetFileName(a.Exe).ToLowerInvariant(), a => (object)Scheme(BundleId(manifest, a)));

        // the simulator's
        string sim = Path.Combine(outDir, platform + "-simulator", FileName(label) + ".app");
        Fill(Path.Combine(pack.Directory, "simulator", "WpfRunBundle.app"), sim, manifest, entry, staged, app, id, label, group, apps, installation);
        // ad hoc, with the installation's App Group (the simulator gives a group's container to an app that claims it)
        string simEntitlements = Path.Combine(work, "simulator.entitlements");
        File.WriteAllBytes(simEntitlements, Plist.WriteXml(new PlistDict { new("com.apple.security.application-groups", new List<object> { group }) }));
        LinkedGroup(Path.Combine(sim, "WpfRunBundle"), group);
        if (OperatingSystem.IsMacOS())
        {
            foreach (string nested in NestedCode(sim)) Run("/usr/bin/codesign", "--force", "--sign", "-", "--timestamp=none", nested);
            Run("/usr/bin/codesign", "--force", "--sign", "-", "--entitlements", simEntitlements, "--timestamp=none", sim);
        }
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
        Fill(Path.Combine(pack.Directory, "device", "WpfRunBundle.app"), device, manifest, entry, staged, app, id, label, group, apps, installation);
        File.Copy(signing.AppleProvisioningProfile, Path.Combine(device, "embedded.mobileprovision"), overwrite: true);
        string entitlements = Path.Combine(work, "entitlements.plist");
        File.WriteAllBytes(entitlements, Entitlements(signing.AppleProvisioningProfile, id, group, log));
        // what the bundle holds that is code of its own first (frameworks, dylibs), then the bundle
        foreach (string nested in NestedCode(device))
            Run("/usr/bin/codesign", "--force", "--sign", signing.AppleSigningIdentity, "--timestamp=none", nested);
        Run("/usr/bin/codesign", "--force", "--sign", signing.AppleSigningIdentity, "--entitlements", entitlements, "--timestamp=none", device);
        string ipa = Path.Combine(outDir, platform, FileName(label) + ".ipa");
        Directory.CreateDirectory(Path.GetDirectoryName(ipa)!);
        if (File.Exists(ipa)) File.Delete(ipa);
        ZipFile.CreateFromDirectory(payload, ipa, CompressionLevel.Optimal, includeBaseDirectory: true);
        log($"{ipa} ({new FileInfo(ipa).Length / (1024 * 1024)} MB)");
        made.Add(ipa);
        return made;
    }

    // the code a bundle holds of its own (frameworks, dylibs), deepest first: signed before the bundle that seals them
    private static IEnumerable<string> NestedCode(string bundle) =>
        Directory.EnumerateFileSystemEntries(bundle, "*", SearchOption.AllDirectories)
            .Where(p => p.EndsWith(".dylib", StringComparison.Ordinal) || p.EndsWith(".framework", StringComparison.Ordinal))
            .OrderByDescending(p => p.Length).ToList();

    private static void Fill(string template, string app, PackageManifest manifest, AppEntry entry, StagedState staged, MobileApp mobile, string id, string label,
        string group, Dictionary<string, object> apps, IReadOnlyList<AppEntry> installation)
    {
        if (Directory.Exists(app)) Directory.Delete(app, recursive: true);
        CopyTree(template, app);
        // where the app is in the installation: the registry's installation paths (install/) are every app's
        string folder = Path.Combine("install", mobile.Folder == "." ? "" : mobile.Folder.Trim('/')).TrimEnd('/');
        foreach (var (source, target) in mobile.Files) Copy(source, Path.Combine(app, folder, target));
        // the installation's other apps' .exe where they are on Windows, as another app finds them (File.Exists)
        // before it starts one -- which opens that app
        foreach (AppEntry other in installation)
        {
            string to = Path.Combine(app, "install", other.Exe.Replace('\\', '/'));
            if (!File.Exists(to)) Copy(Path.Combine(manifest.Inputs.InstallDir, other.Exe), to);
        }
        foreach (string hive in Directory.EnumerateFiles(staged.RegistryDir, "*.reg"))
        {
            string to = Path.Combine(app, "wpfrun-registry", Path.GetFileName(hive));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(hive, to, overwrite: true);
        }
        var excludes = new GlobSet(manifest.MobileExclude);
        foreach (var (source, target) in staged.ProgramDataFiles)
            if (!excludes.Matches(target)) Copy(source, Path.Combine(app, "wpfrun-state", "ProgramData", target));
        File.WriteAllText(Path.Combine(app, "wpfrun-app.txt"), $"name={mobile.Name}\nexe={mobile.Exe}\nfolder={folder.Replace('\\', '/')}\nosidentity={manifest.Product.OsIdentity}\n");

        string infoPath = Path.Combine(app, "Info.plist");
        var info = (PlistDict)Plist.Read(File.ReadAllBytes(infoPath));
        info["CFBundleIdentifier"] = id;
        info["CFBundleName"] = label.Length > 15 ? label[..15] : label;
        info["CFBundleDisplayName"] = label;
        info["CFBundleShortVersionString"] = manifest.Product.Version;
        info["CFBundleVersion"] = manifest.Product.Version;
        // opened by another app of the installation through its own scheme; the installation's apps' schemes
        // (Process.Start of one's .exe opens it) and the App Group whose container holds the machine's state
        info["CFBundleURLTypes"] = new List<object> { new PlistDict { new("CFBundleURLName", id), new("CFBundleURLSchemes", new List<object> { Scheme(id) }) } };
        var table = new PlistDict();
        foreach (var (exe, scheme) in apps) table[exe] = scheme;
        info["WpfRunInstallationApps"] = table;
        info["WpfRunAppGroup"] = group;
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
    private static byte[] Entitlements(string profile, string id, string group, Action<string> log)
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
        // the installation's App Group: only what the profile grants can be claimed
        if (entitlements["com.apple.security.application-groups"] is not List<object> groups || !groups.Contains(group))
            log($"  {profile} grants no App Group {group}: the apps keep a machine state each (add the group to the app id and the profile)");
        return Plist.WriteXml(entitlements);
    }

    /// <summary>The App Group the simulator template's executable is linked with (the simulator reads an app's
    /// entitlements from its executable, not its signature), made the installation's in place: the same length,
    /// the rest of it XML's white space.</summary>
    public static readonly string SimulatorGroupPlaceholder = "group.wpfrun.installation." + new string('x', 200);

    /// <summary>The simulator executable's linked entitlements given the installation's App Group.</summary>
    private static void LinkedGroup(string executable, string group)
    {
        byte[] bytes = File.ReadAllBytes(executable);
        byte[] from = Encoding.UTF8.GetBytes("<string>" + SimulatorGroupPlaceholder + "</string>");
        string replacement = "<string>" + group + "</string>";
        if (replacement.Length > from.Length) throw new PackagerError($"the App Group {group} is longer than the simulator template has room for");
        byte[] to = Encoding.UTF8.GetBytes(replacement.PadRight(from.Length));
        int at = bytes.AsSpan().IndexOf(from);
        if (at < 0) throw new PackagerError("the simulator template's executable has no App Group to make the installation's: rebuild ios.pack");
        to.CopyTo(bytes, at);
        File.WriteAllBytes(executable, bytes);
    }

    /// <summary>The installation's App Group: group.&lt;id prefix&gt;.&lt;product&gt;.</summary>
    public static string AppGroup(PackageManifest manifest) => "group." + Android.AndroidPackager.InstallationId(manifest).Replace('_', '-');

    /// <summary>The URL scheme an app is opened by: its bundle id, as a scheme spells it.</summary>
    public static string Scheme(string bundleId) => string.Concat(bundleId.ToLowerInvariant().Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '+'));

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
