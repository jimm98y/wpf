// The startup hook wpfrun installs into every app it launches.
//
// wpfrun runs an unmodified app through `dotnet exec` with a GENERATED runtimeconfig and deps.json
// that live in wpfrun's cache, never beside the app. The host derives the app's base directory from
// where the deps.json is, so without this hook AppContext.BaseDirectory (and with it
// AppDomain.BaseDirectory, which most apps use to find their content) would point into the cache.
// The first thing this hook does is put it back.
//
// It is named StartupHook, in no namespace, with a static Initialize: that is the contract the
// runtime's startup-hook loader looks for.

using System.Reflection;
using System.Runtime.Loader;
using WpfWebGpu.Bootstrap;

internal static class StartupHook
{
    public static void Initialize()
    {
        // Must run before anything reads AppContext.BaseDirectory, which caches its first answer.
        if (AppContext.GetData(Keys.AppDirectory) is string appDir && appDir.Length > 0)
        {
            AppContext.SetData("APP_CONTEXT_BASE_DIRECTORY", appDir);
        }

        CompatLog.Start(AppContext.GetData(Keys.LogPath) as string);
        // For the replacement assemblies (System.Management, EventLog, ...), which report what they
        // cannot do, and what is written to them, without depending on this hook.
        AppContext.SetData(Keys.LogCallback, (Action<string>)CompatLog.Info);
        AppContext.SetData(Keys.LogOnceCallback, (Action<string, string>)CompatLog.Once);
        VersionUnification.Install(AssemblyLoadContext.Default);
        // App assemblies the launcher chose -- with [ComImport] types, with Windows P/Invokes: rewritten in
        // memory on first load (WpfWebGpu.ComRewrite; Documentation/comimport-rewriter.md, pinvoke-rewriter.md)
        if (AppContext.GetData(Keys.ComImport) is string comImport && comImport.Length > 0)
        {
            try
            {
                Type.GetType("WpfWebGpu.ComRewrite.ComImportLoader, WpfWebGpu.ComRewrite", throwOnError: true)!
                    .GetMethod("InstallDefault")!
                    .Invoke(null, new object?[] { comImport, AppContext.GetData(Keys.AppDirectory) as string ?? AppContext.BaseDirectory,
                        AppContext.GetData(Keys.OverlayDirectory) as string ?? "", AppContext.GetData(Keys.CacheDirectory) as string, (Action<string>)CompatLog.Info });
            }
            catch (Exception e) { CompatLog.Info($"comimport: the in-memory rewriter is not available ({e.GetBaseException().Message})"); }
        }
        if (AppContext.GetData(Keys.OverlayDirectory) is string overlay && overlay.Length > 0)
        {
            // The implementations of Windows exports the app's rewritten P/Invokes call (above).
            CallWin32("WpfWebGpu.Win32.Exports", "Initialize", (Action<string, string>)CompatLog.Once);
            // Unless asked to keep them (wpfrun run --crash-reports), no crash reports or dialogs.
            if (AppContext.GetData(Keys.CrashReports) as string != "true")
            {
                WpfWebGpu.Bootstrap.Win32.QuietAborts.Install();
            }
        }
        if (!OperatingSystem.IsWindows() && AppContext.GetData(Keys.StateDirectory) is string state && state.Length > 0)
        {
            InstallDetours(state);
        }
        if (AppContext.GetData(Keys.Isolate) is string isolate && isolate.Length > 0)
        {
            InstallIsolation(isolate);
        }
        if (AppContext.GetData(Keys.TitlePrefix) is string prefix && prefix.Length > 0)
        {
            CallWin32("WpfWebGpu.Win32.TitlePrefix", "Install", prefix);
        }
        if (AppContext.GetData(Keys.ScreenshotDirectory) is string shots && shots.Length > 0)
        {
            StartScreenshots(shots, int.TryParse(AppContext.GetData(Keys.ScreenshotInterval) as string, out int s) ? s : 5);
        }

        // A diagnostic hook of the developer's own, run inside the app: DOTNET_STARTUP_HOOKS reaches
        // the launcher, which clears it for the app it starts (Runner).
        if (Environment.GetEnvironmentVariable("WPFRUN_APP_STARTUP_HOOK") is string extra && File.Exists(extra))
        {
            try { Assembly.LoadFrom(extra).GetType("StartupHook")?.GetMethod("Initialize", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.Invoke(null, null); }
            catch (Exception e) { CompatLog.Info($"app startup hook {extra}: {e.GetBaseException().Message}"); }
        }

        CompatLog.Info($"wpfrun bootstrap: {Assembly.GetEntryAssembly()?.GetName().Name} on {System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier}, base {AppContext.BaseDirectory}");
    }

    // In WpfWebGpu.Win32, which renders through WPF and WinForms; reached by name like the others.
    private static void StartScreenshots(string directory, int seconds) =>
        CallWin32("WpfWebGpu.Win32.Screenshots", "Start", directory, seconds);

    private static void CallWin32(string type, string method, params object[] args)
    {
        try
        {
            Assembly.Load(new AssemblyName("WpfWebGpu.Win32")).GetType(type)?.GetMethod(method)?.Invoke(null, args);
        }
        catch (Exception e)
        {
            CompatLog.Info($"{type}.{method}: {e.GetBaseException().Message}");
        }
    }

    private static void InstallIsolation(string suffix)
    {
        try
        {
            var type = Assembly.Load(new AssemblyName("WpfWebGpu.Detours")).GetType("WpfWebGpu.Detours.Isolation");
            object? count = type?.GetMethod("Install")?.Invoke(null, new object[] { suffix, (Action<string, string>)CompatLog.Once });
            CompatLog.Info($"isolate: named kernel objects suffixed '{suffix}' ({count} methods)");
        }
        catch (Exception e)
        {
            CompatLog.Info($"isolate: not installed: {e.GetBaseException().Message}");
        }
    }

    // WpfWebGpu.Detours carries MonoMod; reached by name so that the hook itself stays dependency-free.
    private static void InstallDetours(string state)
    {
        try
        {
            var type = Assembly.Load(new AssemblyName("WpfWebGpu.Detours")).GetType("WpfWebGpu.Detours.Detours");
            var installed = (string[]?)type?.GetMethod("InstallAll")?.Invoke(null, new object[] { state, (Action<string, string>)CompatLog.Once });
            if (installed != null) CompatLog.Info("detours: " + string.Join(", ", installed));
        }
        catch (Exception e)
        {
            CompatLog.Info($"detours: not installed: {e.GetBaseException().Message}");
        }
    }
}

namespace WpfWebGpu.Bootstrap
{
    /// <summary>The runtimeconfig properties wpfrun passes to this hook.</summary>
    internal static class Keys
    {
        public const string AppDirectory = "WpfWebGpu.AppDirectory";
        public const string LogPath = "WpfWebGpu.LogPath";
        public const string OverlayDirectory = "WpfWebGpu.OverlayDirectory";
        public const string StateDirectory = "WpfWebGpu.StateDirectory";
        public const string CacheDirectory = "WpfWebGpu.CacheDirectory";
        public const string ScreenshotDirectory = "WpfWebGpu.ScreenshotDirectory";
        public const string TitlePrefix = "WpfWebGpu.TitlePrefix";
        public const string Isolate = "WpfWebGpu.Isolate";
        public const string ScreenshotInterval = "WpfWebGpu.ScreenshotInterval";
        public const string CrashReports = "WpfWebGpu.CrashReports";
        /// <summary>The app assemblies to rewrite in memory: "name|original path|parts" lines (ComImportStep).</summary>
        public const string ComImport = "WpfWebGpu.ComImport";

        // Published by the hook, not passed to it: Action<string> and Action<string, string> (key, message).
        public const string LogCallback = "WpfWebGpu.Log";
        public const string LogOnceCallback = "WpfWebGpu.LogOnce";
    }
}
