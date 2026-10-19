// The COM-import rewrite as a step of preparing an app (Documentation/comimport-rewriter.md).
//
// 1. Judge every app assembly that declares [ComImport] types (ComRewriter.Analyze: a metadata and IL scan,
//    no code generated). Windows: all or nothing per assembly -- rewritten only when every ComImport use is
//    supported, and only together with the assemblies whose COM types it uses; else left on built-in COM.
//    Elsewhere there is no built-in COM: what is supported is rewritten, the rest throws by name.
// 2. Caller rewriting (Limit 1): a rewritten assembly has no file, and reports its original location only
//    to code whose location calls are redirected. App assemblies that ask OTHER assemblies where they are
//    (LocationCallers) and can reach a rewritten one -- it calls them, or they enumerate every assembly --
//    are rewritten too, for their location calls alone; and so on, to a fixpoint.
// 3. The chosen leave the deps, and their names and original paths go to the runtimeconfig
//    (WpfMac.ComImport): the startup hook rewrites each in memory when it is first loaded.
//
// Nothing rewritten is written to disk. The verdicts are cached, by file hash and rewriter version, in a
// small JSON file in the app's cache folder (comimport-verdicts.json) -- verdicts, not assemblies.

using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WpfMac.ComRewrite;

namespace WpfMac;

internal static class ComImportStep
{
    /// <summary>Where an earlier wpfmac kept rewritten copies on disk (now removed when found).</summary>
    public const string Folder = "comimport";
    public const string VerdictFile = "comimport-verdicts.json";
    /// <summary>Bumped when the judge's verdicts change, so cached ones are judged again.</summary>
    private const string Version = "5";

    /// <summary>WPF_COMIMPORT_REWRITE: on (default), off, or a ';'-separated list of assembly names.</summary>
    internal static Func<string, bool> Scope(string? setting)
    {
        if (string.IsNullOrWhiteSpace(setting) || setting.Equals("on", StringComparison.OrdinalIgnoreCase)) return _ => true;
        if (setting.Equals("off", StringComparison.OrdinalIgnoreCase)) return _ => false;
        var names = setting.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(n => new System.Text.RegularExpressions.Regex("^" + System.Text.RegularExpressions.Regex.Escape(n).Replace("\\*", ".*") + "$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase)).ToList();
        return name => names.Any(r => r.IsMatch(name));
    }

    private sealed class AppFile
    {
        public required string File, Name;
        /// <summary>Its entries in the deps (an assembly can be listed by more than one library).</summary>
        public List<(JsonObject Runtime, string Asset)> Entries = new();
        public string Hash = "";
        public bool DeclaresCom, MayReadLocations;
        public List<string> References = new();
        public Verdict? Verdict;
        public LocationAsks? Asks;
    }

    public sealed class Verdict
    {
        public int Unsupported { get; set; }
        public List<string> UsesComTypesOf { get; set; } = new();
        public string Summary { get; set; } = "";
    }

    public sealed class AsksEntry
    {
        public bool AsksOthers { get; set; }
        public bool EnumeratesAll { get; set; }
        public string? Example { get; set; }
    }

    private sealed class Cache
    {
        public string Version { get; set; } = "";
        public Dictionary<string, Verdict> Verdicts { get; set; } = new();
        public Dictionary<string, AsksEntry> Asks { get; set; } = new();
    }

    /// <summary>
    /// Chooses what is rewritten among the app's runtime assemblies in <paramref name="deps"/>, takes those out
    /// of it, and lists them ("name|original path") in <paramref name="list"/>. One line per assembly judged
    /// goes to <paramref name="notes"/>. <paramref name="appCache"/>: the app's cache folder (the verdicts).
    /// </summary>
    public static void Apply(JsonObject deps, string appDir, string overlayDir, string cacheRoot, string? appCache, bool windows, string? setting, List<string> notes, List<string> list)
    {
        var total = System.Diagnostics.Stopwatch.StartNew();
        // what an earlier wpfmac wrote: rewritten copies on disk (now made in memory, at load)
        string old = Path.Combine(cacheRoot, Folder);
        if (Directory.Exists(old)) { try { Directory.Delete(old, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        var inScope = Scope(setting);
        var force = Scope(Environment.GetEnvironmentVariable("WPF_COMIMPORT_FORCE") ?? "off");
        string targetName = deps["runtimeTarget"]?["name"]?.GetValue<string>() ?? "";
        if (deps["targets"]?[targetName] is not JsonObject target) return;
        string[] searchDirs = { appDir, overlayDir, System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory() };
        bool partial = !windows;

        // the app's assemblies, from their metadata
        var files = new List<AppFile>();
        foreach (var (libKey, libNode) in target.ToList())
        {
            if (libKey == DepsMerger.OverlayLibrary || libKey.StartsWith("WpfMac.", StringComparison.Ordinal)) continue;      // the stack's own: never
            if (libNode is not JsonObject lib || lib["runtime"] is not JsonObject runtime) continue;
            foreach (var (asset, props) in runtime.ToList())
            {
                string file = Path.Combine(appDir, asset);
                if (!File.Exists(file)) continue;
                var known = files.FirstOrDefault(x => string.Equals(x.File, file, StringComparison.OrdinalIgnoreCase));
                if (known != null) { known.Entries.Add((runtime, asset)); continue; }
                var f = new AppFile { File = file, Name = Path.GetFileNameWithoutExtension(file) };
                f.Entries.Add((runtime, asset));
                if (!ReadMetadata(f)) continue;
                if (files.Any(x => string.Equals(x.Name, f.Name, StringComparison.OrdinalIgnoreCase))) continue;     // the same assembly twice: the first
                files.Add(f);
            }
        }
        var candidates = files.Where(f => f.DeclaresCom).ToList();
        if (candidates.Count == 0) return;
        foreach (var c in candidates.Where(c => !inScope(c.Name))) notes.Add($"comimport {c.Name}: left (WPF_COMIMPORT_REWRITE)");
        candidates = candidates.Where(c => inScope(c.Name)).ToList();

        // the cached verdicts
        string? cachePath = appCache == null ? null : Path.Combine(appCache, VerdictFile);
        var cache = Load(cachePath);
        bool dirty = false;

        // what of each candidate the other app assemblies reference by name
        var external = ExternalReferences(files.Select(f => f.File), candidates.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase));

        // 1. judge
        int judged = 0;
        foreach (var c in candidates)
        {
            var refs = external.TryGetValue(c.Name, out var r) ? r : new HashSet<string>();
            string key = $"{c.Hash}|{HashText(string.Join("\n", refs.Order(StringComparer.Ordinal)))}|{(partial ? "p" : "w")}";
            if (cache.Verdicts.TryGetValue(key, out var v)) { c.Verdict = v; continue; }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var res = ComRewriter.Analyze(c.File, searchDirs, refs);
                c.Verdict = new Verdict { Unsupported = res.Unsupported.Count, UsesComTypesOf = res.UsesComTypesOf.ToList(), Summary = res.ToString() };
            }
            catch (Exception e) { c.Verdict = new Verdict { Unsupported = 1, Summary = $"the rewriter failed: {e.GetType().Name}: {e.Message}" }; }
            c.Verdict.Summary += $" [judged in {sw.ElapsedMilliseconds} ms]";
            cache.Verdicts[key] = c.Verdict;
            dirty = true;
            judged++;
        }

        // Windows: rewritten together or not at all -- an assembly whose COM types another uses goes with it
        var chosen = candidates.Where(c => partial || c.Verdict!.Unsupported == 0 || force(c.Name)).ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        if (!partial)
        {
            for (bool changed = true; changed;)
            {
                changed = false;
                foreach (var c in chosen.Values.ToList())
                    if (!force(c.Name) && c.Verdict!.UsesComTypesOf.Any(u => !chosen.ContainsKey(u))) { chosen.Remove(c.Name); changed = true; }
            }
        }

        // 2. caller rewriting: who asks the chosen (and, in turn, the callers) where they are
        var callers = new Dictionary<string, AppFile>(StringComparer.OrdinalIgnoreCase);
        if (chosen.Count > 0)
        {
            foreach (var f in files.Where(f => f.MayReadLocations))
            {
                if (cache.Asks.TryGetValue(f.Hash, out var a)) { f.Asks = new LocationAsks(a.AsksOthers, a.EnumeratesAll, a.Example); continue; }
                try { f.Asks = LocationCallers.Analyze(f.File); }
                catch (Exception) { f.Asks = new LocationAsks(true, false, "its IL could not be read"); }
                cache.Asks[f.Hash] = new AsksEntry { AsksOthers = f.Asks.AsksOthers, EnumeratesAll = f.Asks.EnumeratesAll, Example = f.Asks.Example };
                dirty = true;
            }
            var askers = files.Where(f => f.Asks is { AsksOthers: true }).ToList();
            var rewritten = new HashSet<string>(chosen.Keys, StringComparer.OrdinalIgnoreCase);
            for (bool grew = true; grew;)
            {
                grew = false;
                foreach (var x in askers)
                {
                    if (rewritten.Contains(x.Name)) continue;
                    // x can be asked to tell where a rewritten R is when R calls it, or when x enumerates all
                    var reachedBy = files.Where(rf => rewritten.Contains(rf.Name) && rf.References.Contains(x.Name, StringComparer.OrdinalIgnoreCase)).Select(rf => rf.Name).ToList();
                    if (reachedBy.Count == 0 && !x.Asks!.EnumeratesAll) continue;
                    rewritten.Add(x.Name);
                    callers[x.Name] = x;
                    grew = true;
                    notes.Add($"comimport {x.Name}: its location calls rewritten in memory at load -- it asks other assemblies where they are ({x.Asks!.Example}){(reachedBy.Count > 0 ? $", and {string.Join(", ", reachedBy)} call it" : ", and it goes through every loaded assembly")}");
                }
            }
        }

        // 3. the chosen and their callers leave the deps; the startup hook loads them rewritten
        foreach (var c in candidates)
        {
            bool rewrite = chosen.ContainsKey(c.Name);
            string why = c.Verdict!.Summary + (!rewrite && c.Verdict.Unsupported == 0 && c.Verdict.UsesComTypesOf.Count > 0 ? " (left with them)" : "");
            if (rewrite) Take(c, list);
            notes.Add($"comimport {c.Name}: {(rewrite ? "rewritten in memory at load" : "left on built-in COM")} -- {why}");
        }
        foreach (var x in callers.Values) Take(x, list, locationOnly: !chosen.ContainsKey(x.Name));
        foreach (var (_, libNode) in target)
            if (libNode is JsonObject lib && lib["runtime"] is JsonObject rt && rt.Count == 0) lib.Remove("runtime");

        if (dirty && cachePath != null)
        {
            try { Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!); File.WriteAllText(cachePath, JsonSerializer.Serialize(cache)); }
            catch (IOException) { }
        }
        notes.Add($"comimport: {files.Count} app assemblies read, {candidates.Count} declare COM types, {judged} judged now (the rest cached), " +
                  $"{chosen.Count} rewritten for COM, {callers.Count} for their location calls; {total.ElapsedMilliseconds} ms");
    }

    private static void Take(AppFile f, List<string> list, bool locationOnly = false)
    {
        foreach (var (runtime, asset) in f.Entries) runtime.Remove(asset);
        // a caller alone keeps its COM types as they are, for built-in COM
        list.Add($"{f.Name}|{f.File}{(locationOnly ? "|location" : "")}");
    }

    /// <summary>The file's hash, what it references, whether it declares COM types and may read locations.</summary>
    private static bool ReadMetadata(AppFile f)
    {
        try
        {
            byte[] bytes = File.ReadAllBytes(f.File);
            f.Hash = Convert.ToHexString(SHA256.HashData(bytes))[..24].ToLowerInvariant();
            using var pe = new PEReader(new MemoryStream(bytes));
            if (!pe.HasMetadata) return false;
            var md = pe.GetMetadataReader();
            if (!md.IsAssembly) return false;
            f.Name = md.GetString(md.GetAssemblyDefinition().Name);
            foreach (var h in md.TypeDefinitions)
                if ((md.GetTypeDefinition(h).Attributes & System.Reflection.TypeAttributes.Import) != 0) { f.DeclaresCom = true; break; }
            f.MayReadLocations = LocationCallers.MayRead(md);
            f.References = md.AssemblyReferences.Select(h => md.GetString(md.GetAssemblyReference(h).Name)).ToList();
            return true;
        }
        catch (Exception e) when (e is BadImageFormatException or IOException or InvalidOperationException) { return false; }
    }

    private static Cache Load(string? path)
    {
        if (path != null && File.Exists(path))
        {
            try
            {
                var c = JsonSerializer.Deserialize<Cache>(File.ReadAllText(path));
                if (c != null && c.Version == Version) return c;
            }
            catch (JsonException) { }
        }
        return new Cache { Version = Version };
    }

    /// <summary>For each of <paramref name="targets"/>: the names of its types (Namespace.Name, or Name for a
    /// nested one) that the other files reference.</summary>
    private static Dictionary<string, HashSet<string>> ExternalReferences(IEnumerable<string> files, HashSet<string> targets)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (string f in files)
        {
            try
            {
                using var fs = File.OpenRead(f);
                using var pe = new PEReader(fs);
                if (!pe.HasMetadata) continue;
                var md = pe.GetMetadataReader();
                foreach (var h in md.TypeReferences)
                {
                    var tr = md.GetTypeReference(h);
                    string name = md.GetString(tr.Name);
                    string ns = md.GetString(tr.Namespace);
                    var scope = tr.ResolutionScope;
                    bool nested = false;
                    while (scope.Kind == HandleKind.TypeReference) { nested = true; scope = md.GetTypeReference((TypeReferenceHandle)scope).ResolutionScope; }
                    if (scope.Kind != HandleKind.AssemblyReference) continue;
                    string asm = md.GetString(md.GetAssemblyReference((AssemblyReferenceHandle)scope).Name);
                    if (!targets.Contains(asm)) continue;
                    if (!result.TryGetValue(asm, out var set)) result[asm] = set = new HashSet<string>(StringComparer.Ordinal);
                    set.Add(nested || ns.Length == 0 ? name : ns + "." + name);
                }
            }
            catch (BadImageFormatException) { }
        }
        return result;
    }

    private static string HashText(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..8].ToLowerInvariant();
}
