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

        CompatLog.Info($"wpfmac bootstrap: {Assembly.GetEntryAssembly()?.GetName().Name} on {System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier}, base {AppContext.BaseDirectory}");
    }
}

namespace WpfMac.Bootstrap
{
    /// <summary>The runtimeconfig properties wpfmac passes to this hook.</summary>
    internal static class Keys
    {
        public const string AppDirectory = "WpfMac.AppDirectory";
        public const string LogPath = "WpfMac.LogPath";
    }
}
