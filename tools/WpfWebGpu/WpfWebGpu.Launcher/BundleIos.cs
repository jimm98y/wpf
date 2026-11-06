// wpfrun bundle --ios: an unmodified app, from its binaries, as an iOS app (the simulator's, for now).
//
// The browser bundle's way, for the iOS head: the project WpfWebGpu.Sdk's iOS head would build if the
// app had been written for it -- a host that starts UIKit and then calls the app's own Main -- built
// into an .app. The build does for it what it does for any app on a bundled head: the app's Windows
// P/Invokes and COM types rewritten onto the stack (wpf-rewrite), placeholders for what it references
// and does not ship.
//
// - The assemblies and the data: the browser bundle's (Closure, Payload), the data as the bundle's
//   resources at its root, where the app finds them beside itself (AppContext.BaseDirectory).
// - The registry and the machine's data (ProgramData): wpfrun's, carried in the bundle and copied to the
//   app's own Library on first start, where the registry shim and the machine-wide folders
//   (WindowsPaths' GetFolderPath) keep them from then on. Not the historian's databases.
// - Main runs once the scene is connected, on the main thread, and the process ends when it returns,
//   as an app's process does on Windows.
//
// The generated project lives in the cache (bundles/<app>-ios/); the .app goes to --out.

using System.Text;

namespace WpfWebGpu;

internal static partial class Bundle
{
    private const string IosRid = "iossimulator-arm64";

    /// <summary>The assemblies a default (not --aot) bundle compiles ahead of time.</summary>
    private static readonly string[] CompiledStack =
    {
        "System.Private.CoreLib", "WindowsBase", "PresentationCore", "PresentationFramework", "System.Xaml",
        "Microsoft.Wpf.Interop.WebGpu", "System.Windows.Forms", "System.Windows.Forms.Primitives",
        "Mono.System.Drawing", "System.Drawing.Common", "WpfWebGpu.Win32",
    };

    private static int RunIos(AppFiles app, List<string> assemblies, List<string> files, List<string> folders, string feed, string outDir, bool verbose, bool aot, bool visionOs = false)
    {
        // one project per mode, as for the browser (an AOT build's objects are not an interpreted one's)
        string project = Path.Combine(Locations.CacheRoot, "bundles", app.Name + (aot ? "-ios-aot" : "-ios"));
        // From clean: a compiled image built against an assembly that has changed since is rejected at
        // start-up ("a dependency cannot be found or it is out of date") and the app aborts -- and the
        // build reused them when only some had changed.
        foreach (string stale in new[] { "obj", "bin" })
            if (Directory.Exists(Path.Combine(project, stale))) Directory.Delete(Path.Combine(project, stale), recursive: true);
        Directory.CreateDirectory(project);
        WriteIosProject(project, app, assemblies, files, folders, feed, aot);

        string csproj = Path.Combine(project, "Bundle.csproj");
        Console.WriteLine($"wpfrun bundle: building {project}");
        int code = Dotnet(verbose, "build", csproj, "-c", "Release", "-nologo");
        if (code != 0) return code;

        string built = Path.Combine(project, "bin", "Release", "net10.0-ios", IosRid, "WpfRunBundle.app");
        if (!Directory.Exists(built)) throw new UserError($"the build made no {built}.");
        string target = Path.Combine(outDir, app.Name + ".app");
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        Directory.CreateDirectory(outDir);
        // ditto keeps the bundle's symbolic links and permissions, which a managed copy would not
        using (var p = System.Diagnostics.Process.Start("ditto", new[] { built, target })!)
        {
            p.WaitForExit();
            if (p.ExitCode != 0) return p.ExitCode;
        }
        if (visionOs)
        {
            int retag = RetagForVisionOs(target);
            if (retag != 0) return retag;
        }
        Console.WriteLine($"wpfrun bundle: done: {target}");
        Console.WriteLine($"  xcrun simctl install booted \"{target}\" && xcrun simctl launch --console booted {IosBundleId(app)}");
        return 0;
    }

    private static string IosBundleId(AppFiles app)
    {
        var id = new StringBuilder("com.wpfrun.");
        foreach (char c in app.Name) id.Append(char.IsAsciiLetterOrDigit(c) ? c : '-');
        return id.ToString();
    }

    // The template (wpfrun pack --ios): the host with no app, which the Packager fills (the app's folder under app/,
    // its assemblies loaded from there, its name, id and icon in Info.plist) without a build.
    internal const string IosTemplateId = "com.wpfrun.template";
    internal const string IosTemplateLabel = "WpfRunTemplateLabel";

    internal static void WriteIosTemplateProject(string dir, string feed, string rid)
    {
        File.WriteAllText(Path.Combine(dir, "wpfrun-app.txt"), "name=\nexe=\nfolder=app\n");
        string interpreter = "all," + string.Join(",", CompiledStack.Select(a => "-" + a));
        WriteIosProjectCore(dir, "as the Packager's template", IosTemplateId, IosTemplateLabel, rid, "", "", interpreter, feed,
            template: true);
    }

    private static void WriteIosProject(string dir, AppFiles app, List<string> assemblies, List<string> files, List<string> folders, string feed, bool aot)
    {
        // What runs compiled. By default the core library and the stack -- what every frame runs (layout, the
        // renderer, the drivers) -- and everything else interpreted: Workbench came up in 10 s against 19 s
        // all interpreted, built in the same three minutes. --aot: the rest of the runtime's libraries too,
        // the app's own assemblies still interpreted (11 s, and twice the build).
        string interpreter = aot
            ? "-all," + string.Join(",", assemblies.Select(a => Path.GetFileNameWithoutExtension(a)))
            : "all," + string.Join(",", CompiledStack.Select(a => "-" + a));
        var refs = new StringBuilder();
        foreach (string a in assemblies)
            refs.Append($"    <Reference Include=\"{Xml(Path.GetFileNameWithoutExtension(a))}\"><HintPath>{Xml(a)}</HintPath><Private>true</Private></Reference>\n");
        var data = new StringBuilder();
        foreach (string f in files)
            data.Append($"    <BundleResource Include=\"{Xml(Path.Combine(app.Directory, f))}\" Link=\"{Xml(f)}\" />\n");
        foreach (string d in folders)
            data.Append($"    <BundleResource Include=\"{Xml(Path.Combine(app.Directory, d))}/**\" Link=\"{Xml(d)}/%(RecursiveDir)%(Filename)%(Extension)\" />\n");
        string registry = Path.Combine(Locations.StateRoot, "registry");
        if (Directory.Exists(registry))
            data.Append($"    <BundleResource Include=\"{Xml(registry)}/*.reg\" Link=\"wpfrun-registry/%(Filename)%(Extension)\" />\n");
        // the machine's data the installation wrote (ProgramData), but its historian's databases: what the
        // app recorded as it ran, not what it needs to
        string programData = Path.Combine(Locations.StateRoot, "ProgramData");
        if (Directory.Exists(programData))
            data.Append($"    <BundleResource Include=\"{Xml(programData)}/**\" Exclude=\"{Xml(programData)}/**/DataHistorian/**\" Link=\"wpfrun-state/ProgramData/%(RecursiveDir)%(Filename)%(Extension)\" />\n");

        string appPath = File.Exists(app.Exe) ? app.Name + ".exe" : app.Name + ".dll";
        File.WriteAllText(Path.Combine(dir, "wpfrun-app.txt"), $"name={app.Name}\nexe={appPath}\nfolder=\n");
        WriteIosProjectCore(dir, $"for {app.Dll}", IosBundleId(app), app.Name, IosRid, refs.ToString(), data.ToString(), interpreter, feed, template: false);
    }

    private static void WriteIosProjectCore(string dir, string what, string id, string title, string rid, string refs, string data, string interpreter, string feed, bool template)
    {
        File.WriteAllText(Path.Combine(dir, "Bundle.csproj"), $"""
            <!-- Generated by wpfrun bundle (iOS) {Xml(what)}; regenerated on every run. -->
            <Project Sdk="WpfWebGpu.Sdk/0.1.1">

              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0-ios</TargetFramework>
                <RuntimeIdentifier>{rid}</RuntimeIdentifier>
                <!-- the wgpu-native the SDK links is built for 14.0 -->
                <SupportedOSPlatformVersion>14.0</SupportedOSPlatformVersion>
                <UseWPF>true</UseWPF>
                <UseWindowsForms>true</UseWindowsForms>
                <AssemblyName>WpfRunBundle</AssemblyName>
                <ApplicationId>{id}</ApplicationId>
                <ApplicationTitle>{Xml(title)}</ApplicationTitle>{(template && !rid.Contains("simulator") ? """

                <!-- the template for devices: signed by the Packager, with the app's certificate and profile -->
                <EnableCodeSigning>false</EnableCodeSigning>
            """ : "")}
                <Nullable>disable</Nullable>
                <ImplicitUsings>disable</ImplicitUsings>
                <EnableNETAnalyzers>false</EnableNETAnalyzers>
                <AnalysisLevel>none</AnalysisLevel>
                <EnforceCodeStyleInBuild>false</EnforceCodeStyleInBuild>
                <!-- nothing trimmed: the app reaches its parts by name, as on Windows -->
                <MtouchLink>None</MtouchLink>
                <!-- interpreted, as a non-AOT browser bundle is: compiling 300-odd assemblies ahead of time
                     (and through LLVM, which a Release build does) takes the better part of an hour; for an
                     AOT bundle, only the app's own assemblies -->
                <MtouchInterpreter>{interpreter}</MtouchInterpreter>
                <MtouchUseLlvm>false</MtouchUseLlvm>
                <!-- the app's binaries were built against .NET's WindowsDesktop assemblies, which the stack replaces -->
                <MSBuildWarningsAsMessages>$(MSBuildWarningsAsMessages);MSB3277;MSB3243;MSB3245</MSBuildWarningsAsMessages>
                <NoWarn>$(NoWarn);CA1416;CS0618</NoWarn>
              </PropertyGroup>

              <ItemGroup>
            {refs}  </ItemGroup>

              <!-- the app's data, at the bundle's root beside the assemblies (AppContext.BaseDirectory) -->
              <ItemGroup>
                <BundleResource Include="wpfrun-app.txt" Link="wpfrun-app.txt" />
            {data}  </ItemGroup>

            </Project>
            """);
        File.WriteAllText(Path.Combine(dir, "Info.plist"), """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>LSRequiresIPhoneOS</key>
                <true/>
                <key>UIDeviceFamily</key>
                <array>
                    <integer>1</integer>
                    <integer>2</integer>
                </array>
                <key>UISupportedInterfaceOrientations</key>
                <array>
                    <string>UIInterfaceOrientationPortrait</string>
                    <string>UIInterfaceOrientationLandscapeLeft</string>
                    <string>UIInterfaceOrientationLandscapeRight</string>
                </array>
                <key>UISupportedInterfaceOrientations~ipad</key>
                <array>
                    <string>UIInterfaceOrientationPortrait</string>
                    <string>UIInterfaceOrientationPortraitUpsideDown</string>
                    <string>UIInterfaceOrientationLandscapeLeft</string>
                    <string>UIInterfaceOrientationLandscapeRight</string>
                </array>
                <key>UILaunchScreen</key>
                <dict/>
                <key>UIApplicationSceneManifest</key>
                <dict>
                    <key>UIApplicationSupportsMultipleScenes</key>
                    <false/>
                    <key>UISceneConfigurations</key>
                    <dict>
                        <key>UIWindowSceneSessionRoleApplication</key>
                        <array>
                            <dict>
                                <key>UISceneConfigurationName</key>
                                <string>Default Configuration</string>
                                <key>UISceneDelegateClassName</key>
                                <string>WpfRunSceneDelegate</string>
                            </dict>
                        </array>
                    </dict>
                </dict>
            </dict>
            </plist>
            """);
        File.WriteAllText(Path.Combine(dir, "Program.cs"), $$"""
            // Generated by wpfrun bundle --ios: UIKit first, then the app's own Main.
            using System;
            using System.IO;
            using System.Reflection;
            using Foundation;
            using UIKit;

            internal static class WpfRunBundleHost
            {
                private static void Main(string[] args)
                {
                    // the app this bundle starts and the folder it is in (wpfrun-app.txt; a template's app is in app/):
                    // the folder is the app's own, as on Windows (AppContext.BaseDirectory), before anything reads it
                    string bundle = AppContext.BaseDirectory;
                    foreach (string line in File.ReadAllLines(Path.Combine(bundle, "wpfrun-app.txt")))
                    {
                        int eq = line.IndexOf('=');
                        if (eq > 0) Info[line[..eq]] = line[(eq + 1)..];
                    }
                    if (Info["name"].Length == 0) throw new InvalidOperationException("this bundle holds no app (a template the Packager has not filled)");
                    if (Info["folder"].Length > 0) AppContext.SetData("APP_CONTEXT_BASE_DIRECTORY", Path.Combine(bundle, Info["folder"]) + "/");
                    Bundle = bundle;
                    // the app's own command line: a launch passes no arguments to an iOS app, but an environment
                    // (WPFRUN_ARGS, '|' between arguments; SIMCTL_CHILD_WPFRUN_ARGS on a simulator launch)
                    if (Environment.GetEnvironmentVariable("WPFRUN_ARGS") is { Length: > 0 } passed)
                        Args = passed.Split('|');
                    UIApplication.Main(args, null, typeof(WpfRunAppDelegate));
                }

                internal static string[] Args = Array.Empty<string>();
                internal static readonly System.Collections.Generic.Dictionary<string, string> Info = new() { ["name"] = "", ["exe"] = "", ["folder"] = "" };
                internal static string Bundle = "";

                /// <summary>The app's Main, on the main thread, once the scene has its window; the process
                /// ends when it returns, as the app's own does on Windows.</summary>
                internal static void RunApp()
                {
                    // WPF_WEBGPU_LOG_FIRST_CHANCE=1 (SIMCTL_CHILD_ on a launch): every exception as it is thrown,
                    // the browser boot's diagnostic -- what an app catches and shows only the message of
                    if (Environment.GetEnvironmentVariable("WPF_WEBGPU_LOG_FIRST_CHANCE") is "1")
                        AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
                        {
                            // the stack being thrown on: the exception's own is not filled in yet
                            var frames = new System.Diagnostics.StackTrace(1, false).GetFrames();
                            var at = new System.Text.StringBuilder();
                            for (int i = 0; i < frames.Length && i < 14; i++)
                                if (frames[i].GetMethod() is { } m) at.Append("\n   at ").Append(m.DeclaringType?.FullName).Append('.').Append(m.Name);
                            string file = e.Exception is FileNotFoundException f ? " [" + f.FileName + "]" : "";
                            Console.WriteLine("first chance: " + e.Exception.GetType().FullName + ": " + e.Exception.Message + file + at);
                        };
                    string root = AppContext.BaseDirectory;
                    string name = Info["name"];
                    SeedRegistry(Bundle, root);
                    SeedState(Bundle);
                    // the app's assemblies the bundle was not built with (a template's app: all of them) are its folder's,
                    // as on Windows: there, and a culture's satellites in the culture's folder
                    System.Runtime.Loader.AssemblyLoadContext.Default.Resolving += (context, asked) =>
                    {
                        string culture = string.IsNullOrEmpty(asked.CultureName) || asked.CultureName == "neutral" ? "" : asked.CultureName;
                        string file = Path.Combine(root, culture, asked.Name + ".dll");
                        return File.Exists(file) ? context.LoadFromAssemblyPath(file) : null;
                    };
                    // the app's configuration, not this host's: what ConfigurationManager reads
                    AppContext.SetData("APP_CONFIG_FILE", Path.Combine(root, name + ".dll.config"));
                    // the file the app is started from, first on its command line as on Windows
                    AppContext.SetData("WPFWEBGPU_APP_PATH", Path.Combine(root, Info["exe"]));
                    Environment.SetEnvironmentVariable("WPF_USE_WEBGPU_COMPOSITION", "1");
                    InitializeWin32();
                    Assembly app = Assembly.Load(new AssemblyName(name));
                    // the app is the process's entry assembly, as when its own .exe starts it
                    Assembly.SetEntryAssembly(app);
                    MethodInfo entry = app.EntryPoint ?? throw new InvalidOperationException(name + " has no entry point");
                    int code = 0;
                    try
                    {
                        object result = entry.Invoke(null, BindingFlags.DoNotWrapExceptions, null,
                            entry.GetParameters().Length == 1 ? new object[] { Args } : null, null);
                        if (result is int c) code = c;
                    }
                    catch (Exception e)
                    {
                        Console.Error.WriteLine(name + ": " + e);
                        code = e.HResult;
                    }
                    Console.WriteLine(name + ": Main returned " + code);
                    Environment.Exit(code);
                }

                /// <summary>The stack's Windows API the app's P/Invokes were rewritten onto at build
                /// (WpfWebGpu.Win32): set up before the app's first call, as the browser's boot and the
                /// launcher's bootstrap do. Its once-per-function notes go to the console.</summary>
                private static void InitializeWin32()
                {
                    Assembly win32;
                    try { win32 = Assembly.Load(new AssemblyName("WpfWebGpu.Win32")); }
                    catch (FileNotFoundException) { return; }   // nothing was rewritten
                    var seen = new System.Collections.Generic.HashSet<string>();
                    Action<string, string> logOnce = (key, message) => { lock (seen) if (seen.Add(key)) Console.WriteLine(message); };
                    win32.GetType("WpfWebGpu.Win32.Exports")?.GetMethod("Initialize", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, new object[] { logOnce });
                }

                /// <summary>The machine's folders (ProgramData), the bundle's copy put once into the app's Library:
                /// where the app's machine-wide folders are (WPFWEBGPU_STATE_DIR).</summary>
                private static void SeedState(string root)
                {
                    string state = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "wpf-webgpu", "state");
                    AppContext.SetData("WPFWEBGPU_STATE_DIR", state);
                    string seed = Path.Combine(root, "wpfrun-state");
                    string done = Path.Combine(state, ".seeded");
                    if (!Directory.Exists(seed) || File.Exists(done)) return;
                    foreach (string file in Directory.EnumerateFiles(seed, "*", SearchOption.AllDirectories))
                    {
                        string to = Path.Combine(state, Path.GetRelativePath(seed, file));
                        if (File.Exists(to)) continue;
                        Directory.CreateDirectory(Path.GetDirectoryName(to));
                        File.Copy(file, to);
                    }
                    File.WriteAllText(done, "");
                }

                /// <summary>The registry the bundle carries (wpfrun's), copied once into the app's Library,
                /// where the registry shim reads and writes it from then on.</summary>
                private static void SeedRegistry(string root, string appFolder)
                {
                    string store = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "wpf-webgpu", "registry");
                    Environment.SetEnvironmentVariable("WPFWEBGPU_REGISTRY_DIR", store);
                    string seed = Path.Combine(root, "wpfrun-registry");
                    if (!Directory.Exists(seed)) return;
                    Directory.CreateDirectory(store);
                    foreach (string hive in Directory.EnumerateFiles(seed, "*.reg"))
                    {
                        string to = Path.Combine(store, Path.GetFileName(hive));
                        if (File.Exists(to)) continue;
                        // the installation's paths, as the Packager left them (PathTokens): where they are here
                        string state = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "wpf-webgpu", "state");
                        File.WriteAllText(to, File.ReadAllText(hive)
                            .Replace("${WPFRUN_APPDIR}", appFolder.TrimEnd('/'))
                            .Replace("${WPFRUN_PROGRAMDATA}", Path.Combine(state, "ProgramData"))
                            .Replace("${WPFRUN_INSTALLDIR}", Path.Combine(root, "install")));
                    }
                }
            }

            [Register("WpfRunAppDelegate")]
            internal sealed class WpfRunAppDelegate : UIApplicationDelegate
            {
                public override bool FinishedLaunching(UIApplication application, NSDictionary launchOptions) => true;

                // the window's scene is the app's; another (visionOS's immersive space, a SwiftUI scene) keeps its own
                public override UISceneConfiguration GetConfiguration(UIApplication application, UISceneSession connectingSceneSession, UISceneConnectionOptions options)
                    => connectingSceneSession.Role == UIWindowSceneSessionRole.Application
                        ? new UISceneConfiguration("Default Configuration", connectingSceneSession.Role)
                        : connectingSceneSession.Configuration;
            }

            /// <summary>The screen the app's windows are on: all of it, as a desktop is -- no status bar over
            /// the windows' tops, and the home indicator out of the way until the screen's edge is touched.</summary>
            internal sealed class WpfRunScreenController : UIViewController
            {
                public override bool PrefersStatusBarHidden() => true;
                public override bool PrefersHomeIndicatorAutoHidden => true;
            }

            [Register("WpfRunSceneDelegate")]
            internal sealed class WpfRunSceneDelegate : UIResponder, IUIWindowSceneDelegate
            {
                private static bool s_started;

                [Export("window")]
                public UIWindow Window { get; set; }

                [Export("scene:willConnectToSession:options:")]
                public void WillConnect(UIScene scene, UISceneSession session, UISceneConnectionOptions connectionOptions)
                {
                    if (scene is not UIWindowScene windowScene) return;
                    Window ??= new UIWindow(windowScene);
                    var vc = new WpfRunScreenController();
                    Window.RootViewController = vc;
                    Window.MakeKeyAndVisible();
                    // the screen the app's windows are views of
                    MS.Internal.Interop.UIKitWindow.RootView = vc.View!.Handle;
                    if (s_started) return;
                    s_started = true;
                    // after the scene has connected: the app's Main does not return until it ends
                    NSRunLoop.Main.BeginInvokeOnMainThread(WpfRunBundleHost.RunApp);
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
        File.WriteAllText(Path.Combine(dir, "Directory.Build.props"), "<Project />\n");
        File.WriteAllText(Path.Combine(dir, "Directory.Build.targets"), "<Project />\n");
    }
}
