// Supplies the assets an app's deps.json names for THIS platform but that were never copied into its
// folder. A Windows build lists, for example, SkiaSharp.NativeAssets.macOS's libSkiaSharp.dylib
// (the package is a dependency) while only the Windows binaries are deployed. The asset is a file of an
// exact package version that the deps.json identifies, so the fix is generic: fetch that version from
// nuget.org into wpfrun's cache, once, and resolve the asset from there.
//
// The cache uses NuGet's own folder layout (<id lowercase>/<version>/...), so it is also an ordinary
// probing path: the merged deps.json names a library with path "<id>/<version>" and the host finds it.

using System.IO.Compression;

namespace WpfWebGpu;

internal sealed class PackageFetcher
{
    private static readonly HttpClient s_http = new() { Timeout = TimeSpan.FromMinutes(5) };

    public PackageFetcher(string root, bool offline)
    {
        Root = root;
        Offline = offline;
    }

    /// <summary>The probing root: &lt;root&gt;/&lt;id&gt;/&lt;version&gt;/.</summary>
    public string Root { get; }
    public bool Offline { get; }

    /// <summary>The library path relative to <see cref="Root"/>, as a deps.json names it.</summary>
    public static string LibraryPath(string id, string version) => $"{id.ToLowerInvariant()}/{version.ToLowerInvariant()}";

    /// <summary>
    /// Makes sure package <paramref name="id"/> <paramref name="version"/> is extracted under the root
    /// and returns its folder, or null (with <paramref name="error"/>) if it cannot be had.
    /// </summary>
    public string? Ensure(string id, string version, out string? error)
    {
        error = null;
        string dir = Path.Combine(Root, LibraryPath(id, version));
        string marker = Path.Combine(dir, ".wpfrun-complete");
        if (File.Exists(marker)) return dir;

        // A package the machine already restored for something else.
        string nugetCache = Environment.GetEnvironmentVariable("NUGET_PACKAGES") is { Length: > 0 } np
            ? np
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        string restored = Path.Combine(nugetCache, LibraryPath(id, version));
        if (Directory.Exists(restored) && Directory.EnumerateFileSystemEntries(restored).Any())
        {
            CopyTree(restored, dir);
            File.WriteAllText(marker, restored);
            return dir;
        }

        if (Offline)
        {
            error = $"{id} {version} is not in the NuGet cache and --offline was given";
            return null;
        }

        string url = $"https://api.nuget.org/v3-flatcontainer/{id.ToLowerInvariant()}/{version.ToLowerInvariant()}/{id.ToLowerInvariant()}.{version.ToLowerInvariant()}.nupkg";
        try
        {
            Console.Error.WriteLine($"wpfrun: fetching {id} {version} for the assets this platform needs");
            byte[] bytes = s_http.GetByteArrayAsync(url).GetAwaiter().GetResult();
            string tmp = dir + ".tmp";
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            using (var zip = new ZipArchive(new MemoryStream(bytes)))
            {
                zip.ExtractToDirectory(tmp, overwriteFiles: true);
            }
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.Move(tmp, dir);
            File.WriteAllText(marker, url);
            return dir;
        }
        catch (Exception e)
        {
            error = $"{id} {version}: {e.Message}";
            return null;
        }
    }

    private static void CopyTree(string from, string to)
    {
        foreach (string d in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, d)));
        }
        Directory.CreateDirectory(to);
        foreach (string f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            File.Copy(f, Path.Combine(to, Path.GetRelativePath(from, f)), overwrite: true);
        }
    }
}
