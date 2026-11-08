// wpfrun pack: the Packager's templates (its packs/ folder), built where the toolchains are -- a head built once
// with no app in it, which the Packager fills with an app's files, names and icon without a build.
//
//   android.pack   the Android/Quest host APK (both activities, the OpenXR loader), every assembly kept whole,
//                  and those assemblies (refs/) for the rewrite of an app's onto the stack.
//   browser.pack   the browser head's page with no app in it (site/: the page, the runtime, the stack, nothing
//                  trimmed), and its assemblies (refs/) for the rewrite of an app's onto the stack.
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
        bool android = false, verbose = false, ios = false, browser = false;
        var desktop = new List<string>();
        var setup = new List<string>();
        string? wpfrunBuilds = null;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--out" or "-o": outDir = args[++i]; break;
                case "--feed": feed = args[++i]; break;
                case "--android": android = true; break;
                case "--ios": ios = true; break;
                case "--browser": browser = true; break;
                case "--desktop": desktop.AddRange(args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries)); break;
                case "--wpfrun": wpfrunBuilds = args[++i]; break;
                case "--setup": setup.AddRange(args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries)); break;
                case "-v" or "--verbose": verbose = true; break;
                default: throw new UserError($"unknown option '{args[i]}'.");
            }
        }
        if (!android && !ios && !browser && desktop.Count == 0 && setup.Count == 0) throw new UserError("which template? --android, --ios, --browser, --desktop <rid>[,<rid>...], --setup <win-rid>[,...]");
        outDir = Path.GetFullPath(outDir ?? Path.Combine(Environment.CurrentDirectory, "packs"));
        Directory.CreateDirectory(outDir);
        foreach (string rid in desktop)
        {
            int code = PackDesktop(outDir, rid, Path.GetFullPath(wpfrunBuilds ?? Path.Combine(Bundle.FindFeedForPack() ?? ".", "..", "wpfrun")));
            if (code != 0) return code;
        }
        foreach (string rid in setup)
        {
            int code = PackSetup(outDir, rid, verbose);
            if (code != 0) return code;
        }
        if (!android && !ios && !browser) return 0;
        feed = Path.GetFullPath(feed ?? Bundle.FindFeedForPack() ?? throw new UserError("no package feed with WpfWebGpu.Sdk found beside wpfrun; pass --feed <folder>."));
        if (browser)
        {
            int code = PackBrowser(outDir, feed, verbose);
            if (code != 0 || (!android && !ios)) return code;
        }
        if (ios)
        {
            int code = PackIos(outDir, feed, verbose);
            if (code != 0 || !android) return code;
        }
        return PackAndroid(outDir, feed, verbose);
    }

    // ---- the browser ------------------------------------------------------------------------------------------

    private static int PackBrowser(string outDir, string feed, bool verbose)
    {
        string project = Path.Combine(Locations.CacheRoot, "bundles", "template-browser");
        if (Directory.Exists(project)) Directory.Delete(project, recursive: true);
        Directory.CreateDirectory(project);
        Bundle.WriteBrowserTemplateProject(project, feed);
        string csproj = Path.Combine(project, "Bundle.csproj");
        Console.WriteLine($"wpfrun pack: building the browser template in {project}");
        // the host page first (the SDK scaffolds it into wwwroot/, which the web SDK reads when the project is)
        int code = Bundle.DotnetForPack(verbose, "msbuild", csproj, "-restore", "-t:WpfWebGpuScaffoldWeb", "-p:WpfPlatform=browser", "-p:Configuration=Release", "-nologo");
        if (code != 0) return code;
        string published = Path.Combine(project, "published");
        code = Bundle.DotnetForPack(verbose, "publish", csproj, "-c", "Release", "-p:WpfPlatform=browser", "-o", published, "-nologo");
        if (code != 0) return code;
        string site = Directory.Exists(Path.Combine(published, "wwwroot")) ? Path.Combine(published, "wwwroot") : published;
        string framework = Path.Combine(site, "_framework");
        if (!File.Exists(Path.Combine(site, "index.html")) || !Directory.Exists(framework))
            throw new UserError($"the publish made no page in {site}");

        string pack = Path.Combine(outDir, "browser.pack");
        if (File.Exists(pack)) File.Delete(pack);
        using (var zip = ZipFile.Open(pack, ZipArchiveMode.Create))
        {
            var info = new Dictionary<string, object>
            {
                ["kind"] = "browser",
                ["version"] = typeof(Pack).Assembly.GetName().Version?.ToString() ?? "0",
                ["built"] = DateTime.UtcNow.ToString("O"),
            };
            using (var w = new StreamWriter(zip.CreateEntry("pack.json").Open()))
                w.Write(JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true }));
            // the page, without the precompressed copies (a server compresses what it serves, or not)
            int files = 0;
            foreach (string f in Directory.EnumerateFiles(site, "*", SearchOption.AllDirectories))
            {
                if (f.EndsWith(".gz", StringComparison.Ordinal) || f.EndsWith(".br", StringComparison.Ordinal)) continue;
                zip.CreateEntryFromFile(f, "site/" + Path.GetRelativePath(site, f).Replace('\\', '/'), CompressionLevel.Optimal);
                files++;
            }
            var refs = Directory.EnumerateFiles(framework, "*.dll").Where(f => !f.EndsWith(".resources.dll", StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (string r in refs) zip.CreateEntryFromFile(r, "refs/" + Path.GetFileName(r), CompressionLevel.Optimal);
            Console.WriteLine($"wpfrun pack: {pack}: the page ({files} files) and {refs.Count} assemblies");
        }
        return 0;
    }

    // ---- iOS ----------------------------------------------------------------------------------------------

    private static int PackIos(string outDir, string feed, bool verbose)
    {
        if (!OperatingSystem.IsMacOS()) throw new UserError("the iOS template is built on a Mac (Xcode)");
        var apps = new Dictionary<string, string>();
        foreach (var (kind, rid) in new[] { ("simulator", "iossimulator-arm64"), ("device", "ios-arm64") })
        {
            string project = Path.Combine(Locations.CacheRoot, "bundles", "template-ios-" + kind);
            if (Directory.Exists(project)) Directory.Delete(project, recursive: true);
            Directory.CreateDirectory(project);
            Bundle.WriteIosTemplateProject(project, feed, rid);
            Console.WriteLine($"wpfrun pack: building the iOS template ({kind}) in {project}");
            int code = Bundle.DotnetForPack(verbose, "build", Path.Combine(project, "Bundle.csproj"), "-c", "Release", "-nologo");
            if (code != 0) return code;
            string app = Path.Combine(project, "bin", "Release", "net10.0-ios", rid, "WpfRunBundle.app");
            if (!Directory.Exists(app)) throw new UserError($"the build made no {app}");
            apps[kind] = app;
        }

        string pack = Path.Combine(outDir, "ios.pack");
        if (File.Exists(pack)) File.Delete(pack);
        using (var zip = ZipFile.Open(pack, ZipArchiveMode.Create))
        {
            var info = new Dictionary<string, object>
            {
                ["kind"] = "ios", ["templateId"] = Bundle.IosTemplateId, ["templateLabel"] = Bundle.IosTemplateLabel,
                ["built"] = DateTime.UtcNow.ToString("O"),
            };
            using (var w = new StreamWriter(zip.CreateEntry("pack.json").Open()))
                w.Write(JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true }));
            foreach (var (kind, app) in apps)
                foreach (string f in Directory.EnumerateFiles(app, "*", SearchOption.AllDirectories))
                {
                    string rel = Path.GetRelativePath(app, f).Replace('\\', '/');
                    var entry = zip.CreateEntryFromFile(f, $"{kind}/WpfRunBundle.app/{rel}", CompressionLevel.Optimal);
                    entry.ExternalAttributes = (int)(((File.GetUnixFileMode(f) & UnixFileMode.UserExecute) != 0 ? 0x81ED : 0x81A4) << 16);
                }
            // the assemblies the bundle holds (the device's: the same set), for the rewrite of an app's onto the stack
            foreach (string dll in Directory.EnumerateFiles(apps["device"], "*.dll", SearchOption.AllDirectories)
                .Where(f => !f.EndsWith(".resources.dll", StringComparison.OrdinalIgnoreCase)))
                zip.CreateEntryFromFile(dll, "refs/" + Path.GetFileName(dll), CompressionLevel.Optimal);
        }
        Console.WriteLine($"wpfrun pack: {pack} ({new FileInfo(pack).Length / (1024 * 1024)} MB)");
        return 0;
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
        {
            // dotnetw.exe: the runtime's dotnet.exe as a window program (its PE subsystem GUI), for the Start menu's
            // shortcuts: it finds the runtime beside itself as dotnet.exe does, and opens no console window
            byte[] exe = File.ReadAllBytes(Path.Combine(runtime, "dotnet", "dotnet.exe"));
            int pe = BitConverter.ToInt32(exe, 0x3C);
            if (BitConverter.ToUInt32(exe, pe) != 0x00004550) throw new UserError("dotnet.exe is not a PE file");
            BitConverter.GetBytes((ushort)2 /* IMAGE_SUBSYSTEM_WINDOWS_GUI */).CopyTo(exe, pe + 24 + 68);
            File.WriteAllBytes(Path.Combine(runtime, "dotnet", "dotnetw.exe"), exe);
        }
        // the programs' own executables are .NET's apphost, patched per program where it runs (ProgramHost)
        if (!windows) File.Copy(AppHost(rid, version), Path.Combine(runtime, "launcher", "apphost"), overwrite: true);
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

    // ---- setup ----------------------------------------------------------------------------------------------

    private static int PackSetup(string outDir, string rid, bool verbose)
    {
        string? project = null;
        foreach (string start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
            for (string? d = start; d != null && project == null; d = Path.GetDirectoryName(d.TrimEnd(Path.DirectorySeparatorChar)))
            {
                string candidate = Path.Combine(d, "tools", "WpfWebGpu", "WpfWebGpu.Setup", "WpfWebGpu.Setup.csproj");
                if (File.Exists(candidate)) project = candidate;
            }
        if (project == null) throw new UserError("WpfWebGpu.Setup is not found: run wpfrun pack --setup from the repository");
        string publish = Path.Combine(Locations.CacheRoot, "packs", "setup-" + rid);
        if (Directory.Exists(publish)) Directory.Delete(publish, recursive: true);
        Console.WriteLine($"wpfrun pack: publishing Setup.exe for {rid}");
        int code = Bundle.DotnetForPack(verbose, "publish", project, "-c", "Release", "-r", rid, "-o", publish, "-nologo");
        if (code != 0) return code;
        string exe = Path.Combine(publish, "Setup.exe");
        if (!File.Exists(exe)) throw new UserError($"the publish made no {exe}");
        string pack = Path.Combine(outDir, $"setup-{rid}.pack");
        if (File.Exists(pack)) File.Delete(pack);
        using (var zip = ZipFile.Open(pack, ZipArchiveMode.Create))
        {
            var info = new Dictionary<string, object> { ["kind"] = "setup", ["rid"] = rid, ["built"] = DateTime.UtcNow.ToString("O") };
            using (var w = new StreamWriter(zip.CreateEntry("pack.json").Open()))
                w.Write(JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true }));
            zip.CreateEntryFromFile(exe, "Setup.exe", CompressionLevel.Optimal);
        }
        Console.WriteLine($"wpfrun pack: {pack} ({new FileInfo(exe).Length / (1024 * 1024)} MB Setup.exe)");
        return 0;
    }

    private static bool Executable(string file, string rel)
    {
        if (rel is "runtime/run" or "runtime/dotnet/dotnet" or "runtime/launcher/apphost") return true;
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

    /// <summary>.NET's apphost for <paramref name="rid"/> (Microsoft.NETCore.App.Host, MIT), of the runtime's version,
    /// downloaded once from nuget.org.</summary>
    private static string AppHost(string rid, string version)
    {
        string path = Path.Combine(Locations.CacheRoot, "apphosts", rid + "-" + version, "apphost");
        if (File.Exists(path)) return path;
        string id = "microsoft.netcore.app.host." + rid;
        string url = $"https://api.nuget.org/v3-flatcontainer/{id}/{version}/{id}.{version}.nupkg";
        Console.WriteLine($"wpfrun pack: fetching {url}");
        using var http = new System.Net.Http.HttpClient();
        using var package = new ZipArchive(new MemoryStream(http.GetByteArrayAsync(url).GetAwaiter().GetResult()));
        var entry = package.GetEntry($"runtimes/{rid}/native/apphost") ?? throw new UserError($"{url} holds no apphost for {rid}");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        entry.ExtractToFile(path, overwrite: true);
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
