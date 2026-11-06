// A manifest's packages for the targets asked for.

using WpfWebGpu.Packager.Android;

namespace WpfWebGpu.Packager;

public static class Builder
{
    public static List<string> Build(PackageManifest manifest, IReadOnlyList<Target> targets, string outDir, string packsFolder, Action<string> log)
    {
        var made = new List<string>();
        var staged = Staging.Stage(manifest, Path.Combine(outDir, ".work"));
        log($"staged: registry {staged.Keys} keys, ProgramData {staged.ProgramDataFiles.Count} files");
        var mobile = manifest.Apps.Where(a => a.Mobile).ToList();

        // Android and Quest are one APK (both activities: the panel, and the immersive one a headset starts)
        if (targets.Contains(Target.Android) || targets.Contains(Target.Quest))
        {
            if (mobile.Count == 0) throw new PackagerError("no app is marked \"mobile\": nothing to make for Android or Quest");
            var pack = PackFile.Open(packsFolder, "android");
            foreach (var app in mobile)
                made.Add(AndroidPackager.Build(manifest, app, staged, pack, Path.Combine(outDir, "android"), log));
        }
        if (targets.Contains(Target.MacOS))
        {
            // a package per architecture the packs folder has a runtime for
            var rids = Directory.Exists(packsFolder)
                ? Directory.EnumerateFiles(packsFolder, "desktop-osx-*.pack").Select(f => Path.GetFileNameWithoutExtension(f)["desktop-".Length..]).Order().ToList()
                : new List<string>();
            if (rids.Count == 0) throw new PackagerError($"no desktop-osx-<arch>.pack in {packsFolder}: the macOS runtime is not installed");
            foreach (string rid in rids)
                made.Add(Desktop.MacPackager.Build(manifest, staged, PackFile.Open(packsFolder, "desktop-" + rid), Path.Combine(outDir, "macos"), log));
        }
        if (targets.Contains(Target.Linux))
        {
            var rids = Directory.Exists(packsFolder)
                ? Directory.EnumerateFiles(packsFolder, "desktop-linux-*.pack").Select(f => Path.GetFileNameWithoutExtension(f)["desktop-".Length..]).Order().ToList()
                : new List<string>();
            if (rids.Count == 0) throw new PackagerError($"no desktop-linux-<arch>.pack in {packsFolder}: the Linux runtime is not installed");
            foreach (string rid in rids)
                made.Add(Desktop.LinuxPackager.Build(manifest, staged, PackFile.Open(packsFolder, "desktop-" + rid), Path.Combine(outDir, "linux"), log));
        }
        if (targets.Contains(Target.Windows))
        {
            var rids = Directory.Exists(packsFolder)
                ? Directory.EnumerateFiles(packsFolder, "desktop-win-*.pack").Select(f => Path.GetFileNameWithoutExtension(f)["desktop-".Length..]).Order().ToList()
                : new List<string>();
            if (rids.Count == 0) throw new PackagerError($"no desktop-win-<arch>.pack in {packsFolder}: the Windows runtime is not installed");
            foreach (string rid in rids)
                made.Add(Desktop.WindowsPackager.Build(manifest, staged, PackFile.Open(packsFolder, "desktop-" + rid), PackFile.Open(packsFolder, "setup-" + rid), Path.Combine(outDir, "windows"), log));
        }
        if (targets.Contains(Target.IOS))
        {
            if (mobile.Count == 0) throw new PackagerError("no app is marked \"mobile\": nothing to make for iOS");
            var pack = PackFile.Open(packsFolder, "ios");
            foreach (var app in mobile)
                made.AddRange(Apple.IosPackager.Build(manifest, app, staged, pack, outDir, log));
        }
        foreach (var t in targets.Where(t => t is not (Target.Android or Target.Quest or Target.MacOS or Target.Linux or Target.Windows or Target.IOS)))
            log($"{t}: not made yet by this Packager");
        return made;
    }
}
