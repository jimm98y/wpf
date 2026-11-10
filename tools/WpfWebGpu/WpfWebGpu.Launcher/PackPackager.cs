// wpfrun pack --packager <rid>[,...]: the Packager as it is given to its users, per OS, nothing to install -- the
// stack's desktop runtime (desktop-<rid>.pack's runtime/: .NET, wpfrun), the Packager app (WpfWebGpu.Packager.App,
// an ordinary WPF app for Windows, run by that runtime on every OS as an installation's programs are), the packs
// folder (every template beside it: what it fills for each target) and its launcher:
//
//   macOS     Packager.app            Contents/MacOS/Packager (a script: runtime/run app/Packager.exe),
//                                     Contents/Resources/{runtime,app,packs}
//   Linux     Packager-<rid>/packager (the same script), runtime/, app/, packs/
//   Windows   Packager-<rid>\Packager.exe  .NET's apphost made a window program (no console), wpfrun.dll its app and
//                                     runtime\dotnet the .NET it starts (found beside it: the apphost's app-relative
//                                     search), Packager.wpfrun saying what it runs (LauncherStub); runtime\, app\, packs\
//
// Written beside the packs folder (<packs>/../Packager-<rid>/). The app is published from this repository's source.

using System.Reflection;
using System.Text.Json;

namespace WpfWebGpu;

internal static class PackPackager
{
    public static int Make(string packsDir, string rid, bool verbose)
    {
        string desktopPack = Path.Combine(packsDir, $"desktop-{rid}.pack");
        if (!File.Exists(desktopPack)) throw new UserError($"no {desktopPack}: make it first (wpfrun pack --desktop {rid})");
        var desktop = WpfWebGpu.Packager.PackFile.Open(packsDir, "desktop-" + rid);

        string project = FindProject() ?? throw new UserError("the Packager app's project (tools/WpfWebGpu/WpfWebGpu.Packager.App) is not found from here");
        string published = Path.Combine(Locations.CacheRoot, "packs", "packager-app");
        if (Directory.Exists(published)) Directory.Delete(published, recursive: true);
        Console.WriteLine("wpfrun pack: publishing the Packager app");
        int code = Bundle.DotnetForPack(verbose, "publish", project, "-c", "Release", "-o", published, "-nologo");
        if (code != 0) return code;

        bool windows = rid.StartsWith("win", StringComparison.Ordinal), mac = rid.StartsWith("osx", StringComparison.Ordinal);
        string output = Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(packsDir))!, mac ? "Packager-" + rid : "Packager-" + rid);
        if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
        string bundle = Path.Combine(output, "Packager.app");
        string root = mac ? Path.Combine(bundle, "Contents", "Resources") : output;

        CopyTree(Path.Combine(desktop.Directory, "runtime"), Path.Combine(root, "runtime"));
        CopyTree(published, Path.Combine(root, "app"));
        int packs = 0;
        foreach (string pack in Directory.EnumerateFiles(packsDir, "*.pack"))
        {
            Directory.CreateDirectory(Path.Combine(root, "packs"));
            File.Copy(pack, Path.Combine(root, "packs", Path.GetFileName(pack)), overwrite: true);
            packs++;
        }

        const string script = """
            #!/bin/sh
            # the Packager: the stack's runtime runs it, the templates beside it
            here="$(cd "$(dirname "$0")/{0}" && pwd)"
            export WPFPKG_PACKS="$here/packs"
            exec "$here/runtime/run" "$here/app/Packager.exe" "$@"

            """;
        if (mac)
        {
            Directory.CreateDirectory(Path.Combine(bundle, "Contents", "MacOS"));
            string launcher = Path.Combine(bundle, "Contents", "MacOS", "Packager");
            File.WriteAllText(launcher, script.Replace("{0}", "../Resources"));
            File.SetUnixFileMode(launcher, (UnixFileMode)0b111_101_101);
            File.WriteAllText(Path.Combine(bundle, "Contents", "Info.plist"), $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                <plist version="1.0">
                <dict>
                  <key>CFBundleExecutable</key><string>Packager</string>
                  <key>CFBundleIdentifier</key><string>org.wpfwebgpu.packager</string>
                  <key>CFBundleName</key><string>Packager</string>
                  <key>CFBundleDisplayName</key><string>Packager</string>
                  <key>CFBundlePackageType</key><string>APPL</string>
                  <key>CFBundleShortVersionString</key><string>{desktop["version"]}</string>
                  <key>LSMinimumSystemVersion</key><string>13.0</string>
                  <key>NSHighResolutionCapable</key><true/>
                </dict>
                </plist>
                """);
        }
        else if (!windows)
        {
            string launcher = Path.Combine(root, "packager");
            File.WriteAllText(launcher, script.Replace("{0}", "."));
            File.SetUnixFileMode(launcher, (UnixFileMode)0b111_101_101);
        }
        else
        {
            WindowsLauncher(rid, desktop["dotnet"], Path.Combine(root, "Packager.exe"));
            File.WriteAllText(Path.Combine(root, "Packager.wpfrun"),
                "# what Packager.exe runs (LauncherStub): wpfrun, given these before its own arguments\r\nrun\r\n@app\\Packager.exe\r\nset WPFPKG_PACKS=@packs\r\n");
        }
        Console.WriteLine($"wpfrun pack: {(mac ? bundle : output)} (the runtime, the app, {packs} packs)");
        return 0;
    }

    // .NET's apphost for Windows, pointed at runtime\launcher\wpfrun.dll and at runtime\dotnet for the .NET it starts,
    // made a window program: written with the SDK's HostWriter (Microsoft.NET.HostModel), loaded from the SDK this runs on
    private static void WindowsLauncher(string rid, string dotnetVersion, string destination)
    {
        string template = Pack.AppHostFor(rid, dotnetVersion);
        string sdkRoot = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath!)!, "sdk");
        string? hostModel = Directory.Exists(sdkRoot)
            ? Directory.EnumerateDirectories(sdkRoot).OrderByDescending(d => d, StringComparer.Ordinal).Select(d => Path.Combine(d, "Microsoft.NET.HostModel.dll")).FirstOrDefault(File.Exists)
            : null;
        if (hostModel == null) throw new UserError($"the .NET SDK's Microsoft.NET.HostModel.dll is not found under {sdkRoot}: run this with the SDK's dotnet");
        Assembly model = Assembly.LoadFrom(hostModel);
        Type writer = model.GetType("Microsoft.NET.HostModel.AppHost.HostWriter", throwOnError: true)!;
        Type optionsType = writer.GetNestedType("DotNetSearchOptions") ?? throw new UserError("this SDK's HostWriter has no DotNetSearchOptions (.NET 9 or later is needed)");
        object options = Activator.CreateInstance(optionsType)!;
        Type location = optionsType.GetNestedType("SearchLocation")!;
        optionsType.GetProperty("Location")!.SetValue(options, Enum.Parse(location, "AppRelative"));
        optionsType.GetProperty("AppRelativeDotNet")!.SetValue(options, @"runtime\dotnet");
        MethodInfo create = writer.GetMethod("CreateAppHost", BindingFlags.Public | BindingFlags.Static)!;
        object?[] arguments = create.GetParameters().Select(p => p.Name switch
        {
            "appHostSourceFilePath" => template,
            "appHostDestinationFilePath" => destination,
            "appBinaryFilePath" => @"runtime\launcher\wpfrun.dll",
            "windowsGraphicalUserInterface" => true,
            "dotNetSearchOptions" => options,
            _ => p.HasDefaultValue ? p.DefaultValue : (p.ParameterType == typeof(bool) ? false : null),
        }).ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        create.Invoke(null, arguments);
    }

    private static string? FindProject()
    {
        for (string? dir = AppContext.BaseDirectory; dir != null; dir = Path.GetDirectoryName(dir))
        {
            string candidate = Path.Combine(dir, "tools", "WpfWebGpu", "WpfWebGpu.Packager.App", "WpfWebGpu.Packager.App.csproj");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
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
}
