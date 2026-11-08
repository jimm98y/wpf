// The files prepared for each app run from a cache (apps/<program>-<hash of its folder>): what the app's start
// needs and is slow to make. Nothing else ever removed them, so every program an installation had -- each renamed,
// moved or uninstalled one -- kept its folder for good. Each folder says which program it is for and is marked when
// that program starts; a folder whose program is gone, or that no start has used for a month, goes.

namespace WpfWebGpu;

internal static class AppCaches
{
    private const string ProgramFile = "program.txt";
    private static readonly TimeSpan s_unused = TimeSpan.FromDays(30);

    /// <summary>Marks <paramref name="cacheDir"/> as used by <paramref name="program"/> now, and prunes the other apps'.</summary>
    public static void Use(string cacheDir, string program)
    {
        try
        {
            File.WriteAllText(Path.Combine(cacheDir, ProgramFile), program);
            Directory.SetLastWriteTimeUtc(cacheDir, DateTime.UtcNow);
            if (Path.GetDirectoryName(cacheDir) is { } apps) Prune(apps, cacheDir, DateTime.UtcNow - s_unused);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Deletes the folders in <paramref name="apps"/> other than <paramref name="current"/> whose program no
    /// longer exists or that were last used before <paramref name="before"/>.</summary>
    internal static void Prune(string apps, string current, DateTime before)
    {
        foreach (string dir in Directory.EnumerateDirectories(apps))
        {
            if (string.Equals(dir, current, StringComparison.Ordinal)) continue;
            try
            {
                string record = Path.Combine(dir, ProgramFile);
                bool gone = File.Exists(record) && !File.Exists(File.ReadAllText(record).Trim());
                if (gone || Directory.GetLastWriteTimeUtc(dir) < before) Directory.Delete(dir, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
}
