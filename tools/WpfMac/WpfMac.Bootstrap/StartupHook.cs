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
        VersionUnification.Install(AssemblyLoadContext.Default);
        if (AppContext.GetData(Keys.OverlayDirectory) is string overlay && overlay.Length > 0)
        {
            WpfMac.Bootstrap.Win32.ShimLibrary.Initialize(overlay);
        }
        if (!OperatingSystem.IsWindows() && AppContext.GetData(Keys.StateDirectory) is string state && state.Length > 0)
        {
            InstallDetours(state);
        }

        CompatLog.Info($"wpfmac bootstrap: {Assembly.GetEntryAssembly()?.GetName().Name} on {System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier}, base {AppContext.BaseDirectory}");
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
    }
}
