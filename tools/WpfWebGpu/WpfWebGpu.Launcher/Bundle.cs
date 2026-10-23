// wpfrun bundle: an unmodified app, from its binaries, as a page for the browser head.
//
// A browser cannot run an app from a folder as wpfrun runs it on a desktop: everything it runs is a
// bundle of assemblies and data built ahead of time. So this makes the project WpfWebGpu.Sdk's browser
// head would build if the app had been written for it -- a host whose only code calls the app's own
// Main -- and publishes it. The publish then does what it does for any app on that head: the app's
// Windows P/Invokes and COM types rewritten onto the stack (wpf-rewrite), trimming, the bundle, the page.
//
// - The assemblies: the app's, the ones its entry assembly reaches by reference, from its folder. Not
//   the stack's own (the overlay has them; the SDK brings its own), and not mixed-mode ones, which no
//   browser can load.
// - The data: the app folder's own files and folders (configuration, displays, images), mounted at the
//   page's file system root where the app finds them beside itself (AppContext.BaseDirectory is "/").
//   Not native libraries (runtimes/, and any folder holding only native code), and not satellite
//   assemblies' culture folders yet.
// - The app reads its own configuration (App.dll.config), not the host's: APP_CONFIG_FILE.
//
// The generated project lives in the cache (bundles/<app>/); the page goes to --out.

using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using WpfWebGpu.ComRewrite;

namespace WpfWebGpu;

internal static class Bundle
{
    /// <summary>Folders that are a native runtime's, never the app's data.</summary>
    private static readonly HashSet<string> s_nativeFolders = new(StringComparer.OrdinalIgnoreCase) { "runtimes", "x86", "x64", "arm64", "amd64" };

    public static int Run(string[] args)
    {
        string? target = null, outDir = null, feed = null;
        bool aot = false, verbose = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--out" or "-o": outDir = args[++i]; break;
                case "--feed": feed = args[++i]; break;
                case "--aot": aot = true; break;
                case "-v" or "--verbose": verbose = true; break;
                default:
                    if (args[i].StartsWith('-')) throw new UserError($"unknown option '{args[i]}'.");
                    target = args[i];
                    break;
            }
        }
        if (target == null) throw new UserError("which app? Pass its .dll, .exe or folder.");
        AppFiles app = AppFiles.Resolve(target);
        outDir = Path.GetFullPath(outDir ?? Path.Combine(Environment.CurrentDirectory, app.Name + "-browser"));
        feed = Path.GetFullPath(feed ?? FindFeed() ?? throw new UserError("no package feed with WpfWebGpu.Sdk found beside wpfrun; pass --feed <folder>."));

        var stack = StackAssemblies(feed);

        var (assemblies, left) = Closure(app, stack);
        Console.WriteLine($"wpfrun bundle: {app.Name}: {assemblies.Count} app assemblies" + (left.Count > 0 ? $" ({left.Count} left out: {string.Join(", ", left.Take(8))}{(left.Count > 8 ? ", ..." : "")})" : ""));
        var (files, folders) = Payload(app);
        Console.WriteLine($"wpfrun bundle: data: {files.Count} files and {folders.Count} folders ({string.Join(", ", folders)})");

        string project = Path.Combine(Locations.CacheRoot, "bundles", app.Name);
        Directory.CreateDirectory(project);
        WriteProject(project, app, assemblies, files, folders, feed, aot);

        string csproj = Path.Combine(project, "Bundle.csproj");
        // The host page first (WpfWebGpu.Sdk scaffolds it into wwwroot/): the web SDK takes the page's
        // files when the project is read, so a page made during the same publish is left unprocessed.
        // The scaffold leaves an index.html and main.js that exist alone (an app's own are its to edit),
        // but a bundle's page is wpfrun's: taken afresh each time, so it is the SDK's current one.
        foreach (string page in new[] { "index.html", "main.js" })
            if (File.Exists(Path.Combine(project, "wwwroot", page)))
                File.Delete(Path.Combine(project, "wwwroot", page));
        int scaffold = Dotnet(verbose, "msbuild", csproj, "-restore", "-t:WpfWebGpuScaffoldWeb", "-p:WpfPlatform=browser", "-p:Configuration=Release", "-nologo");
        if (scaffold != 0) return scaffold;
        Console.WriteLine($"wpfrun bundle: publishing {project} -> {outDir}");
        int code = Dotnet(verbose, "publish", csproj, "-c", "Release", "-p:WpfPlatform=browser", "-o", outDir, "-nologo");
        if (code != 0) return code;
        Console.WriteLine($"wpfrun bundle: done. wpfrun serve {outDir}, then open the address it prints.");
        return 0;
    }

    private static int Dotnet(bool verbose, params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet") { UseShellExecute = false };
        foreach (string a in args) psi.ArgumentList.Add(a);
        if (!verbose) psi.ArgumentList.Add("-v:q");
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        return p.ExitCode;
    }

    /// <summary>The feed WpfWebGpu.Sdk was packed into: artifacts/local-feed, beside artifacts/wpfrun/&lt;rid&gt;/launcher.</summary>
    private static string? FindFeed()
    {
        for (string? d = AppContext.BaseDirectory; d != null; d = Path.GetDirectoryName(d.TrimEnd(Path.DirectorySeparatorChar)))
        {
            string candidate = Path.Combine(d, "local-feed");
            if (Directory.Exists(candidate) && Directory.EnumerateFiles(candidate, "WpfWebGpu.Sdk.*.nupkg").Any()) return candidate;
        }
        return null;
    }

    /// <summary>The assemblies the SDK brings itself (its lib/ folders, in the package in <paramref name="feed"/>):
    /// an app's own copy of one would collide with the stack's.</summary>
    private static HashSet<string> StackAssemblies(string feed)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? package = Directory.EnumerateFiles(feed, "WpfWebGpu.Sdk.*.nupkg").OrderBy(f => f, StringComparer.Ordinal).LastOrDefault();
        if (package == null) return names;
        using var zip = System.IO.Compression.ZipFile.OpenRead(package);
        foreach (var e in zip.Entries)
            if (e.FullName.StartsWith("lib/", StringComparison.Ordinal) && e.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                names.Add(Path.GetFileNameWithoutExtension(e.Name));
        return names;
    }

    /// <summary>The app's assemblies its entry assembly reaches by reference, in its folder; and the ones left
    /// out (mixed-mode, or the stack's). What it references and does not ship gets a placeholder at publish
    /// (wpf-rewrite, Placeholders).</summary>
    private static (List<string> Files, List<string> Left) Closure(AppFiles app, HashSet<string> stack)
    {
        var files = new List<string>();
        var left = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();
        queue.Enqueue(app.Name);
        while (queue.Count > 0)
        {
            string name = queue.Dequeue();
            if (!seen.Add(name)) continue;
            string file = Path.Combine(app.Directory, name + ".dll");
            if (!File.Exists(file)) continue;                   // the framework's, or not shipped
            if (name != app.Name && stack.Contains(name)) { left.Add(name + " (the stack's)"); continue; }
            if (AssemblyScan.Read(file) is not { } scan) continue;
            if (!scan.ILOnly) { left.Add(name + " (mixed-mode)"); continue; }
            files.Add(file);
            foreach (string r in scan.References) queue.Enqueue(r);
        }
        return (files, left);
    }

    /// <summary>The app folder's data: its loose files that are not code, and its folders that are not a
    /// native runtime's or a culture's (satellites).</summary>
    private static (List<string> Files, List<string> Folders) Payload(AppFiles app)
    {
        var files = new List<string>();
        foreach (string f in Directory.EnumerateFiles(app.Directory))
        {
            string ext = Path.GetExtension(f);
            string name = Path.GetFileName(f);
            if (ext is ".dll" or ".exe" or ".pdb" or ".so" or ".dylib" || name.StartsWith('.')) continue;
            if (name.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase)) continue;
            files.Add(name);
        }
        var folders = new List<string>();
        foreach (string d in Directory.EnumerateDirectories(app.Directory))
        {
            string name = Path.GetFileName(d);
            if (name.StartsWith('.') || s_nativeFolders.Contains(name)) continue;
            var all = Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories).ToList();
            if (all.Count == 0) continue;
            // a culture's satellite assemblies (de/App.resources.dll): not data
            if (all.All(f => f.EndsWith(".resources.dll", StringComparison.OrdinalIgnoreCase))) continue;
            // a native SDK's runtime (its libraries, shaders and resources): nothing a page can use
            bool native = all.Any(f => (Path.GetExtension(f) is ".so" or ".dylib") || (Path.GetExtension(f) == ".dll" && !FileIdentity.IsManaged(f)));
            if (native && !all.Any(FileIdentity.IsManaged)) continue;
            folders.Add(name);
        }
        return (files, folders);
    }

    private static string Xml(string s) => System.Security.SecurityElement.Escape(s)!;

    private static void WriteProject(string dir, AppFiles app, List<string> assemblies, List<string> files, List<string> folders, string feed, bool aot)
    {
        var refs = new StringBuilder();
        foreach (string a in assemblies)
            refs.Append($"    <Reference Include=\"{Xml(Path.GetFileNameWithoutExtension(a))}\"><HintPath>{Xml(a)}</HintPath><Private>true</Private></Reference>\n");
        var data = new StringBuilder();
        foreach (string f in files)
            data.Append($"    <None Include=\"{Xml(Path.Combine(app.Directory, f))}\" Link=\"{Xml(f)}\" CopyToOutputDirectory=\"PreserveNewest\" Visible=\"false\" />\n");
        foreach (string d in folders)
            data.Append($"    <None Include=\"{Xml(Path.Combine(app.Directory, d))}/**\" Link=\"{Xml(d)}/%(RecursiveDir)%(Filename)%(Extension)\" CopyToOutputDirectory=\"PreserveNewest\" Visible=\"false\" />\n");
        var payload = new StringBuilder();
        foreach (string f in files) payload.Append($"      <_WpfWebGpuAppPayload Include=\"$(OutDir){Xml(f)}\" />\n");
        foreach (string d in folders) payload.Append($"      <_WpfWebGpuAppPayload Include=\"$(OutDir){Xml(d)}/**/*\" />\n");

        File.WriteAllText(Path.Combine(dir, "Bundle.csproj"), $"""
            <!-- Generated by wpfrun bundle for {Xml(app.Dll)}; regenerated on every run. -->
            <Project Sdk="WpfWebGpu.Sdk/0.1.1">

              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <UseWPF>true</UseWPF>
                <UseWindowsForms>true</UseWindowsForms>
                <AssemblyName>WpfRunBundle</AssemblyName>
                <Nullable>disable</Nullable>
                <ImplicitUsings>disable</ImplicitUsings>
                <EnableNETAnalyzers>false</EnableNETAnalyzers>
                <AnalysisLevel>none</AnalysisLevel>
                <EnforceCodeStyleInBuild>false</EnforceCodeStyleInBuild>
                <WpfWebGpuAot>{(aot ? "true" : "false")}</WpfWebGpuAot>
                <!-- the app's threads are threads (Web Workers): an app starts them, and they may block. The page
                     must then be served cross-origin isolated (COOP/COEP), as wpfrun serve does. -->
                <WasmEnableThreads>true</WasmEnableThreads>
                <!-- the app's binaries were built against .NET's WindowsDesktop assemblies, which the stack replaces -->
                <MSBuildWarningsAsMessages>$(MSBuildWarningsAsMessages);MSB3277;MSB3243;MSB3245</MSBuildWarningsAsMessages>
              </PropertyGroup>

              <ItemGroup>
            {refs}  </ItemGroup>

              <ItemGroup>
            {data}  </ItemGroup>

              <!-- the app's data, mounted at the page's file system root (AppContext.BaseDirectory) -->
              <Target Name="_WpfRunBundlePayload" BeforeTargets="_WpfWebGpuComputeAppPayload">
                <ItemGroup>
            {payload}    </ItemGroup>
              </Target>

            </Project>
            """);
        File.WriteAllText(Path.Combine(dir, "Program.cs"), $$"""
            // Generated by wpfrun bundle: the page's entry point calls the app's own Main.
            using System;
            using System.Reflection;

            internal static class WpfRunBundleHost
            {
                [STAThread]
                private static int Main(string[] args)
                {
                    // the app's configuration, not this host's: what ConfigurationManager reads
                    AppContext.SetData("APP_CONFIG_FILE", "/{{app.Name}}.dll.config");
                    Assembly app = Assembly.Load(new AssemblyName("{{app.Name}}"));
                    // the app is the process's entry assembly, as when its own .exe starts it: an app asks
                    // GetEntryAssembly who it is (GraphWorX picks its colour scheme by its name)
                    Assembly.SetEntryAssembly(app);
                    MethodInfo entry = app.EntryPoint ?? throw new InvalidOperationException("{{app.Name}} has no entry point");
                    object result = entry.Invoke(null, entry.GetParameters().Length == 1 ? new object[] { args } : null);
                    return result is int code ? code : 0;
                }
            }
            """);
        File.WriteAllText(Path.Combine(dir, "nuget.config"), $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="wpfwebgpu" value="{Xml(feed)}" />
                <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
              </packageSources>
            </configuration>
            """);
        // not under the repository's build props (nor any other folder's)
        File.WriteAllText(Path.Combine(dir, "Directory.Build.props"), "<Project />\n");
        File.WriteAllText(Path.Combine(dir, "Directory.Build.targets"), "<Project />\n");
    }
}
