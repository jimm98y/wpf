// What the P/Invoke rewriter knows of the Windows API (Documentation/pinvoke-rewriter.md):
//
// - every Windows export, by module, with the value it returns when it fails (win32_exports.tsv, generated
//   from the Windows API metadata by win32shim-gen and embedded in WpfMac.Win32), so a function nobody implements
//   still fails the way it would on a Windows that lacked it;
// - the stack's managed implementations, found by their attributes in the implementation assemblies'
//   metadata (Win32ExportAttribute: every call off Windows; Win32ExportOnWindowsAttribute: the calls on the
//   stack's windows, on Windows). Matched by the attributes' names, so each assembly declares its own.

using Mono.Cecil;

namespace WpfMac.ComRewrite;

public sealed class Win32Catalog
{
    /// <summary>The assemblies that implement Windows exports.</summary>
    public static readonly string[] ImplementationAssemblies = { "WpfMac.Win32", "Microsoft.Wpf.Interop.WebGpu" };

    /// <summary>A function's failure value (Generator.cs's Failure).</summary>
    public enum Failure : byte { Void = 0, Zero = 1, HResult = 2, NtStatus = 3, Win32Error = 4, InvalidHandle = 5 }

    // Win32ExportOnWindows masks: bit k argument k is a window, bit 16+k a device context, bit 15 always
    public const int Always = 1 << 15;

    private readonly HashSet<string> _modules = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, Failure>> _exports = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Failure> _anyModule = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MethodDefinition> _offWindows = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (MethodDefinition? Impl, int Mask)> _onWindows = new(StringComparer.Ordinal);
    private readonly List<ModuleDefinition> _held = new();

    /// <summary>The helpers a rewritten P/Invoke calls (WpfMac.Win32.Exports): the logged failure, and on
    /// Windows the routing questions.</summary>
    public MethodDefinition? Unimplemented { get; private set; }
    public MethodDefinition? OnStack { get; private set; }
    public MethodDefinition? FlushDC { get; private set; }

    /// <summary>The implementation assemblies' MVIDs, for a cache of what was rewritten against them.</summary>
    public string Fingerprint { get; private set; } = "";

    /// <summary>What did not load, one line each.</summary>
    public List<string> Problems { get; } = new();

    public IReadOnlyCollection<string> OffWindowsNames => _offWindows.Keys;

    private static readonly Dictionary<string, Win32Catalog> s_cache = new(StringComparer.Ordinal);

    /// <summary>The catalog over the implementation assemblies found in <paramref name="searchDirs"/> (once
    /// per process for the same files).</summary>
    public static Win32Catalog For(IEnumerable<string> searchDirs)
    {
        var paths = new List<string>();
        foreach (string name in ImplementationAssemblies)
            foreach (string d in searchDirs)
            {
                string p = Path.Combine(d, name + ".dll");
                if (File.Exists(p)) { paths.Add(Path.GetFullPath(p)); break; }
            }
        string key = string.Join("|", paths.Select(p => p + "@" + File.GetLastWriteTimeUtc(p).Ticks));
        lock (s_cache)
        {
            if (!s_cache.TryGetValue(key, out var c)) s_cache[key] = c = new Win32Catalog(paths);
            return c;
        }
    }

    /// <summary>A catalog over these implementation assemblies (tests; a build-time rewrite).</summary>
    public static Win32Catalog Load(IEnumerable<string> implementationPaths) => new(implementationPaths.Select(Path.GetFullPath));

    private Win32Catalog(IEnumerable<string> implementationPaths)
    {
        var mvids = new List<string>();
        foreach (string path in implementationPaths)
        {
            try
            {
                var resolver = new DefaultAssemblyResolver();
                resolver.AddSearchDirectory(Path.GetDirectoryName(path)!);
                var m = ModuleDefinition.ReadModule(path, new ReaderParameters { AssemblyResolver = resolver, ReadingMode = ReadingMode.Deferred, InMemory = true });
                _held.Add(m);
                mvids.Add(m.Mvid.ToString("N"));
                foreach (var t in m.Types.SelectMany(ComRewriter.AllTypes))
                    foreach (var md in t.Methods)
                        if (md.HasCustomAttributes) Index(md);
                // the helpers: public static members of a public class Exports (WpfMac.Win32's)
                foreach (var exports in m.Types.Where(t => t.Name == "Exports" && t.IsPublic))
                {
                    MethodDefinition? Find(string name) => exports.Methods.FirstOrDefault(x => x.Name == name && x.IsPublic && x.IsStatic);
                    Unimplemented ??= Find("Unimplemented");
                    OnStack ??= Find("OnStack");
                    FlushDC ??= Find("FlushDC");
                }
            }
            catch (Exception e) when (e is IOException or BadImageFormatException or AssemblyResolutionException)
            {
                Problems.Add($"{Path.GetFileName(path)}: {e.Message}");
            }
        }
        Fingerprint = string.Join(",", mvids);
        // the table is WpfMac.Win32's (it answers LoadLibrary with it too)
        var res = _held.SelectMany(m => m.Resources).OfType<EmbeddedResource>().FirstOrDefault(r => r.Name == "win32_exports.tsv");
        if (res == null) { Problems.Add("no implementation assembly carries win32_exports.tsv: no Windows export is known"); return; }
        using var r = new StreamReader(res.GetResourceStream());
        for (string? line; (line = r.ReadLine()) != null;)
        {
            var parts = line.Split('\t');
            if (parts.Length < 2) continue;
            if (parts[1] == "*") { _modules.Add(parts[0]); continue; }
            var f = parts.Length > 2 && byte.TryParse(parts[2], out byte b) ? (Failure)b : Failure.Zero;
            if (!_exports.TryGetValue(parts[0], out var byName)) _exports[parts[0]] = byName = new(StringComparer.Ordinal);
            byName[parts[1]] = f;
            _anyModule.TryAdd(parts[1], f);
        }
    }

    private void Index(MethodDefinition md)
    {
        foreach (var a in md.CustomAttributes)
        {
            string n = a.AttributeType.Name;
            if (n is not ("Win32ExportAttribute" or "Win32ExportOnWindowsAttribute")) continue;
            bool callable = md.IsStatic && md.IsPublic && Visible(md.DeclaringType) && !md.HasGenericParameters;
            if (n == "Win32ExportAttribute")
            {
                if (!callable) { Problems.Add($"{md.FullName}: not a public static method"); continue; }
                foreach (string name in Names(a, 0)) _offWindows.TryAdd(name, md);
            }
            else
            {
                int mask = Convert.ToInt32(a.ConstructorArguments[0].Value);
                // a mask with only device-context bits: no implementation, the real function after the DC's
                // pending drawing is written to it
                bool flushOnly = (mask & 0xFFFF) == 0;
                if (!flushOnly && !callable) { Problems.Add($"{md.FullName}: not a public static method"); continue; }
                foreach (string name in Names(a, 1))
                {
                    _onWindows.TryGetValue(name, out var known);
                    _onWindows[name] = (flushOnly ? known.Impl : md, known.Mask | mask);
                }
            }
        }
    }

    private static bool Visible(TypeDefinition t)
    {
        for (var x = t; x != null; x = x.DeclaringType)
            if (!(x.IsPublic || x.IsNestedPublic)) return false;
        return true;
    }

    private static IEnumerable<string> Names(CustomAttribute a, int index)
    {
        if (a.ConstructorArguments.Count <= index) yield break;
        if (a.ConstructorArguments[index].Value is CustomAttributeArgument[] arr)
            foreach (var x in arr) if (x.Value is string s) yield return s;
    }

    /// <summary>user32.dll, USER32, C:\Windows\System32\user32.dll -> user32.</summary>
    public static string Normalize(string module)
    {
        string file = Path.GetFileName(module.Replace('\\', '/').Trim());
        if (file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) file = file[..^4];
        return file.ToLowerInvariant();
    }

    /// <summary>Whether <paramref name="module"/> (normalized) is a Windows DLL.</summary>
    public bool IsWindowsModule(string module) => _modules.Contains(module) || IsApiSet(module);

    /// <summary>An API set (api-ms-win-core-com-l1-1-0, ext-ms-win-...): a contract name Windows maps to the
    /// DLL that implements it (combase, kernelbase ...), whatever version of the contract is named. The
    /// metadata files the functions under their DLL, so an import through any API set is a Windows DLL's,
    /// its functions found by name (Exports' any-module lookup).</summary>
    public static bool IsApiSet(string module) =>
        module.StartsWith("api-ms-win-", StringComparison.OrdinalIgnoreCase) || module.StartsWith("ext-ms-win-", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="module"/> exports <paramref name="name"/>; else whether some Windows
    /// DLL does (the metadata names a function's home DLL, an app may import it from one that forwards it).</summary>
    public bool Exports(string module, string name, out Failure failure)
    {
        if (_exports.TryGetValue(module, out var byName) && byName.TryGetValue(name, out failure)) return true;
        return _anyModule.TryGetValue(name, out failure);
    }

    public MethodDefinition? OffWindows(string name) => _offWindows.TryGetValue(name, out var m) ? m : null;

    public (MethodDefinition? Impl, int Mask) OnWindows(string name) => _onWindows.TryGetValue(name, out var x) ? x : default;

    /// <summary>Whether the Windows head routes <paramref name="name"/> (it has a Win32ExportOnWindows).</summary>
    public bool HasOnWindows(string name) => _onWindows.ContainsKey(name);
}
