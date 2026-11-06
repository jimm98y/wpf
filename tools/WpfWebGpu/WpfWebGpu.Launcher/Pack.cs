// wpfrun pack: the Packager's templates (its packs/ folder), built where the toolchains are -- a head built once
// with no app in it, which the Packager fills with an app's files, names and icon without a build.
//
//   android.pack   the Android/Quest host APK (both activities, the OpenXR loader), every assembly kept whole,
//                  and those assemblies (refs/) for the rewrite of an app's onto the stack.
//   desktop-<rid>.pack   runtime/: what runs an installation's programs on that OS with nothing installed --
//                  a private .NET (with ASP.NET Core, which servers use), wpfrun (launcher/, overlay/ from
//                  artifacts/wpfrun/<rid>, made by build.sh -r <rid>) and `run`, which starts a program with them.
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
        var desktop = new List<string>();
        string? wpfrunBuilds = null;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--out" or "-o": outDir = args[++i]; break;
                case "--feed": feed = args[++i]; break;
                case "--android": android = true; break;
                case "--desktop": desktop.AddRange(args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries)); break;
                case "--wpfrun": wpfrunBuilds = args[++i]; break;
                case "-v" or "--verbose": verbose = true; break;
                default: throw new UserError($"unknown option '{args[i]}'.");
            }
        }
        if (!android && desktop.Count == 0) throw new UserError("which template? --android, --desktop <rid>[,<rid>...]");
        outDir = Path.GetFullPath(outDir ?? Path.Combine(Environment.CurrentDirectory, "packs"));
        Directory.CreateDirectory(outDir);
        foreach (string rid in desktop)
        {
            int code = PackDesktop(outDir, rid, Path.GetFullPath(wpfrunBuilds ?? Path.Combine(Bundle.FindFeedForPack() ?? ".", "..", "wpfrun")));
            if (code != 0) return code;
        }
        if (!android) return 0;
        feed = Path.GetFullPath(feed ?? Bundle.FindFeedForPack() ?? throw new UserError("no package feed with WpfWebGpu.Sdk found beside wpfrun; pass --feed <folder>."));
        return PackAndroid(outDir, feed, verbose);
    }

    // ---- desktop ----------------------------------------------------------------------------------------

    private static int PackDesktop(string outDir, string rid, string wpfrunBuilds)
    {
        string build = Path.Combine(wpfrunBuilds, rid);
        foreach (string part in new[] { "launcher", "overlay" })
            if (!Directory.Exists(Path.Combine(build, part)))
                throw new UserError($"no {Path.Combine(build, part)}: build wpfrun for {rid} first (tools/WpfWebGpu/build.sh -r {rid}, or build.cmd on Windows)");

        string runtimeArchive = DotnetRuntime(rid, out string version);
        string staging = Path.Combine(Locations.CacheRoot, "packs", "desktop-" + rid);
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        string runtime = Path.Combine(staging, "runtime");
        Directory.CreateDirectory(Path.Combine(runtime, "dotnet"));
        Extract(runtimeArchive, Path.Combine(runtime, "dotnet"));
        CopyTree(Path.Combine(build, "launcher"), Path.Combine(runtime, "launcher"));
        CopyTree(Path.Combine(build, "overlay"), Path.Combine(runtime, "overlay"));
        bool windows = rid.StartsWith("win", StringComparison.Ordinal);
        if (windows)
            File.WriteAllText(Path.Combine(runtime, "run.cmd"), "@\"%~dp0dotnet\\dotnet.exe\" \"%~dp0launcher\\wpfrun.dll\" run %*\r\n");
        else
            File.WriteAllText(Path.Combine(runtime, "run"), "#!/bin/sh\n# runs a Windows .NET program of the installation on this OS: run <program.exe> [arguments]\nhere=\"$(cd \"$(dirname \"$0\")\" && pwd)\"\nexec \"$here/dotnet/dotnet\" \"$here/launcher/wpfrun.dll\" run \"$@\"\n");

        string pack = Path.Combine(outDir, $"desktop-{rid}.pack");
        if (File.Exists(pack)) File.Delete(pack);
        using (var zip = ZipFile.Open(pack, ZipArchiveMode.Create))
        {
            var info = new Dictionary<string, object>
            {
                ["kind"] = "desktop",
                ["rid"] = rid,
                ["dotnet"] = version,
                ["version"] = typeof(Pack).Assembly.GetName().Version?.ToString() ?? "0",
                ["built"] = DateTime.UtcNow.ToString("O"),
            };
            using (var w = new StreamWriter(zip.CreateEntry("pack.json").Open()))
                w.Write(JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true }));
            foreach (string f in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(staging, f).Replace('\\', '/');
                var entry = zip.CreateEntryFromFile(f, rel, CompressionLevel.Optimal);
                // the permissions a program needs to start (dotnet, run), kept for the OS that reads them
                if (!windows) entry.ExternalAttributes = (int)(Executable(f, rel) ? 0x81ED : 0x81A4) << 16;
            }
        }
        Console.WriteLine($"wpfrun pack: {pack}: .NET {version} for {rid}, wpfrun from {build} ({new FileInfo(pack).Length / (1024 * 1024)} MB)");
        return 0;
    }

    private static bool Executable(string file, string rel)
    {
        if (rel is "runtime/run" or "runtime/dotnet/dotnet") return true;
        if (OperatingSystem.IsWindows()) return false;
        return (File.GetUnixFileMode(file) & UnixFileMode.UserExecute) != 0;
    }

    /// <summary>Microsoft's ASP.NET Core runtime (which holds .NET's own) for <paramref name="rid"/>, the latest
    /// .NET 10, downloaded once and checked against the release's SHA-512.</summary>
    private static string DotnetRuntime(string rid, out string version)
    {
        using var http = new System.Net.Http.HttpClient();
        using var doc = JsonDocument.Parse(http.GetStringAsync("https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json").GetAwaiter().GetResult());
        var release = doc.RootElement.GetProperty("releases")[0];
        var aspnet = release.GetProperty("aspnetcore-runtime");
        version = aspnet.GetProperty("version").GetString()!;
        string wanted = "aspnetcore-runtime-" + rid + (rid.StartsWith("win", StringComparison.Ordinal) ? ".zip" : ".tar.gz");
        JsonElement? file = null;
        foreach (var f in aspnet.GetProperty("files").EnumerateArray())
            if (f.GetProperty("name").GetString() == wanted) file = f;
        if (file == null) throw new UserError($"no {wanted} in .NET {version}'s release");
        string url = file.Value.GetProperty("url").GetString()!, hash = file.Value.GetProperty("hash").GetString()!;
        string path = Path.Combine(Locations.CacheRoot, "dotnet-runtimes", Path.GetFileName(new Uri(url).LocalPath));
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            Console.WriteLine($"wpfrun pack: fetching {url}");
            byte[] bytes = http.GetByteArrayAsync(url).GetAwaiter().GetResult();
            string sha = Convert.ToHexString(System.Security.Cryptography.SHA512.HashData(bytes));
            if (!sha.Equals(hash, StringComparison.OrdinalIgnoreCase)) throw new UserError($"{url} is not the file the release lists (sha512 {sha})");
            File.WriteAllBytes(path, bytes);
        }
        return path;
    }

    private static void Extract(string archive, string to)
    {
        if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) { ZipFile.ExtractToDirectory(archive, to); return; }
        using var gz = new GZipStream(File.OpenRead(archive), CompressionMode.Decompress);
        System.Formats.Tar.TarFile.ExtractToDirectory(gz, to, overwriteFiles: true);
    }

    private static void CopyTree(string from, string to)
    {
        foreach (string f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            string dest = Path.Combine(to, Path.GetRelativePath(from, f));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(f, dest, overwrite: true);
        }
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
