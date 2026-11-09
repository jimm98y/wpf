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
        string appPath = File.Exists(app.Exe) ? app.Name + ".exe" : app.Name + ".dll";
        // what this build's assets are, so a new install unpacks them again and a relaunch does not
        WriteAndroidAppInfo(dir, app.Name, appPath, Guid.NewGuid().ToString("N"));
        WriteAndroidIcons(dir, WpfWebGpu.Packager.PeFile.ReadIcon(app.Exe));
        WriteAndroidHost(dir, $"for {app.Dll}", AndroidPackageId(app), AndroidPackageId(app), app.Name, refs.ToString(), data.ToString(), feed, openXrLoader, template: false);
    }

    /// <summary>The app the host starts (AndroidAppInfo): its assembly's name, the file it is started as, and
    /// the stamp of its assets. An asset, so that a template's host is told by the Packager.</summary>
    internal static void WriteAndroidAppInfo(string dir, string name, string appPath, string stamp) =>
        File.WriteAllText(Path.Combine(dir, "wpfrun-app.txt"), $"name={name}\nexe={appPath}\nstamp={stamp}\n");

    // The template (wpfrun pack --android): the host with no app, which the Packager fills (its assets, the
    // app's assemblies loaded from them, its name, id and icon in the manifest) without a build. Its own
    // names are ones the Packager finds and replaces in the binary manifest.
    internal const string AndroidTemplateId = "com.wpfrun.template";
    /// <summary>The apps one package may hold (the template's activity slots: WpfRunActivity, WpfRunApp2..).</summary>
    internal const int AndroidSlots = 8;
    internal const string AndroidTemplateLabel = "WpfRunTemplateLabel";
    internal const string AndroidTemplateNamespace = "wpfrun.host";

    internal static void WriteAndroidTemplateProject(string dir, string feed, string openXrLoader)
    {
        File.WriteAllLines(Path.Combine(dir, "wpfrun-assets.txt"), Array.Empty<string>());
        WriteAndroidAppInfo(dir, "", "", "");
        WriteAndroidIcons(dir, null);
        WriteAndroidHost(dir, "as the Packager's template", AndroidTemplateId, AndroidTemplateNamespace, AndroidTemplateLabel, "", "", feed, openXrLoader, template: true);
    }

    /// <summary>The launcher icon at each density (appicon/&lt;density&gt;.png): the app's own, or a plain one.</summary>
    internal static void WriteAndroidIcons(string dir, byte[]? ico)
    {
        string icons = Path.Combine(dir, "appicon");
        if (Directory.Exists(icons)) Directory.Delete(icons, recursive: true);
        Directory.CreateDirectory(icons);
        var image = (ico != null ? WpfWebGpu.Packager.Icons.Largest(ico) : null) ?? WpfWebGpu.Packager.Icons.Placeholder();
        foreach (var (density, size) in WpfWebGpu.Packager.Icons.AndroidMipmaps)
            File.WriteAllBytes(Path.Combine(icons, density + ".png"), WpfWebGpu.Packager.Icons.PngAt(image, size));
    }

    private static void WriteAndroidHost(string dir, string what, string id, string ns, string label, string refs, string data, string feed, string openXrLoader, bool template)
    {
        // a template keeps every assembly whole: the app it is filled with may use any of them
        string templateProject = !template ? "" : """

              <!-- the template: every assembly kept whole, for whatever app it is filled with (rooted as the SDK
                   roots an app's: before the linker's inputs are taken) -->
              <Target Name="WpfRunTemplateRootAll" BeforeTargets="PrepareForILLink">
                <ItemGroup>
                  <_WpfRunTemplateAsm Include="@(ResolvedFileToPublish)" Condition="'%(Extension)' == '.dll' and !$([System.String]::Copy('%(Filename)').EndsWith('.resources'))" />
                  <TrimmerRootAssembly Include="@(_WpfRunTemplateAsm->'%(Filename)'->Distinct())" RootMode="All" />
                </ItemGroup>
              </Target>
            """;
        // the template's further app slots (an installation's apps in one package: each its own launcher icon,
        // process and task): their icons, which the Packager fills
        string slotIcons = !template ? "" : string.Concat(Enumerable.Range(2, AndroidSlots - 1).Select(n =>
            $"\n                <AndroidResource Include=\"appicon/*.png\" Link=\"Resources/mipmap-%(RecursiveDir)%(Filename)/appicon{n}.png\" />"));
        File.WriteAllText(Path.Combine(dir, "Bundle.csproj"), $"""
            <!-- Generated by wpfrun bundle (Android) {Xml(what)}; regenerated on every run. -->
            <Project Sdk="WpfWebGpu.Sdk/0.1.1">

              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0-android</TargetFramework>
                <RuntimeIdentifier>{AndroidRid}</RuntimeIdentifier>
                <SupportedOSPlatformVersion>26</SupportedOSPlatformVersion>
                <UseWPF>true</UseWPF>
                <UseWindowsForms>true</UseWindowsForms>
                <AssemblyName>WpfRunBundle</AssemblyName>
                <RootNamespace>{ns}</RootNamespace>
                <ApplicationId>{id}</ApplicationId>
                <ApplicationTitle>{Xml(label)}</ApplicationTitle>
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
                <AndroidAsset Include="wpfrun-app.txt" Link="wpfrun-app.txt" />
                <AndroidResource Include="appicon/*.png" Link="Resources/mipmap-%(RecursiveDir)%(Filename)/appicon.png" />{slotIcons}
            {data}  </ItemGroup>
            {templateProject}
            </Project>
            """);
        // --xr: the immersive activity an OpenXR headset runs the app in, each window a panel in space
        // (AndroidXrHost). Started by intent (adb shell am start -n <id>/<id>.WpfRunXrActivity) beside the flat
        // activity, which stays the launcher's; one of the two per process.
        string xrAssembly = openXrLoader == null ? "" : """
            [assembly: UsesFeature("android.hardware.vr.headtracking", Required = false)]
            // the room behind the windows (AndroidXrHost's passthrough layer): Meta's runtime shows it only to an
            // app that says it uses it
            [assembly: UsesFeature("com.oculus.feature.PASSTHROUGH", Required = false)]
            [assembly: MetaData("com.oculus.supportedDevices", Value = "quest2|questpro|quest3|quest3s")]
            """;
        // the flat activity's way into the immersive one (B or Y on a controller, the 3D view under the pointer
        // filling the eyes if there is one there), and its windows back from it when it returns
        string flatToXr = openXrLoader == null ? "" : $$"""
                    // B or Y: the app immersive, a 3D view under the pointer filling the eyes (a hybrid app's way in).
                    // A headset's system gives a panel app B as Back (from its pointer, "source Mouse"); a Windows
                    // program has no Back of its own to lose to it
                    bool immersiveKey = e.KeyCode is Android.Views.Keycode.ButtonB or Android.Views.Keycode.ButtonY
                        || (e.KeyCode == Android.Views.Keycode.Back && IsHeadset);
                    // a press of this panel's own: not the release of the B that brought it back (the session
                    // acts on the press; its release lands here), nor one in the moment it comes back
                    if (immersiveKey && e.Action == Android.Views.KeyEventActions.Down && e.RepeatCount == 0)
                        _immersivePressed = Android.OS.SystemClock.UptimeMillis() - _resumedAt > 800;
                    if (immersiveKey && e.Action == Android.Views.KeyEventActions.Up && _immersivePressed)
                    {
                        _immersivePressed = false;
                        AndroidWindow.TryGetCursorPixels(out int x, out int y);
                        var intent = new Android.Content.Intent(this, typeof(WpfRunXrActivity));
                        intent.PutExtra("x", x);
                        intent.PutExtra("y", y);
                        StartActivity(intent);
                        return true;
                    }
                    if (immersiveKey) return true;
            """;
        string xrActivity = openXrLoader == null ? "" : $$"""
            [Activity(
                Name = "{{ns}}.WpfRunXrActivity",
                Label = "{{label}} (VR)",
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

                // B or Y are the session's own (AndroidXrHost); the system's Back from them must not end the activity
                public override bool DispatchKeyEvent(Android.Views.KeyEvent e)
                    => e.KeyCode == Android.Views.Keycode.Back || _flat?.DispatchKey(e) == true || base.DispatchKeyEvent(e);

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
                    WpfRunBundleHost.MachineEnvironment(Assets, FilesDir.AbsolutePath);
                    AppContext.SetData("APP_CONTEXT_BASE_DIRECTORY", WpfRunBundleHost.AppFolder(Assets, FilesDir.AbsolutePath, 1) + "/");
                    AndroidFonts.ExtractBundled(this);
                    // the activity keeps a view hierarchy for what is not a window (the soft keyboard, the
                    // system's dialogs); the windows are the session's panels
                    var root = new FrameLayout(this);
                    SetContentView(root);
                    var flat = new AndroidHost(this, root);
                    var xr = new AndroidXrHost(this, flat);
                    // (the panel's windows, if the app runs already, are moved across below: Rehost)
                    if (!WpfRunBundleHost.Started) AndroidWindow.Host = xr;
                    AndroidAccessibility.Host = flat;
                    AndroidPrint.Host = flat;
                    AndroidClipboard.Host = flat;
                    AndroidDialogs.Host = flat;
                    AndroidDragDrop.Host = flat;
                    _flat = flat;
                    _xr = xr;
                    // B or Y away from a 3D view: back to the 2D panel, with the windows
                    xr.LeaveRequested += () => RunOnUiThread(() =>
                    {
                        var back = new Android.Content.Intent(this, typeof(WpfRunActivity));
                        back.AddFlags(Android.Content.ActivityFlags.ReorderToFront);
                        StartActivity(back);
                        Finish();
                    });
                    if (Intent?.HasExtra("x") == true) xr.FullscreenWhenShown(Intent.GetIntExtra("x", 0), Intent.GetIntExtra("y", 0));
                    if (WpfRunBundleHost.Started)
                    {
                        // the panel's app come here: its windows the session's panels now
                        xr.Start();
                        AndroidWindow.Rehost(xr);
                        return;
                    }
                    WpfRunBundleHost.Started = true;
                    xr.Start();
                    string files = FilesDir.AbsolutePath;
                    var assets = Assets;
                    root.Post(() => flat.RunApp(() => WpfRunBundleHost.RunApp(assets, files, 1, this, null)));
                }
            }

            """;
        // the further app slots (template only): each app of an installation its own launcher icon, process and task;
        // disabled until the Packager fills one (an empty slot shows no icon)
        string slotActivities = !template ? "" : string.Concat(Enumerable.Range(2, AndroidSlots - 1).Select(n => $$"""

            [Activity(
                Name = "{{ns}}.WpfRunApp{{n}}",
                Label = "wpfrun-slot-{{n}}",
                Icon = "@mipmap/appicon{{n}}",
                Enabled = false,
                Exported = true,
                Process = ":app{{n}}",
                TaskAffinity = "{{ns}}.app{{n}}",
                LaunchMode = LaunchMode.SingleTask,
                Theme = "@android:style/Theme.Material.Light.NoActionBar.Fullscreen",
                ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize
                                     | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize
                                     | ConfigChanges.Density | ConfigChanges.KeyboardHidden | ConfigChanges.Keyboard)]
            [IntentFilter(new[] { Android.Content.Intent.ActionMain }, Categories = new[] { Android.Content.Intent.CategoryLauncher })]
            [Layout(DefaultWidth = "1280dp", DefaultHeight = "800dp", Gravity = "center")]
            public class WpfRunApp{{n}} : WpfRunActivity
            {
                protected override int Slot => {{n}};
            }
            """));
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

            namespace {{ns}};

            [Activity(
                Name = "{{ns}}.WpfRunActivity",
                Label = "{{label}}",
                Icon = "@mipmap/appicon",
                MainLauncher = true,
                // one panel: coming back from the immersive activity is this one again, not another beside it
                LaunchMode = LaunchMode.SingleTask,
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

                /// <summary>Which of the package's apps this activity starts (1: the first, WpfRunApp2.. the others).</summary>
                protected virtual int Slot => 1;

                protected override void OnActivityResult(int requestCode, Result resultCode, Android.Content.Intent data)
                {
                    if (_host?.HandleActivityResult(requestCode, resultCode, data) == true) return;
                    base.OnActivityResult(requestCode, resultCode, data);
                }

                // the hardware keyboard's keys go to the app before any view (AndroidHost.DispatchKey)
                public override bool DispatchKeyEvent(Android.Views.KeyEvent e)
                {
                    if (System.Environment.GetEnvironmentVariable("WPF_TRACE_ANDROID") == "1")
                        Console.WriteLine($"[android] activity key {e.Action} {e.KeyCode} source {e.Source}");
            {{flatToXr}}
                    return _host?.DispatchKey(e) == true || base.DispatchKeyEvent(e);
                }

                // a headset's (Meta's declares its own feature, not Android's VR one; Android XR its OpenXR)
                private bool IsHeadset => PackageManager is { } pm && (pm.HasSystemFeature("oculus.hardware.standalone_vr")
                    || pm.HasSystemFeature("android.hardware.vr.headtracking") || pm.HasSystemFeature("android.software.xr.api.openxr"));

                private bool _immersivePressed;
                private long _resumedAt;

                // back from the immersive activity: the windows are this one's again
                protected override void OnResume()
                {
                    base.OnResume();
                    _resumedAt = Android.OS.SystemClock.UptimeMillis();
                    _immersivePressed = false;
                    if (_host != null && WpfRunBundleHost.Started && !ReferenceEquals(AndroidWindow.Host, _host)) Install(_host);
                }

                private static void Install(AndroidHost host)
                {
                    AndroidAccessibility.Host = host;
                    AndroidPrint.Host = host;
                    AndroidClipboard.Host = host;
                    AndroidDialogs.Host = host;
                    AndroidDragDrop.Host = host;
                    if (AndroidWindow.Host == null) AndroidWindow.Host = host;
                    else AndroidWindow.Rehost(host);
                }

                protected override void OnDestroy()
                {
                    _host?.CancelPendingDialogs();
                    base.OnDestroy();
                }

                protected override void OnCreate(Bundle savedInstanceState)
                {
                    base.OnCreate(savedInstanceState);
                    // the diagnostics' variables and the machine's registry and data before the stack starts
                    WpfRunBundleHost.ApplyEnvironment();
                    WpfRunBundleHost.MachineEnvironment(Assets, FilesDir.AbsolutePath);
                    // the folder the app is started from, as on Windows (its data beside it), before
                    // anything reads it: the stack's own fonts are unpacked under it (AndroidFonts)
                    AppContext.SetData("APP_CONTEXT_BASE_DIRECTORY", WpfRunBundleHost.AppFolder(Assets, FilesDir.AbsolutePath, Slot) + "/");
                    AndroidFonts.ExtractBundled(this);
                    var root = new FrameLayout(this);
                    SetContentView(root);
                    var host = new AndroidHost(this, root);
                    Install(host);
                    _host = host;
                    if (WpfRunBundleHost.Started) return;
                    WpfRunBundleHost.Started = true;
                    string files = FilesDir.AbsolutePath;
                    var assets = Assets;
                    // after the first layout pass, so the app's window is given the activity's real size
                    int slot = Slot;
                    string args = Intent?.GetStringExtra(WpfRunBundleHost.ArgsExtra);
                    root.Post(() => host.RunApp(() => WpfRunBundleHost.RunApp(assets, files, slot, this, args)));
                }
            }
            {{slotActivities}}

            {{xrActivity}}
            internal static class WpfRunBundleHost
            {
                /// <summary>The app's Main has been started: one per process, whichever activity came first.</summary>
                internal static bool Started;

                /// <summary>The command line another app of the package starts this one with (Process.Start).</summary>
                internal const string ArgsExtra = "wpfrun.args";

                /// <summary>The app's information file: wpfrun-app.txt for the first slot, wpfrun-app&lt;n&gt;.txt for the others.</summary>
                private static string InfoAsset(int slot) => slot == 1 ? "wpfrun-app.txt" : "wpfrun-app" + slot + ".txt";

                /// <summary>Where the slot's app is: install/&lt;its folder in the installation&gt; (an installation's apps
                /// where they are on Windows, beside each other), or app/ for a package of one app.</summary>
                internal static string AppFolder(Android.Content.Res.AssetManager assets, string files, int slot)
                {
                    string folder = AppInfo(assets, slot)["folder"];
                    return folder.Length > 0 ? Path.Combine(files, "install", folder) : Path.Combine(files, "app");
                }

                /// <summary>The app's Main, on the app's UI thread; the process ends when it returns.</summary>
                internal static void RunApp(Android.Content.Res.AssetManager assets, string files, int slot, Activity activity, string args)
                {
                    ForwardStderr();
                    ApplyEnvironment();
                    if (System.Environment.GetEnvironmentVariable("WPF_WEBGPU_LOG_FIRST_CHANCE") is "1")
                        AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
                            Console.WriteLine("first chance: " + e.Exception.GetType().FullName + ": " + e.Exception.Message + "\n" + System.Environment.StackTrace);
                    string root = AppFolder(assets, files, slot);
                    var info = AppInfo(assets, slot);
                    string name = info["name"];
                    // a product written for Windows only, told it runs on it (WindowsPaths: WPFWEBGPU_OS_IDENTITY)
                    if (info["osidentity"].Length > 0) System.Environment.SetEnvironmentVariable("WPFWEBGPU_OS_IDENTITY", info["osidentity"]);
                    Unpack(assets, files, info["stamp"]);
                    // Process.Start of another app of the package: that app's activity, as Windows starts its process
                    AppContext.SetData("WPFWEBGPU_START_APP", (Func<string, string, bool>)((exe, commandLine) => StartApp(activity, assets, files, exe, commandLine)));
                    // the app's assemblies the APK does not hold (a template's app: all of them) are its folder's,
                    // as on Windows: there, and a culture's satellites in the culture's folder
                    System.Runtime.Loader.AssemblyLoadContext.Default.Resolving += (context, asked) =>
                    {
                        string culture = string.IsNullOrEmpty(asked.CultureName) || asked.CultureName == "neutral" ? "" : asked.CultureName;
                        string file = Path.Combine(root, culture, asked.Name + ".dll");
                        return File.Exists(file) ? context.LoadFromAssemblyPath(file) : null;
                    };
                    AppContext.SetData("APP_CONFIG_FILE", Path.Combine(root, name + ".dll.config"));
                    AppContext.SetData("WPFWEBGPU_APP_PATH", Path.Combine(root, info["exe"]));
                    System.Environment.SetEnvironmentVariable("WPF_USE_WEBGPU_COMPOSITION", "1");
                    Directory.SetCurrentDirectory(root);
                    InitializeWin32();
                    Assembly app = Assembly.Load(new AssemblyName(name));
                    Assembly.SetEntryAssembly(app);
                    MethodInfo entry = app.EntryPoint ?? throw new InvalidOperationException(name + " has no entry point");
                    string[] argv = SplitCommandLine(args);
                    object result = entry.Invoke(null, BindingFlags.DoNotWrapExceptions, null,
                        entry.GetParameters().Length == 1 ? new object[] { argv } : null, null);
                    Console.WriteLine(name + ": Main returned " + (result ?? 0));
                }

                /// <summary>The process's standard error (wgpu's and Rust's panics, which say why the process is about
                /// to abort; the managed code's Console.Error) into the log: Android sends an app's stderr nowhere.</summary>
                private static void ForwardStderr()
                {
                    var fds = new int[2];
                    if (pipe(fds) != 0 || dup2(fds[1], 2) < 0) return;
                    // and the managed code's (Console.Error: the app's, the stack's diagnostics), which .NET on
                    // Android otherwise loses: the same standard error, as a program's on Windows
                    Console.SetError(new StreamWriter(new FileStream(new Microsoft.Win32.SafeHandles.SafeFileHandle(2, ownsHandle: false), FileAccess.Write), new System.Text.UTF8Encoding(false)) { AutoFlush = true });
                    var reader = new System.Threading.Thread(() =>
                    {
                        var buffer = new byte[4096];
                        var line = new System.Collections.Generic.List<byte>();
                        for (nint n; (n = read(fds[0], buffer, buffer.Length)) > 0;)
                            for (int i = 0; i < n; i++)
                            {
                                if (buffer[i] != (byte)'\n') { line.Add(buffer[i]); continue; }
                                __android_log_write(6, "stderr", System.Text.Encoding.UTF8.GetString(line.ToArray()));
                                line.Clear();
                            }
                    }) { IsBackground = true, Name = "stderr" };
                    reader.Start();
                }

                [System.Runtime.InteropServices.DllImport("libc")] private static extern int pipe(int[] fds);
                [System.Runtime.InteropServices.DllImport("libc")] private static extern int dup2(int from, int to);
                [System.Runtime.InteropServices.DllImport("libc")] private static extern nint read(int fd, byte[] buffer, nint count);
                [System.Runtime.InteropServices.DllImport("liblog")] private static extern int __android_log_write(int priority, string tag, string text);

                /// <summary>The machine the package's apps share, before the stack starts (it reads the registry as
                /// it does, and a store opened is where it was opened): its data, its registry, and the
                /// installation's paths, kept in the registry as the Packager left them (PathTokens): where they are
                /// here.</summary>
                internal static void MachineEnvironment(Android.Content.Res.AssetManager assets, string files)
                {
                    AppContext.SetData("WPFWEBGPU_STATE_DIR", Path.Combine(files, "state"));
                    string registry = Path.Combine(files, "registry");
                    System.Environment.SetEnvironmentVariable("WPFWEBGPU_REGISTRY_DIR", registry);
                    // the registry as the package seeds it, on the first start (then the machine's: the apps'
                    // to change), there before the store is first opened
                    Directory.CreateDirectory(registry);
                    foreach (string hive in assets.List("registry") ?? Array.Empty<string>())
                    {
                        string to = Path.Combine(registry, hive);
                        if (File.Exists(to)) continue;
                        // whole or not at all: another app of the package may be starting as well
                        string part = to + "." + System.Environment.ProcessId;
                        using (var input = assets.Open("registry/" + hive))
                        using (var output = File.Create(part))
                            input.CopyTo(output);
                        try { File.Move(part, to); }
                        catch (IOException) { File.Delete(part); }
                    }
                    System.Environment.SetEnvironmentVariable("WPFWEBGPU_REGISTRY_TOKENS", string.Join("\n",
                        "${WPFRUN_APPDIR}=" + Path.Combine(files, "app"),
                        "${WPFRUN_PROGRAMDATA}=" + Path.Combine(files, "state", "ProgramData"),
                        "${WPFRUN_INSTALLDIR}=" + Path.Combine(files, "install")));
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
                /// <summary>The app the APK starts (wpfrun-app.txt: name=, exe=, stamp=).</summary>
                private static System.Collections.Generic.Dictionary<string, string> AppInfo(Android.Content.Res.AssetManager assets, int slot = 1)
                {
                    var info = new System.Collections.Generic.Dictionary<string, string> { ["name"] = "", ["exe"] = "", ["stamp"] = "", ["osidentity"] = "", ["folder"] = "" };
                    using var reader = new StreamReader(assets.Open(InfoAsset(slot)));
                    for (string line; (line = reader.ReadLine()) != null;)
                    {
                        int eq = line.IndexOf('=');
                        if (eq > 0) info[line[..eq]] = line[(eq + 1)..];
                    }
                    if (info["name"].Length == 0) throw new InvalidOperationException("this APK holds no app (a template the Packager has not filled)");
                    return info;
                }

                private static void Unpack(Android.Content.Res.AssetManager assets, string files, string stamp)
                {
                    string done = Path.Combine(files, ".wpfrun-unpacked");
                    if (File.Exists(done) && File.ReadAllText(done) == stamp) return;
                    // the package's apps may start at once, each in its own process: one unpacks, the others wait
                    using var gate = Lock(Path.Combine(files, ".wpfrun-unpack.lock"));
                    if (File.Exists(done) && File.ReadAllText(done) == stamp) return;
                    using var list = new StreamReader(assets.Open("wpfrun-assets.txt"));
                    var buffer = new byte[1 << 16];
                    for (string line; (line = list.ReadLine()) != null;)
                    {
                        if (line.Length == 0) continue;
                        // "<path>" or "<path>\t<stored copy>": a file the package's apps have alike is stored once
                        int tab = line.IndexOf('\t');
                        string asset = tab < 0 ? line : line[..tab], stored = tab < 0 ? asset : line[(tab + 1)..];
                        string to = Path.Combine(files, asset);
                        // the apps are this version's; the registry and the machine's data are the apps' to change:
                        // unpacked once only, and then the machine's, shared by every app of the package
                        bool program = asset.StartsWith("app/", StringComparison.Ordinal) || asset.StartsWith("install/", StringComparison.Ordinal);
                        if (!program && File.Exists(to)) continue;
                        Directory.CreateDirectory(Path.GetDirectoryName(to));
                        using var input = assets.Open(stored);
                        using var output = File.Create(to);
                        int n;
                        while ((n = input.Read(buffer, 0, buffer.Length)) > 0) output.Write(buffer, 0, n);
                    }
                    File.WriteAllText(done, stamp);
                }

                private static FileStream Lock(string path)
                {
                    for (int i = 0; ; i++)
                    {
                        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                        catch (IOException) when (i < 6000) { System.Threading.Thread.Sleep(100); }
                    }
                }

                /// <summary>Another app of the package, by the .exe the app starts (its path in the installation, or
                /// its file name): its activity, with the command line, in its own process and task.</summary>
                private static bool StartApp(Activity activity, Android.Content.Res.AssetManager assets, string files, string exe, string commandLine)
                {
                    var apps = new System.Collections.Generic.List<(int Slot, string Path)>();
                    try
                    {
                        using var reader = new StreamReader(assets.Open("wpfrun-apps.txt"));
                        for (string line; (line = reader.ReadLine()) != null;)
                        {
                            string[] f = line.Split('\t');
                            if (f.Length == 3 && int.TryParse(f[0], out int slot)) apps.Add((slot, Path.Combine(files, "install", f[1], f[2])));
                        }
                    }
                    catch (Exception) { return false; }          // a package of one app
                    string wanted = Path.GetFullPath(exe.Replace('\\', '/'));
                    int found = 0;
                    foreach (var app in apps)
                        if (string.Equals(Path.GetFullPath(app.Path), wanted, StringComparison.OrdinalIgnoreCase)) { found = app.Slot; break; }
                    if (found == 0)
                        foreach (var app in apps)
                            if (string.Equals(Path.GetFileName(app.Path), Path.GetFileName(wanted), StringComparison.OrdinalIgnoreCase)) { found = app.Slot; break; }
                    if (found == 0) return false;
                    var intent = new Android.Content.Intent();
                    intent.SetClassName(activity.PackageName, found == 1 ? "{{ns}}.WpfRunActivity" : "{{ns}}.WpfRunApp" + found);
                    intent.AddFlags(Android.Content.ActivityFlags.NewTask);
                    if (!string.IsNullOrEmpty(commandLine)) intent.PutExtra(ArgsExtra, commandLine);
                    activity.RunOnUiThread(() => activity.StartActivity(intent));
                    return true;
                }

                /// <summary>A command line as Windows splits it (CommandLineToArgvW's rules).</summary>
                private static string[] SplitCommandLine(string commandLine)
                {
                    var args = new System.Collections.Generic.List<string>();
                    if (string.IsNullOrEmpty(commandLine)) return args.ToArray();
                    var sb = new System.Text.StringBuilder();
                    bool quoted = false, any = false;
                    for (int i = 0; i < commandLine.Length; i++)
                    {
                        char c = commandLine[i];
                        if (c == '\\')
                        {
                            int slashes = 0;
                            while (i < commandLine.Length && commandLine[i] == '\\') { slashes++; i++; }
                            if (i < commandLine.Length && commandLine[i] == '"') { sb.Append('\\', slashes / 2); if (slashes % 2 == 1) sb.Append('"'); else { quoted = !quoted; } }
                            else { sb.Append('\\', slashes); i--; }
                            any = true;
                            continue;
                        }
                        if (c == '"') { quoted = !quoted; any = true; continue; }
                        if (!quoted && (c == ' ' || c == '\t')) { if (any || sb.Length > 0) { args.Add(sb.ToString()); sb.Clear(); any = false; } continue; }
                        sb.Append(c);
                        any = true;
                    }
                    if (any || sb.Length > 0) args.Add(sb.ToString());
                    return args.ToArray();
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
