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
        foreach (var t in targets.Where(t => t is not (Target.Android or Target.Quest)))
            log($"{t}: not made yet by this Packager");
        return made;
    }
}
