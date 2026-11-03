// wpfrun bundle --android: an unmodified app, from its binaries, as an Android app (an APK).
//
// The iOS bundle's way, for the Android head: the project WpfWebGpu.Sdk's Android head would build if
// the app had been written for it -- an activity that installs the window layer (AndroidHost) and then
// runs the app's own Main on a UI thread of its own -- built into a signed APK. The build does for it
// what it does on every bundled head: the app's Windows P/Invokes and COM types rewritten onto the
// stack (wpf-rewrite), placeholders for what it references and does not ship.
//
// - The assemblies: the browser bundle's (Closure), embedded in the APK and run by the JIT (no AOT, so a
//   build takes minutes, not an hour).
// - The data: the app's folder (Payload) travels as the APK's assets, with a list of them, and is
//   unpacked on first start into the app's files, which is then the folder the app is started from
//   (AppContext.BaseDirectory, WPFWEBGPU_APP_PATH): an APK has no file path to be beside.
// - The registry and the machine's data (ProgramData): wpfrun's, as on iOS.
// - An Android app inherits no environment from adb, so diagnostics' variables come from a system
//   property: adb shell setprop debug.wpfrun.env "WPF_DEVTOOLS=9333 WF_TRACE_ANDROID=1".
//
// The generated project lives in the cache (bundles/<app>-android/); the APK goes to --out.

using System.Text;

namespace WpfWebGpu;

internal static partial class Bundle
{
    private const string AndroidRid = "android-arm64";

    private static int RunAndroid(AppFiles app, List<string> assemblies, List<string> files, List<string> folders, string feed, string outDir, bool verbose, bool xr = false)
    {
        string project = Path.Combine(Locations.CacheRoot, "bundles", app.Name + "-android");
        Directory.CreateDirectory(project);
        WriteAndroidProject(project, app, assemblies, files, folders, feed, xr ? OpenXrLoader() : null);

        string csproj = Path.Combine(project, "Bundle.csproj");
        Console.WriteLine($"wpfrun bundle: building {project}");
        int code = Dotnet(verbose, "build", csproj, "-c", "Release", "-nologo");
        if (code != 0) return code;

        string id = AndroidPackageId(app);
        string built = Path.Combine(project, "bin", "Release", "net10.0-android", AndroidRid, id + "-Signed.apk");
        if (!File.Exists(built)) throw new UserError($"the build made no {built}.");
        Directory.CreateDirectory(outDir);
        string target = Path.Combine(outDir, app.Name + ".apk");
        File.Copy(built, target, overwrite: true);
        Console.WriteLine($"wpfrun bundle: done: {target}");
        Console.WriteLine($"  adb install -r \"{target}\" && adb shell am start -n {id}/{id}.WpfRunActivity");
        return 0;
    }

    // Khronos's OpenXR loader for Android (Apache-2.0 OR MIT): the native loader and the manifest entries it
    // needs (its permissions, the runtime broker it asks), from Maven Central, once, checked.
    private const string OpenXrLoaderVersion = "1.1.65";
    private const string OpenXrLoaderSha256 = "a298c326085b09181c85235fb04d37510939bd30938ed4322c093c09780e8643";

    private static string OpenXrLoader()
    {
        string path = Path.Combine(Locations.CacheRoot, "openxr", $"openxr_loader_for_android-{OpenXrLoaderVersion}.aar");
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string url = $"https://repo1.maven.org/maven2/org/khronos/openxr/openxr_loader_for_android/{OpenXrLoaderVersion}/openxr_loader_for_android-{OpenXrLoaderVersion}.aar";
            Console.WriteLine($"wpfrun bundle: fetching the OpenXR loader {OpenXrLoaderVersion}");
            using var http = new System.Net.Http.HttpClient();
            byte[] bytes = http.GetByteArrayAsync(url).GetAwaiter().GetResult();
            string sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
            if (sha != OpenXrLoaderSha256) throw new UserError($"the OpenXR loader from {url} is not the one expected (sha256 {sha}).");
            File.WriteAllBytes(path, bytes);
        }
        return path;
    }

    private static string AndroidPackageId(AppFiles app)
    {
        var id = new StringBuilder("com.wpfrun.");
        foreach (char c in app.Name) id.Append(char.IsAsciiLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_');
        return id.ToString();
    }

    private static void WriteAndroidProject(string dir, AppFiles app, List<string> assemblies, List<string> files, List<string> folders, string feed, string openXrLoader = null)
    {
        var refs = new StringBuilder();
        foreach (string a in assemblies)
            refs.Append($"    <Reference Include=\"{Xml(Path.GetFileNameWithoutExtension(a))}\"><HintPath>{Xml(a)}</HintPath><Private>true</Private></Reference>\n");

        // the app's folder, its registry and its machine data, as assets under app/, registry/, state/;
        // and the list of them, which is how the activity knows what to unpack (an APK cannot be listed cheaply)
        var data = new StringBuilder();
        var list = new List<string>();
        void Asset(string source, string asset)
        {
            // the packager leaves out hidden files and folders (.DS_Store), so the list must too
            if (asset.Split('/').Any(part => part.StartsWith('.'))) return;
            data.Append($"    <AndroidAsset Include=\"{Xml(source)}\" Link=\"{Xml(asset)}\" />\n");
            list.Add(asset);
        }
        foreach (string f in files) Asset(Path.Combine(app.Directory, f), "app/" + f);
        foreach (string d in folders)
            foreach (string f in Directory.EnumerateFiles(Path.Combine(app.Directory, d), "*", SearchOption.AllDirectories))
                Asset(f, "app/" + Path.GetRelativePath(app.Directory, f).Replace('\\', '/'));
        string registry = Path.Combine(Locations.StateRoot, "registry");
        if (Directory.Exists(registry))
            foreach (string f in Directory.EnumerateFiles(registry, "*.reg"))
                Asset(f, "registry/" + Path.GetFileName(f));
        string programData = Path.Combine(Locations.StateRoot, "ProgramData");
        if (Directory.Exists(programData))
            foreach (string f in Directory.EnumerateFiles(programData, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(Locations.StateRoot, f).Replace('\\', '/');
                if (rel.Contains("/DataHistorian/", StringComparison.OrdinalIgnoreCase)) continue;   // what the app recorded as it ran
                Asset(f, "state/" + rel);
            }
        File.WriteAllLines(Path.Combine(dir, "wpfrun-assets.txt"), list);

        File.WriteAllText(Path.Combine(dir, "Bundle.csproj"), $"""
            <!-- Generated by wpfrun bundle (Android) for {Xml(app.Dll)}; regenerated on every run. -->
            <Project Sdk="WpfWebGpu.Sdk/0.1.1">

              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0-android</TargetFramework>
                <RuntimeIdentifier>{AndroidRid}</RuntimeIdentifier>
                <SupportedOSPlatformVersion>26</SupportedOSPlatformVersion>
                <UseWPF>true</UseWPF>
                <UseWindowsForms>true</UseWindowsForms>
                <AssemblyName>WpfRunBundle</AssemblyName>
                <RootNamespace>{AndroidPackageId(app)}</RootNamespace>
                <ApplicationId>{AndroidPackageId(app)}</ApplicationId>
                <ApplicationTitle>{Xml(app.Name)}</ApplicationTitle>
                <ApplicationVersion>1</ApplicationVersion>
                <ApplicationDisplayVersion>1.0</ApplicationDisplayVersion>
                <Nullable>disable</Nullable>
                <ImplicitUsings>disable</ImplicitUsings>
                <EnableNETAnalyzers>false</EnableNETAnalyzers>
                <AnalysisLevel>none</AnalysisLevel>
                <EnforceCodeStyleInBuild>false</EnforceCodeStyleInBuild>
                <!-- run by the JIT, as a non-AOT browser bundle is interpreted: compiling 300-odd assemblies
                     ahead of time takes the better part of an hour -->
                <RunAOTCompilation>false</RunAOTCompilation>
                <AndroidEnableProfiledAot>false</AndroidEnableProfiledAot>
                <!-- the trimmer runs (it is where the build's rewrite of the app onto the stack happens), but
                     only on what is marked trimmable: the app reaches its parts by name, as on Windows, and
                     every assembly of it is rooted (WpfWebGpu.Sdk) -->
                <PublishTrimmed>true</PublishTrimmed>
                <TrimMode>partial</TrimMode>
                <SuppressTrimAnalysisWarnings>true</SuppressTrimAnalysisWarnings>
                <EmbedAssembliesIntoApk>true</EmbedAssembliesIntoApk>
                <AndroidPackageFormat>apk</AndroidPackageFormat>
                <!-- the app's binaries were built against .NET's WindowsDesktop assemblies, which the stack replaces -->
                <MSBuildWarningsAsMessages>$(MSBuildWarningsAsMessages);MSB3277;MSB3243;MSB3245;XA0141;XA1036</MSBuildWarningsAsMessages>
                <NoWarn>$(NoWarn);CA1416;CS0618;XA0141</NoWarn>
            {(openXrLoader == null ? "" : $"""
                <!-- the immersive activity for an OpenXR headset (AndroidXrHost), with Khronos's loader -->
                <WpfWebGpuAndroidXr>true</WpfWebGpuAndroidXr>
                <WpfWebGpuOpenXrLoader>{Xml(openXrLoader)}</WpfWebGpuOpenXrLoader>
                <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
            """)}  </PropertyGroup>

              <ItemGroup>
            {refs}  </ItemGroup>

              <ItemGroup>
                <AndroidAsset Include="wpfrun-assets.txt" Link="wpfrun-assets.txt" />
            {data}  </ItemGroup>

            </Project>
            """);
        string appPath = File.Exists(app.Exe) ? app.Name + ".exe" : app.Name + ".dll";
        string id = AndroidPackageId(app);
        // what this build's assets are, so a new install unpacks them again and a relaunch does not
        string stamp = Guid.NewGuid().ToString("N");
        // --xr: the immersive activity an OpenXR headset runs the app in, each window a panel in space
        // (AndroidXrHost). Started by intent (adb shell am start -n <id>/<id>.WpfRunXrActivity) beside the flat
        // activity, which stays the launcher's; one of the two per process.
        string xrAssembly = openXrLoader == null ? "" : """
            [assembly: UsesFeature("android.hardware.vr.headtracking", Required = false)]
            [assembly: MetaData("com.oculus.supportedDevices", Value = "quest2|questpro|quest3|quest3s")]
            """;
        string xrActivity = openXrLoader == null ? "" : $$"""
            [Activity(
                Name = "{{id}}.WpfRunXrActivity",
                Label = "{{app.Name}} (VR)",
                Exported = true,
                LaunchMode = LaunchMode.SingleTask,
                ScreenOrientation = ScreenOrientation.Landscape,
                Theme = "@android:style/Theme.Black.NoTitleBar.Fullscreen",
                ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize
                                     | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize
                                     | ConfigChanges.Density | ConfigChanges.KeyboardHidden | ConfigChanges.Keyboard)]
            [IntentFilter(new[] { Android.Content.Intent.ActionMain },
                Categories = new[] { "com.oculus.intent.category.VR", "org.khronos.openxr.intent.category.IMMERSIVE_HMD" })]
            [MetaData("com.oculus.vr.focusaware", Value = "true")]
            public class WpfRunXrActivity : Activity
            {
                private AndroidHost _flat;
                private AndroidXrHost _xr;
                private static bool s_started;

                public override bool DispatchKeyEvent(Android.Views.KeyEvent e)
                    => _flat?.DispatchKey(e) == true || base.DispatchKeyEvent(e);

                protected override void OnDestroy()
                {
                    _xr?.Stop();
                    _flat?.CancelPendingDialogs();
                    base.OnDestroy();
                }

                protected override void OnCreate(Bundle savedInstanceState)
                {
                    base.OnCreate(savedInstanceState);
                    // the diagnostics' variables before the session's host is made: it reads its own
                    // (WPF_XR_STEREO, WPF_XR_COMMANDS) as it starts, before the app's thread would set them
                    WpfRunBundleHost.ApplyEnvironment();
                    AppContext.SetData("APP_CONTEXT_BASE_DIRECTORY", Path.Combine(FilesDir.AbsolutePath, "app") + "/");
                    AndroidFonts.ExtractBundled(this);
                    // the activity keeps a view hierarchy for what is not a window (the soft keyboard, the
                    // system's dialogs); the windows are the session's panels
                    var root = new FrameLayout(this);
                    SetContentView(root);
                    var flat = new AndroidHost(this, root);
                    var xr = new AndroidXrHost(this, flat);
                    AndroidWindow.Host = xr;
                    AndroidAccessibility.Host = flat;
                    AndroidPrint.Host = flat;
                    AndroidClipboard.Host = flat;
                    AndroidDialogs.Host = flat;
                    AndroidDragDrop.Host = flat;
                    _flat = flat;
                    _xr = xr;
                    if (s_started) return;
                    s_started = true;
                    xr.Start();
                    string files = FilesDir.AbsolutePath;
                    var assets = Assets;
                    root.Post(() => flat.RunApp(() => WpfRunBundleHost.RunApp(assets, files)));
                }
            }

            """;
        File.WriteAllText(Path.Combine(dir, "WpfRunActivity.cs"), $$"""
            // Generated by wpfrun bundle (Android): the window layer installed, then the app's own Main on a
            // UI thread of its own (AndroidHost.RunApp).
            using System;
            using System.IO;
            using System.Reflection;
            using Android.App;
            using Android.Content.PM;
            using Android.OS;
            using Android.Widget;
            using MS.Internal.Interop;

            // the network, which every Windows program has
            [assembly: UsesPermission(Android.Manifest.Permission.Internet)]
            [assembly: UsesPermission(Android.Manifest.Permission.AccessNetworkState)]
            {{xrAssembly}}

            namespace {{id}};

            [Activity(
                Name = "{{id}}.WpfRunActivity",
                Label = "{{app.Name}}",
                MainLauncher = true,
                Theme = "@android:style/Theme.Material.Light.NoActionBar.Fullscreen",
                // one app, as on Windows: not torn down and rebuilt per rotation
                ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize
                                     | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize
                                     | ConfigChanges.Density | ConfigChanges.KeyboardHidden | ConfigChanges.Keyboard)]
            // where windows float (a headset's panel, a desktop's window), the size a desktop app's window
            // starts at: landscape, as a monitor is -- without it the activity started as a narrow portrait
            // panel; a full-screen phone or tablet ignores it
            [Layout(DefaultWidth = "1280dp", DefaultHeight = "800dp", Gravity = "center")]
            public class WpfRunActivity : Activity
            {
                private AndroidHost _host;
                private static bool s_started;

                protected override void OnActivityResult(int requestCode, Result resultCode, Android.Content.Intent data)
                {
                    if (_host?.HandleActivityResult(requestCode, resultCode, data) == true) return;
                    base.OnActivityResult(requestCode, resultCode, data);
                }

                // the hardware keyboard's keys go to the app before any view (AndroidHost.DispatchKey)
                public override bool DispatchKeyEvent(Android.Views.KeyEvent e)
                    => _host?.DispatchKey(e) == true || base.DispatchKeyEvent(e);

                protected override void OnDestroy()
                {
                    _host?.CancelPendingDialogs();
                    base.OnDestroy();
                }

                protected override void OnCreate(Bundle savedInstanceState)
                {
                    base.OnCreate(savedInstanceState);
                    // the folder the app is started from, as on Windows (its data beside it), before
                    // anything reads it: the stack's own fonts are unpacked under it (AndroidFonts)
                    AppContext.SetData("APP_CONTEXT_BASE_DIRECTORY", Path.Combine(FilesDir.AbsolutePath, "app") + "/");
                    AndroidFonts.ExtractBundled(this);
                    var root = new FrameLayout(this);
                    SetContentView(root);
                    var host = new AndroidHost(this, root);
                    AndroidWindow.Host = host;
                    AndroidAccessibility.Host = host;
                    AndroidPrint.Host = host;
                    AndroidClipboard.Host = host;
                    AndroidDialogs.Host = host;
                    AndroidDragDrop.Host = host;
                    _host = host;
                    if (s_started) return;
                    s_started = true;
                    string files = FilesDir.AbsolutePath;
                    var assets = Assets;
                    // after the first layout pass, so the app's window is given the activity's real size
                    root.Post(() => host.RunApp(() => WpfRunBundleHost.RunApp(assets, files)));
                }
            }

            {{xrActivity}}
            internal static class WpfRunBundleHost
            {
                /// <summary>The app's Main, on the app's UI thread; the process ends when it returns.</summary>
                internal static void RunApp(Android.Content.Res.AssetManager assets, string files)
                {
                    ApplyEnvironment();
                    if (System.Environment.GetEnvironmentVariable("WPF_WEBGPU_LOG_FIRST_CHANCE") is "1")
                        AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
                            Console.WriteLine("first chance: " + e.Exception.GetType().FullName + ": " + e.Exception.Message + "\n" + System.Environment.StackTrace);
                    string root = Path.Combine(files, "app");
                    Unpack(assets, files);
                    AppContext.SetData("APP_CONFIG_FILE", Path.Combine(root, "{{app.Name}}.dll.config"));
                    AppContext.SetData("WPFWEBGPU_APP_PATH", Path.Combine(root, "{{appPath}}"));
                    AppContext.SetData("WPFWEBGPU_STATE_DIR", Path.Combine(files, "state"));
                    System.Environment.SetEnvironmentVariable("WPFWEBGPU_REGISTRY_DIR", Path.Combine(files, "registry"));
                    System.Environment.SetEnvironmentVariable("WPF_USE_WEBGPU_COMPOSITION", "1");
                    Directory.SetCurrentDirectory(root);
                    InitializeWin32();
                    Assembly app = Assembly.Load(new AssemblyName("{{app.Name}}"));
                    Assembly.SetEntryAssembly(app);
                    MethodInfo entry = app.EntryPoint ?? throw new InvalidOperationException("{{app.Name}} has no entry point");
                    object result = entry.Invoke(null, BindingFlags.DoNotWrapExceptions, null,
                        entry.GetParameters().Length == 1 ? new object[] { Array.Empty<string>() } : null, null);
                    Console.WriteLine("{{app.Name}}: Main returned " + (result ?? 0));
                }

                /// <summary>Diagnostics' variables, from a system property: an Android app inherits no
                /// environment from adb (adb shell setprop debug.wpfrun.env "A=1 B=2").</summary>
                internal static void ApplyEnvironment()
                {
                    string env = AndroidSystemProperties.Get("debug.wpfrun.env");
                    foreach (string pair in env.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    {
                        int eq = pair.IndexOf('=');
                        if (eq > 0) System.Environment.SetEnvironmentVariable(pair[..eq], pair[(eq + 1)..]);
                    }
                }

                /// <summary>The app's folder, its registry and its machine data out of the APK into the app's
                /// files, once per install: from then on they are the app's own, as on a Windows machine.</summary>
                private static void Unpack(Android.Content.Res.AssetManager assets, string files)
                {
                    string done = Path.Combine(files, ".wpfrun-unpacked");
                    const string stamp = "{{stamp}}";
                    if (File.Exists(done) && File.ReadAllText(done) == stamp) return;
                    using var list = new StreamReader(assets.Open("wpfrun-assets.txt"));
                    var buffer = new byte[1 << 16];
                    for (string asset; (asset = list.ReadLine()) != null;)
                    {
                        if (asset.Length == 0) continue;
                        string to = Path.Combine(files, asset);
                        // the registry and the machine's data are the app's to change: unpacked once only
                        if (!asset.StartsWith("app/", StringComparison.Ordinal) && File.Exists(to)) continue;
                        Directory.CreateDirectory(Path.GetDirectoryName(to));
                        using var input = assets.Open(asset);
                        using var output = File.Create(to);
                        int n;
                        while ((n = input.Read(buffer, 0, buffer.Length)) > 0) output.Write(buffer, 0, n);
                    }
                    File.WriteAllText(done, stamp);
                }

                /// <summary>The stack's Windows API the app's P/Invokes were rewritten onto at build
                /// (WpfWebGpu.Win32): set up before the app's first call.</summary>
                private static void InitializeWin32()
                {
                    Assembly win32;
                    try { win32 = Assembly.Load(new AssemblyName("WpfWebGpu.Win32")); }
                    catch (FileNotFoundException) { return; }
                    var seen = new System.Collections.Generic.HashSet<string>();
                    Action<string, string> logOnce = (key, message) => { lock (seen) if (seen.Add(key)) Console.WriteLine(message); };
                    win32.GetType("WpfWebGpu.Win32.Exports")?.GetMethod("Initialize", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, new object[] { logOnce });
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
