// wpfrun pack: the Packager's templates (its packs/ folder), built where the toolchains are -- a head built once
// with no app in it, which the Packager fills with an app's files, names and icon without a build.
//
//   android.pack   the Android/Quest host APK (both activities, the OpenXR loader), every assembly kept whole,
//                  and those assemblies (refs/) for the rewrite of an app's onto the stack.
//
// A pack is a zip: pack.json (what it is, the stack's version), its files.

using System.IO.Compression;
using System.Text.Json;

namespace WpfWebGpu;

internal static class Pack
{
    public static int Run(string[] args)
    {
        string? outDir = null, feed = null;
        bool android = false, verbose = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--out" or "-o": outDir = args[++i]; break;
                case "--feed": feed = args[++i]; break;
                case "--android": android = true; break;
                case "-v" or "--verbose": verbose = true; break;
                default: throw new UserError($"unknown option '{args[i]}'.");
            }
        }
        if (!android) throw new UserError("which template? --android");
        outDir = Path.GetFullPath(outDir ?? Path.Combine(Environment.CurrentDirectory, "packs"));
        feed = Path.GetFullPath(feed ?? Bundle.FindFeedForPack() ?? throw new UserError("no package feed with WpfWebGpu.Sdk found beside wpfrun; pass --feed <folder>."));
        Directory.CreateDirectory(outDir);
        return PackAndroid(outDir, feed, verbose);
    }

    private static int PackAndroid(string outDir, string feed, bool verbose)
    {
        string project = Path.Combine(Locations.CacheRoot, "bundles", "template-android");
        if (Directory.Exists(project)) Directory.Delete(project, recursive: true);
        Directory.CreateDirectory(project);
        Bundle.WriteAndroidTemplateProject(project, feed, Bundle.OpenXrLoaderForPack());
        Console.WriteLine($"wpfrun pack: building the Android template in {project}");
        int code = Bundle.DotnetForPack(verbose, "build", Path.Combine(project, "Bundle.csproj"), "-c", "Release", "-nologo");
        if (code != 0) return code;

        string bin = Path.Combine(project, "bin", "Release", "net10.0-android", "android-arm64");
        string apk = Path.Combine(bin, Bundle.AndroidTemplateId + ".apk");
        if (!File.Exists(apk)) apk = Path.Combine(bin, Bundle.AndroidTemplateId + "-Signed.apk");
        if (!File.Exists(apk)) throw new UserError($"the build made no APK in {bin}.");
        // the assemblies the APK holds, as the build left them (the linker's output: every one, kept whole)
        string linked = Path.Combine(project, "obj", "Release", "net10.0-android", "android-arm64", "linked");
        if (!Directory.Exists(linked)) throw new UserError($"no {linked}: the template's assemblies are not where the build leaves them.");

        string pack = Path.Combine(outDir, "android.pack");
        if (File.Exists(pack)) File.Delete(pack);
        using (var zip = ZipFile.Open(pack, ZipArchiveMode.Create))
        {
            var refs = Directory.EnumerateFiles(linked, "*.dll").Where(f => !f.EndsWith(".resources.dll", StringComparison.OrdinalIgnoreCase)).ToList();
            var info = new Dictionary<string, object>
            {
                ["kind"] = "android",
                ["version"] = typeof(Pack).Assembly.GetName().Version?.ToString() ?? "0",
                ["templateId"] = Bundle.AndroidTemplateId,
                ["templateLabel"] = Bundle.AndroidTemplateLabel,
                ["templateNamespace"] = Bundle.AndroidTemplateNamespace,
                ["built"] = DateTime.UtcNow.ToString("O"),
            };
            using (var w = new StreamWriter(zip.CreateEntry("pack.json").Open()))
                w.Write(JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true }));
            zip.CreateEntryFromFile(apk, "template.apk", CompressionLevel.NoCompression);
            foreach (string r in refs) zip.CreateEntryFromFile(r, "refs/" + Path.GetFileName(r), CompressionLevel.Optimal);
            Console.WriteLine($"wpfrun pack: {pack}: the template APK ({new FileInfo(apk).Length / (1024 * 1024)} MB) and {refs.Count} assemblies");
        }
        return 0;
    }
}
