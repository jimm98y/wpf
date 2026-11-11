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
// (WpfWebGpu.ComInterop.Com.RegisterOriginal), and its P/Invokes find native libraries in the original's
// folder (ResolvingUnmanagedDll), as an assembly loaded from that folder would.

using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using WpfWebGpu.ComInterop;

namespace WpfWebGpu.ComRewrite;

public sealed class ComImportLoader
{
    /// <summary>What of an assembly is rewritten.</summary>
    [Flags]
    public enum Parts { None = 0, Com = 1, Location = 2, PInvokes = 4, Paths = 8, Machine = 16 }

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

    /// <summary>The launcher's list, as it passes it (WpfWebGpu.ComImport): "name|path|parts" lines, parts a
    /// comma-separated list of com, location, pinvoke, paths, machine ("name|path" alone: com).</summary>
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
                what |= p switch { "com" => Parts.Com, "location" => Parts.Location, "pinvoke" => Parts.PInvokes, "paths" => Parts.Paths, "machine" => Parts.Machine, _ => Parts.None };
            d[parts[0]] = (parts[1], what);
        }
        return d;
    }

    /// <summary>The startup hook's entry (reached by name): the launcher's list, into the default context.</summary>
    public static void InstallDefault(string list, string appDir, string overlayDir, string? cacheDir, Action<string> log)
    {
        var loader = new ComImportLoader(Parse(list), new[] { appDir, overlayDir, RuntimeEnvironment.GetRuntimeDirectory() }, RewrittenDir(cacheDir), log);
        loader.Install(AssemblyLoadContext.Default);
        log($"rewriter: {loader._originals.Count} app assemblies are rewritten when first loaded: {string.Join(", ", loader._originals.Select(kv => $"{kv.Key} ({kv.Value.Parts})"))}");
        if (loader._originals.Values.Any(v => (v.Parts & Parts.PInvokes) != 0) && Win32Catalog.For(loader._searchDirs) is { Problems.Count: > 0 } cat)
            foreach (string problem in cat.Problems) log($"pinvoke: {problem}");
    }

    /// <summary>Where rewritten images are kept: beside the app's other prepared files (wpfrun's cache, apps/&lt;app&gt;),
    /// in the cache's own rewritten/ every app run from it shares -- an installation's services carry the same
    /// libraries, and an image is named by what it is made from, not by whose folder it came from.</summary>
    private static string? RewrittenDir(string? cacheDir)
    {
        if (cacheDir == null) return null;
        string full = Path.GetFullPath(cacheDir).TrimEnd(Path.DirectorySeparatorChar);
        string? apps = Path.GetDirectoryName(full);
        string rewritten = apps != null && Path.GetFileName(apps) == "apps" && Path.GetDirectoryName(apps) is { } root
            ? Path.Combine(root, "rewritten")
            : Path.Combine(cacheDir, "rewritten");
        return Generation(rewritten);
    }

    // Generations: every image is named by the rewriter and the stack it was made with, so a new build of either
    // makes all of them anew, and what the previous one made is never read again -- each kept a gigabyte, upgrade
    // after upgrade. The images of one build are kept in a folder of its own, marked when a process uses it, and a
    // generation no process has used for a week goes (an older build still running somewhere marks its own).
    private static readonly TimeSpan s_unusedGeneration = TimeSpan.FromDays(7);

    private static string Generation(string rewritten)
    {
        string key = $"{typeof(ComRewriter).Assembly.ManifestModule.ModuleVersionId}|{typeof(Com).Assembly.ManifestModule.ModuleVersionId}";
        string generation = Path.Combine(rewritten, "g-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..12].ToLowerInvariant());
        try
        {
            Directory.CreateDirectory(generation);
            Directory.SetLastWriteTimeUtc(generation, DateTime.UtcNow);
            Prune(rewritten, generation, DateTime.UtcNow - s_unusedGeneration);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return generation;
    }

    /// <summary>Deletes what in <paramref name="rewritten"/> is not <paramref name="current"/> and was last used before
    /// <paramref name="before"/>: other generations, and images kept before there were generations.</summary>
    internal static void Prune(string rewritten, string current, DateTime before)
    {
        foreach (string entry in Directory.EnumerateFileSystemEntries(rewritten))
        {
            if (string.Equals(entry, current, StringComparison.Ordinal)) continue;
            try
            {
                if (Directory.Exists(entry))
                {
                    if (Directory.GetLastWriteTimeUtc(entry) < before) Directory.Delete(entry, recursive: true);
                }
                else if (File.GetLastWriteTimeUtc(entry) < before) File.Delete(entry);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
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
            // another process making the same image (an installation's services start together): its result waited
            // for, not made again
            using var making = cached == null || File.Exists(cached) ? null : Making(cached);
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
                    com: (parts & Parts.Com) != 0, pinvokes: (parts & Parts.PInvokes) != 0, windows: windows, paths: (parts & Parts.Paths) != 0));
                image = img; symbols = sym;
                what = ((parts & Parts.Com) != 0 ? result.ToString() : (parts & Parts.Location) != 0 ? $"its location calls ({result.CallSites})" : "")
                    + ((parts & Parts.PInvokes) != 0 ? ((parts & (Parts.Com | Parts.Location)) != 0 ? "; " : "") + result.Win32Summary : "")
                    + ((parts & Parts.Paths) != 0 ? ((parts & ~Parts.Paths) != 0 ? "; " : "") + result.PathSummary : "")
                    + (result.Retargeted ? (((parts & ~Parts.Machine) != 0) ? "; " : "") + "built for the other processor, loaded as AnyCPU" : "");
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

    /// <summary>Where the rewritten image of <paramref name="path"/> is kept: named by everything it is made from --
    /// the assembly (its module version id: the same build in any folder), the assemblies it references as they are
    /// found from it, the rewriter, the stack's implementation and what is rewritten -- and by nothing else, so
    /// that the copies of a library an installation's programs each carry share one image.</summary>
    private string? CachePath(string name, string path, Parts parts, bool windows)
    {
        if (_cacheDir == null) return null;
        try
        {
            string fingerprint = (parts & Parts.PInvokes) != 0 ? Win32Catalog.For(_searchDirs).Fingerprint : "";
            var (mvid, references) = Identity(path);
            var resolved = references.Select(r => r + "=" + (Find(r) is { } file ? Identity(file).Mvid : "-")).Order(StringComparer.Ordinal);
            string key = string.Join("|", name, mvid, string.Join(",", resolved), File.Exists(Path.ChangeExtension(path, ".pdb")),
                typeof(ComRewriter).Assembly.ManifestModule.ModuleVersionId, typeof(Com).Assembly.ManifestModule.ModuleVersionId,
                fingerprint, (int)parts, windows, Environment.GetEnvironmentVariable("WPF_D3D11_SHIM"));
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..20].ToLowerInvariant();
            return Path.Combine(_cacheDir, $"{name}-{hash}.dll");
        }
        catch (Exception e) when (e is IOException or BadImageFormatException or InvalidOperationException) { return null; }
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Length, long Written, string Mvid, string[] References)> s_identities = new();

    // an assembly's module version id and the names of the assemblies it references (its metadata alone, kept per file)
    private static (string Mvid, string[] References) Identity(string path)
    {
        var fi = new FileInfo(path);
        if (s_identities.TryGetValue(path, out var known) && known.Length == fi.Length && known.Written == fi.LastWriteTimeUtc.Ticks)
            return (known.Mvid, known.References);
        using var fs = File.OpenRead(path);
        using var pe = new System.Reflection.PortableExecutable.PEReader(fs);
        var md = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
        string mvid = md.GetGuid(md.GetModuleDefinition().Mvid).ToString("N");
        string[] references = md.AssemblyReferences.Select(h => md.GetString(md.GetAssemblyReference(h).Name)).ToArray();
        s_identities[path] = (fi.Length, fi.LastWriteTimeUtc.Ticks, mvid, references);
        return (mvid, references);
    }

    // a referenced assembly as the rewriter finds it
    private string? Find(string assembly)
    {
        foreach (string dir in _searchDirs)
        {
            string candidate = Path.Combine(dir, assembly + ".dll");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>The image's lock, held while it is made: another process asking for it waits, then reads it.</summary>
    private static FileStream? Making(string cached)
    {
        string lockPath = cached + ".lock";
        try { Directory.CreateDirectory(Path.GetDirectoryName(cached)!); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromMinutes(5))
        {
            try { return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose); }
            catch (IOException) { Thread.Sleep(50); }
            catch (UnauthorizedAccessException) { return null; }
        }
        return null;                                // made as if nobody else were making it
    }

    private void Keep(string cached, MemoryStream image, MemoryStream? symbols)
    {
        try
        {
            // the name's other images stay: in the shared cache they are other programs' (another build, other
            // references), still in use
            Directory.CreateDirectory(Path.GetDirectoryName(cached)!);
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
