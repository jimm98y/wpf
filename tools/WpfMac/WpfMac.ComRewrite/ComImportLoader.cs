// Loads the app assemblies the launcher chose to rewrite, rewritten (Documentation/comimport-rewriter.md,
// pinvoke-rewriter.md). The launcher leaves them out of the deps, so the first request for one reaches the
// load context's Resolving event; the original's bytes are rewritten with Cecil -- its COM types, its
// location calls, its Windows P/Invokes, as the launcher listed -- and loaded with LoadFromStream (with
// symbols when a .pdb lies beside it).
//
// What a rewrite made is kept in the app's cache folder (rewritten/), keyed by the original file (path,
// size, time), the rewriter build and the implementations it was rewritten against, so a later launch
// loads it without rewriting. It is loaded from memory all the same: the cache is never the assembly's
// location.
//
// The rewritten assembly's Location, CodeBase and Module names answer with the original file's
// (WpfMac.ComInterop.Com.RegisterOriginal), and its P/Invokes find native libraries in the original's
// folder (ResolvingUnmanagedDll), as an assembly loaded from that folder would.

using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using WpfMac.ComInterop;

namespace WpfMac.ComRewrite;

public sealed class ComImportLoader
{
    /// <summary>What of an assembly is rewritten.</summary>
    [Flags]
    public enum Parts { None = 0, Com = 1, Location = 2, PInvokes = 4 }

    private readonly Dictionary<string, (string Path, Parts Parts)> _originals;          // assembly name -> original file
    private readonly string[] _searchDirs;
    private readonly string? _cacheDir;
    private readonly Action<string>? _log;
    private readonly Dictionary<Assembly, string> _loaded = new();
    private readonly object _lock = new();

    /// <param name="originals">assembly name -> its original file, and what of it is rewritten</param>
    /// <param name="searchDirs">where the rewriter resolves the assemblies' references</param>
    /// <param name="cacheDir">where rewritten images are kept between launches (null: nowhere)</param>
    public ComImportLoader(IDictionary<string, (string Path, Parts Parts)> originals, IEnumerable<string> searchDirs, string? cacheDir, Action<string>? log)
    {
        _originals = new Dictionary<string, (string, Parts)>(originals, StringComparer.OrdinalIgnoreCase);
        _searchDirs = searchDirs.ToArray();
        _cacheDir = cacheDir;
        _log = log;
    }

    /// <summary>The launcher's list, as it passes it (WpfMac.ComImport): "name|path|parts" lines, parts a
    /// comma-separated list of com, location, pinvoke ("name|path" alone: com).</summary>
    public static Dictionary<string, (string Path, Parts Parts)> Parse(string? list)
    {
        var d = new Dictionary<string, (string, Parts)>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(list)) return d;
        foreach (string line in list.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('|');
            if (parts.Length < 2 || parts[0].Length == 0) continue;
            var what = Parts.None;
            foreach (string p in (parts.Length > 2 ? parts[2] : "com").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                what |= p switch { "com" => Parts.Com, "location" => Parts.Location, "pinvoke" => Parts.PInvokes, _ => Parts.None };
            d[parts[0]] = (parts[1], what);
        }
        return d;
    }

    /// <summary>The startup hook's entry (reached by name): the launcher's list, into the default context.</summary>
    public static void InstallDefault(string list, string appDir, string overlayDir, string? cacheDir, Action<string> log)
    {
        var loader = new ComImportLoader(Parse(list), new[] { appDir, overlayDir, RuntimeEnvironment.GetRuntimeDirectory() },
            cacheDir == null ? null : Path.Combine(cacheDir, "rewritten"), log);
        loader.Install(AssemblyLoadContext.Default);
        log($"rewriter: {loader._originals.Count} app assemblies are rewritten when first loaded: {string.Join(", ", loader._originals.Select(kv => $"{kv.Key} ({kv.Value.Parts})"))}");
        if (loader._originals.Values.Any(v => (v.Parts & Parts.PInvokes) != 0) && Win32Catalog.For(loader._searchDirs) is { Problems.Count: > 0 } cat)
            foreach (string problem in cat.Problems) log($"pinvoke: {problem}");
    }

    public void Install(AssemblyLoadContext alc)
    {
        alc.Resolving += (ctx, name) => name.Name != null && _originals.ContainsKey(name.Name) ? Load(ctx, name.Name) : null;
        alc.ResolvingUnmanagedDll += ResolveNative;
    }

    /// <summary>The rewritten assembly <paramref name="name"/>, loaded into <paramref name="alc"/> (once).</summary>
    public Assembly? Load(AssemblyLoadContext alc, string name)
    {
        if (!_originals.TryGetValue(name, out var entry)) return null;
        lock (_lock)
        {
            foreach (var (a, _) in _loaded)
                if (string.Equals(a.GetName().Name, name, StringComparison.OrdinalIgnoreCase) && AssemblyLoadContext.GetLoadContext(a) == alc) return a;
            var sw = Stopwatch.StartNew();
            string path = entry.Path;
            var parts = entry.Parts;
            bool windows = OperatingSystem.IsWindows();
            string? cached = CachePath(name, path, parts, windows);
            MemoryStream image;
            MemoryStream? symbols;
            string what;
            if (cached != null && File.Exists(cached))
            {
                image = new MemoryStream(File.ReadAllBytes(cached));
                string pdb = Path.ChangeExtension(cached, ".pdb");
                symbols = File.Exists(pdb) ? new MemoryStream(File.ReadAllBytes(pdb)) : null;
                what = "from the cache";
            }
            else
            {
                var (img, sym, result) = RewriteServicingTheMainThread(() => ComRewriter.RewriteToMemory(path, _searchDirs, locationOnly: (parts & Parts.Location) != 0 && (parts & Parts.Com) == 0,
                    com: (parts & Parts.Com) != 0, pinvokes: (parts & Parts.PInvokes) != 0, windows: windows));
                image = img; symbols = sym;
                what = ((parts & Parts.Com) != 0 ? result.ToString() : (parts & Parts.Location) != 0 ? $"its location calls ({result.CallSites})" : "")
                    + ((parts & Parts.PInvokes) != 0 ? ((parts & (Parts.Com | Parts.Location)) != 0 ? "; " : "") + result.Win32Summary : "");
                if (cached != null) Keep(cached, image, symbols);
            }
            long rewrite = sw.ElapsedMilliseconds;
            Assembly asm;
            using (image)
            using (symbols)
                asm = symbols != null ? alc.LoadFromStream(image, symbols) : alc.LoadFromStream(image);
            Com.RegisterOriginal(asm, path);
            _loaded[asm] = path;
            _log?.Invoke($"rewriter: {name} rewritten from {path} in {rewrite} ms (loaded in {sw.ElapsedMilliseconds - rewrite} ms){(symbols != null ? ", with symbols" : "")}: {what}");
            return asm;
        }
    }

    /// <summary>
    /// The rewrite, run on a worker thread while this one -- the main thread, which loads most of an app's
    /// assemblies -- goes on doing the work other threads queue for it (WinForms publishes the drain:
    /// WinFormsWebGpu.DrainMainWork). A rewrite takes up to a second; a splash screen shown from its own
    /// thread waits on the main thread for its window and every frame, and stayed invisible through the
    /// start-up while the assemblies it was covering for were rewritten.
    /// </summary>
    private static T RewriteServicingTheMainThread<T>(Func<T> rewrite)
    {
        if (AppContext.GetData("WinFormsWebGpu.DrainMainWork") is not Action drain || !IsMainThread()) return rewrite();
        var task = Task.Run(rewrite);
        while (!task.Wait(10)) { try { drain(); } catch { } }
        return task.GetAwaiter().GetResult();
    }

    private static bool IsMainThread() => OperatingSystem.IsMacOS() && pthread_main_np() != 0;

    [DllImport("/usr/lib/libSystem.dylib")] private static extern int pthread_main_np();

    /// <summary>Where the rewritten image of <paramref name="path"/> is kept: named by everything it depends on.</summary>
    private string? CachePath(string name, string path, Parts parts, bool windows)
    {
        if (_cacheDir == null) return null;
        try
        {
            var fi = new FileInfo(path);
            string fingerprint = (parts & Parts.PInvokes) != 0 ? Win32Catalog.For(_searchDirs).Fingerprint : "";
            string key = string.Join("|", Path.GetFullPath(path), fi.Length, fi.LastWriteTimeUtc.Ticks, File.Exists(Path.ChangeExtension(path, ".pdb")),
                typeof(ComRewriter).Assembly.ManifestModule.ModuleVersionId, typeof(Com).Assembly.ManifestModule.ModuleVersionId,
                fingerprint, (int)parts, windows, Environment.GetEnvironmentVariable("WPF_D3D11_SHIM"));
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..20].ToLowerInvariant();
            return Path.Combine(_cacheDir, $"{name}-{hash}.dll");
        }
        catch (IOException) { return null; }
    }

    private void Keep(string cached, MemoryStream image, MemoryStream? symbols)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cached)!);
            // the name's other images (an older original, rewriter or implementation) go
            string stem = Path.GetFileName(cached)[..(Path.GetFileName(cached).LastIndexOf('-') + 1)];
            foreach (string old in Directory.EnumerateFiles(Path.GetDirectoryName(cached)!, stem + "*"))
                if (Path.GetFileName(old).Length == Path.GetFileName(cached).Length && !Path.GetFileNameWithoutExtension(old).Equals(Path.GetFileNameWithoutExtension(cached), StringComparison.Ordinal))
                    try { File.Delete(old); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            if (symbols != null) Write(Path.ChangeExtension(cached, ".pdb"), symbols);
            Write(cached, image);           // last: its presence says the pair is complete
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _log?.Invoke($"rewriter: could not keep {cached}: {e.Message}"); }

        static void Write(string path, MemoryStream s)
        {
            string temp = path + "." + Environment.ProcessId + ".tmp";
            using (var f = File.Create(temp)) s.WriteTo(f);
            File.Move(temp, path, overwrite: true);
        }
    }

    /// <summary>A rewritten assembly's P/Invoke: the library in the original's folder, as it would have been
    /// found from there (DllImportSearchPath.AssemblyDirectory).</summary>
    private IntPtr ResolveNative(Assembly requester, string library)
    {
        string? path;
        lock (_lock) { if (!_loaded.TryGetValue(requester, out path)) return IntPtr.Zero; }
        string dir = Path.GetDirectoryName(path)!;
        foreach (string candidate in Candidates(library))
        {
            string full = Path.Combine(dir, candidate);
            if (File.Exists(full) && NativeLibrary.TryLoad(full, out IntPtr h)) return h;
        }
        return IntPtr.Zero;
    }

    private static IEnumerable<string> Candidates(string library)
    {
        yield return library;
        string prefix = OperatingSystem.IsWindows() ? "" : "lib";
        string ext = OperatingSystem.IsWindows() ? ".dll" : OperatingSystem.IsMacOS() ? ".dylib" : ".so";
        if (!library.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) yield return library + ext;
        if (prefix.Length > 0 && !Path.GetFileName(library).StartsWith(prefix, StringComparison.Ordinal)) yield return prefix + library + ext;
    }
}
