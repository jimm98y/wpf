// The startup hook wpfmac installs into every app it launches.
//
// wpfmac runs an unmodified app through `dotnet exec` with a GENERATED runtimeconfig and deps.json
// that live in wpfmac's cache, never beside the app. The host derives the app's base directory from
// where the deps.json is, so without this hook AppContext.BaseDirectory (and with it
// AppDomain.BaseDirectory, which most apps use to find their content) would point into the cache.
// The first thing this hook does is put it back.
//
// It is named StartupHook, in no namespace, with a static Initialize: that is the contract the
// runtime's startup-hook loader looks for.

using System.Reflection;
using System.Runtime.Loader;
using WpfMac.Bootstrap;

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
        if (AppContext.GetData(Keys.OverlayDirectory) is string overlay && overlay.Length > 0)
        {
            WpfMac.Bootstrap.Win32.ShimLibrary.Initialize(overlay);
            // Windows: the app's user32 calls on the stack's own windows reach the stack.
            WpfMac.Bootstrap.Win32.WindowsShim.Initialize(overlay, AppContext.GetData(Keys.CacheDirectory) as string);
            // Unless asked to keep them (wpfmac run --crash-reports), no crash reports or dialogs.
            if (AppContext.GetData(Keys.CrashReports) as string != "true")
            {
                WpfMac.Bootstrap.Win32.ShimLibrary.QuietAborts(AppContext.GetData(Keys.LogPath) as string);
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
            CallWin32("WpfMac.Win32.TitlePrefix", "Install", prefix);
        }
        if (AppContext.GetData(Keys.ScreenshotDirectory) is string shots && shots.Length > 0)
        {
            StartScreenshots(shots, int.TryParse(AppContext.GetData(Keys.ScreenshotInterval) as string, out int s) ? s : 5);
        }

        CompatLog.Info($"wpfmac bootstrap: {Assembly.GetEntryAssembly()?.GetName().Name} on {System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier}, base {AppContext.BaseDirectory}");
    }

    // In WpfMac.Win32, which renders through WPF and WinForms; reached by name like the others.
    private static void StartScreenshots(string directory, int seconds) =>
        CallWin32("WpfMac.Win32.Screenshots", "Start", directory, seconds);

    private static void CallWin32(string type, string method, params object[] args)
    {
        try
        {
            Assembly.Load(new AssemblyName("WpfMac.Win32")).GetType(type)?.GetMethod(method)?.Invoke(null, args);
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
            var type = Assembly.Load(new AssemblyName("WpfMac.Detours")).GetType("WpfMac.Detours.Isolation");
            object? count = type?.GetMethod("Install")?.Invoke(null, new object[] { suffix, (Action<string, string>)CompatLog.Once });
            CompatLog.Info($"isolate: named kernel objects suffixed '{suffix}' ({count} methods)");
        }
        catch (Exception e)
        {
            CompatLog.Info($"isolate: not installed: {e.GetBaseException().Message}");
        }
    }

    // WpfMac.Detours carries MonoMod; reached by name so that the hook itself stays dependency-free.
    private static void InstallDetours(string state)
    {
        try
        {
            var type = Assembly.Load(new AssemblyName("WpfMac.Detours")).GetType("WpfMac.Detours.Detours");
            var installed = (string[]?)type?.GetMethod("InstallAll")?.Invoke(null, new object[] { state, (Action<string, string>)CompatLog.Once });
            if (installed != null) CompatLog.Info("detours: " + string.Join(", ", installed));
        }
        catch (Exception e)
        {
            CompatLog.Info($"detours: not installed: {e.GetBaseException().Message}");
        }
    }
}

namespace WpfMac.Bootstrap
{
    /// <summary>The runtimeconfig properties wpfmac passes to this hook.</summary>
    internal static class Keys
    {
        public const string AppDirectory = "WpfMac.AppDirectory";
        public const string LogPath = "WpfMac.LogPath";
        public const string OverlayDirectory = "WpfMac.OverlayDirectory";
        public const string StateDirectory = "WpfMac.StateDirectory";
        public const string CacheDirectory = "WpfMac.CacheDirectory";
        public const string ScreenshotDirectory = "WpfMac.ScreenshotDirectory";
        public const string TitlePrefix = "WpfMac.TitlePrefix";
        public const string Isolate = "WpfMac.Isolate";
        public const string ScreenshotInterval = "WpfMac.ScreenshotInterval";
        public const string CrashReports = "WpfMac.CrashReports";

        // Published by the hook, not passed to it: Action<string> and Action<string, string> (key, message).
        public const string LogCallback = "WpfMac.Log";
        public const string LogOnceCallback = "WpfMac.LogOnce";
    }
}
