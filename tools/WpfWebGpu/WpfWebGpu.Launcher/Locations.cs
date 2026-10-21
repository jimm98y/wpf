namespace WpfWebGpu;

/// <summary>
/// Every place wpfrun writes to. There are exactly these, all per-user, so removing wpfrun is deleting
/// its own folder plus what <see cref="All"/> lists (which is what `wpfrun clean --all` does). Nothing
/// is ever written into an app's folder.
/// </summary>
internal static class Locations
{
    /// <summary>Generated runtimeconfig/deps.json per app, and downloaded native packages. Disposable.</summary>
    public static string CacheRoot => Override("WPFRUN_CACHE") ?? (HostInfo.IsMacOS
        ? Path.Combine(Home, "Library", "Caches", "WpfWebGpu")
        : Path.Combine(LocalAppData, "WpfWebGpu", "cache"));

    /// <summary>One compatibility log per app.</summary>
    public static string LogRoot => Override("WPFRUN_LOGS") ?? (HostInfo.IsMacOS
        ? Path.Combine(Home, "Library", "Logs", "WpfWebGpu")
        : Path.Combine(LocalAppData, "WpfWebGpu", "logs"));

    /// <summary>
    /// State the shims keep for apps where the platform has none: the registry hive, ProgramData.
    /// Shared by every app run through wpfrun, as the real registry is shared on Windows. Unused on
    /// Windows, where the apps get the real registry and folders.
    /// </summary>
    public static string StateRoot => Override("WPFRUN_STATE") ?? (HostInfo.IsMacOS
        ? Path.Combine(Home, "Library", "Application Support", "WpfWebGpu")
        : Path.Combine(LocalAppData, "WpfWebGpu", "state"));

    public static IEnumerable<string> All => new[] { CacheRoot, LogRoot, StateRoot }.Distinct();

    /// <summary>
    /// The overlay: the published WpfWebGpu.Payload. wpfrun ships as artifacts/wpfrun/&lt;rid&gt;/ with
    /// launcher/ and overlay/ side by side.
    /// </summary>
    public static string? FindOverlay()
    {
        string? env = Override("WPFRUN_OVERLAY");
        if (env != null) return env;
        foreach (string candidate in new[]
        {
            Path.Combine(AppContext.BaseDirectory, "..", "overlay", PayloadDirName),
            Path.Combine(AppContext.BaseDirectory, "overlay", PayloadDirName),
        })
        {
            string full = Path.GetFullPath(candidate);
            if (File.Exists(Path.Combine(full, PayloadName + ".deps.json"))) return full;
        }
        return null;
    }

    public const string PayloadDirName = "wpfrun.payload";
    public const string PayloadName = "WpfWebGpu.Payload";

    /// <summary>
    /// The folders as wpfrun's earlier name (wpfmac) left them become this one's, once: the state (the
    /// registry hive, ProgramData) and the logs are the apps' data and move over, where nothing has been
    /// written under the new name yet. The cache is generated, and its runtimeconfigs name the earlier
    /// overlay, so it is removed instead.
    /// </summary>
    public static void AdoptEarlierName()
    {
        string old = HostInfo.IsMacOS ? "" : Path.Combine(LocalAppData, "WpfMac");
        Move(Override("WPFRUN_STATE") == null, HostInfo.IsMacOS ? Path.Combine(Home, "Library", "Application Support", "WpfMac") : Path.Combine(old, "state"), StateRoot);
        Move(Override("WPFRUN_LOGS") == null, HostInfo.IsMacOS ? Path.Combine(Home, "Library", "Logs", "WpfMac") : Path.Combine(old, "logs"), LogRoot);
        string oldCache = HostInfo.IsMacOS ? Path.Combine(Home, "Library", "Caches", "WpfMac") : Path.Combine(old, "cache");
        if (Override("WPFRUN_CACHE") == null && Directory.Exists(oldCache))
            try { Directory.Delete(oldCache, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        if (!HostInfo.IsMacOS && Directory.Exists(old) && !Directory.EnumerateFileSystemEntries(old).Any())
            try { Directory.Delete(old); } catch (IOException) { }

        static void Move(bool ours, string from, string to)
        {
            if (!ours || !Directory.Exists(from) || Directory.Exists(to)) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                Directory.Move(from, to);
                Console.Error.WriteLine($"wpfrun: moved {from} to {to} (wpfrun's earlier name was wpfmac)");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"wpfrun: could not move {from} to {to}: {e.Message}");
            }
        }
    }

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static string? Override(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;
}
