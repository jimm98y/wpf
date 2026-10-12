namespace WpfMac;

/// <summary>
/// Every place wpfmac writes to. There are exactly these, all per-user, so removing wpfmac is deleting
/// its own folder plus what <see cref="All"/> lists (which is what `wpfmac clean --all` does). Nothing
/// is ever written into an app's folder.
/// </summary>
internal static class Locations
{
    /// <summary>Generated runtimeconfig/deps.json per app, and downloaded native packages. Disposable.</summary>
    public static string CacheRoot => Override("WPFMAC_CACHE") ?? (HostInfo.IsMacOS
        ? Path.Combine(Home, "Library", "Caches", "WpfMac")
        : Path.Combine(LocalAppData, "WpfMac", "cache"));

    /// <summary>One compatibility log per app.</summary>
    public static string LogRoot => Override("WPFMAC_LOGS") ?? (HostInfo.IsMacOS
        ? Path.Combine(Home, "Library", "Logs", "WpfMac")
        : Path.Combine(LocalAppData, "WpfMac", "logs"));

    /// <summary>
    /// State the shims keep for apps where the platform has none: the registry hive, ProgramData.
    /// Shared by every app run through wpfmac, as the real registry is shared on Windows. Unused on
    /// Windows, where the apps get the real registry and folders.
    /// </summary>
    public static string StateRoot => Override("WPFMAC_STATE") ?? (HostInfo.IsMacOS
        ? Path.Combine(Home, "Library", "Application Support", "WpfMac")
        : Path.Combine(LocalAppData, "WpfMac", "state"));

    public static IEnumerable<string> All => new[] { CacheRoot, LogRoot, StateRoot }.Distinct();

    /// <summary>
    /// The overlay: the published WpfMac.Payload. wpfmac ships as artifacts/wpfmac/&lt;rid&gt;/ with
    /// launcher/ and overlay/ side by side.
    /// </summary>
    public static string? FindOverlay()
    {
        string? env = Override("WPFMAC_OVERLAY");
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

    public const string PayloadDirName = "wpfmac.payload";
    public const string PayloadName = "WpfMac.Payload";

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static string? Override(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;
}
