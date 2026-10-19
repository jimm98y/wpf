// Loads the app assemblies the launcher chose to rewrite, rewritten in memory (Documentation/
// comimport-rewriter.md). The launcher leaves them out of the deps, so the first request for one reaches
// the load context's Resolving event; the original's bytes are rewritten with Cecil and loaded with
// LoadFromStream (with symbols when a .pdb lies beside it). Nothing is written to disk.
//
// The rewritten assembly's Location, CodeBase and Module names answer with the original file's
// (WpfMac.ComInterop.Com.RegisterOriginal), and its P/Invokes find native libraries in the original's
// folder (ResolvingUnmanagedDll), as an assembly loaded from that folder would.

using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using WpfMac.ComInterop;

namespace WpfMac.ComRewrite;

public sealed class ComImportLoader
{
    private readonly Dictionary<string, string> _originals;          // assembly name -> original file
    private readonly string[] _searchDirs;
    private readonly Action<string>? _log;
    private readonly Dictionary<Assembly, string> _loaded = new();
    private readonly object _lock = new();

    /// <param name="originals">assembly name -> its original file</param>
    /// <param name="searchDirs">where the rewriter resolves the assemblies' references</param>
    public ComImportLoader(IDictionary<string, string> originals, IEnumerable<string> searchDirs, Action<string>? log)
    {
        _originals = new Dictionary<string, string>(originals, StringComparer.OrdinalIgnoreCase);
        _searchDirs = searchDirs.ToArray();
        _log = log;
    }

    /// <summary>The launcher's list, as it passes it (WpfMac.ComImport: "name|path" lines).</summary>
    public static Dictionary<string, string> Parse(string? list)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(list)) return d;
        foreach (string line in list.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int bar = line.IndexOf('|');
            if (bar > 0) d[line[..bar]] = line[(bar + 1)..];
        }
        return d;
    }

    /// <summary>The startup hook's entry (reached by name): the launcher's list, into the default context.</summary>
    public static void InstallDefault(string list, string appDir, string overlayDir, Action<string> log)
    {
        var loader = new ComImportLoader(Parse(list), new[] { appDir, overlayDir, RuntimeEnvironment.GetRuntimeDirectory() }, log);
        loader.Install(AssemblyLoadContext.Default);
        log($"comimport: {loader._originals.Count} app assemblies are rewritten in memory when first loaded: {string.Join(", ", loader._originals.Keys)}");
    }

    public void Install(AssemblyLoadContext alc)
    {
        alc.Resolving += (ctx, name) => name.Name != null && _originals.ContainsKey(name.Name) ? Load(ctx, name.Name) : null;
        alc.ResolvingUnmanagedDll += ResolveNative;
    }

    /// <summary>The rewritten assembly <paramref name="name"/>, loaded into <paramref name="alc"/> (once).</summary>
    public Assembly? Load(AssemblyLoadContext alc, string name)
    {
        if (!_originals.TryGetValue(name, out string? path)) return null;
        lock (_lock)
        {
            foreach (var (a, _) in _loaded)
                if (string.Equals(a.GetName().Name, name, StringComparison.OrdinalIgnoreCase) && AssemblyLoadContext.GetLoadContext(a) == alc) return a;
            var sw = Stopwatch.StartNew();
            var (image, symbols, result) = ComRewriter.RewriteToMemory(path, _searchDirs);
            long rewrite = sw.ElapsedMilliseconds;
            Assembly asm;
            using (image)
            using (symbols)
                asm = symbols != null ? alc.LoadFromStream(image, symbols) : alc.LoadFromStream(image);
            Com.RegisterOriginal(asm, path);
            _loaded[asm] = path;
            _log?.Invoke($"comimport: {name} rewritten in memory from {path} in {rewrite} ms (loaded in {sw.ElapsedMilliseconds - rewrite} ms){(symbols != null ? ", with symbols" : "")}: {result}");
            return asm;
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
