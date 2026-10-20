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
    /// <summary>The verdicts' version: the step's own, and the rewriter build that judged them (its module's
    /// MVID, new with every build), so a changed rewriter never reuses an old verdict.</summary>
    private static readonly string Version = "7/" + typeof(WpfMac.ComRewrite.ComRewriter).Assembly.ManifestModule.ModuleVersionId.ToString("N");

    /// <summary>Assemblies the judge finds rewritable whose first rewrite on Windows waits for its own gate
    /// (an app run that exercises them; the smoke runs do not): left on built-in COM, with the gate named.
    /// An entry leaves this list, in its own commit, when its gate passes. WPF_COMIMPORT_FORCE overrides it.</summary>
    internal static readonly IReadOnlyDictionary<string, string> AwaitingGate = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Telerik.Windows.Controls.FileDialogs"] = "a Telerik file dialog opened and cancelled (app File > Open)",
        ["Telerik.Windows.Controls"] = "app and Workbench smoke runs with it rewritten, and a Telerik dialog",
        ["MediaFoundation"] = "a app display playing a video through Telerik's media player (MF.Net, not SharpMediaFoundationInterop)",
        ["Telerik.Windows.MediaFoundation"] = "with MediaFoundation: a app display playing a video",
        ["Telerik.Windows.Controls.RibbonView"] = "app and Workbench smoke runs with it rewritten (the ribbon), and a ribbon drag and drop",
        ["IcoServices"] = "Workbench with it rewritten, and its Active Directory picker",
    };

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
        /// <summary>Windows: the assemblies using its COM types whose references to them were not held against it
        /// (they are rewritten with it or it is left): if one of them is left, so is it.</summary>
        public List<string> Trusts = new();
        /// <summary>Ways it could make built-in RCWs of its own (it declares no COM types): Marshal's RCW makers,
        /// Type.GetTypeFromCLSID/ProgID, a P/Invoke passing a delegate; and the assemblies whose types its
        /// P/Invokes' signatures name.</summary>
        public bool RcwApis, PInvokeDelegates;
        public HashSet<string> PInvokeScopes = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>It declares no COM types but makes RCWs and references COM-declaring assemblies: judged (forced)
        /// as a member of their group, rewritten only with them.</summary>
        public bool Forced;
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
        // the group's other members: assemblies that use the candidates' types and can make RCWs of their own (a
        // Marshal RCW maker, GetTypeFromCLSID, a P/Invoke with COM types): rewritten with them, so that what they
        // make are ComObjects too
        {
            var comNames0 = candidates.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var f in files)
                if (!f.DeclaresCom && inScope(f.Name) && f.References.Any(comNames0.Contains)
                    && (f.RcwApis || f.PInvokeDelegates || f.PInvokeScopes.Overlaps(comNames0)))
                { f.Forced = true; candidates.Add(f); }
        }

        // the cached verdicts
        string? cachePath = appCache == null ? null : Path.Combine(appCache, VerdictFile);
        var cache = Load(cachePath);
        bool dirty = false;

        // what of each candidate the other app assemblies reference by name
        var external = ExternalReferences(files, candidates.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase));
        // a referrer that declares no COM types and has no way to make built-in RCWs only ever holds the
        // rewritten assemblies' own objects: its references do not count against them
        var byName = files.GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var comNames = candidates.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool Clean(string referrer) => byName.TryGetValue(referrer, out var r) && !r.DeclaresCom && !r.RcwApis && !r.PInvokeDelegates && !r.PInvokeScopes.Overlaps(comNames);
        HashSet<string> RefsOf(AppFile c, ICollection<string>? except) =>
            !external.TryGetValue(c.Name, out var byReferrer) ? new HashSet<string>(StringComparer.Ordinal)
            : byReferrer.Where(kv => (except == null || !except.Contains(kv.Key)) && (partial || !Clean(kv.Key))).SelectMany(kv => kv.Value).ToHashSet(StringComparer.Ordinal);

        // 1. judge
        int judged = 0;
        void Judge(AppFile c, HashSet<string> refs)
        {
            string key = $"{c.Hash}|{HashText(string.Join("\n", refs.Order(StringComparer.Ordinal)))}|{(partial ? "p" : "w")}{(c.Forced ? "f" : "")}";
            if (cache.Verdicts.TryGetValue(key, out var v)) { c.Verdict = v; return; }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var res = ComRewriter.Analyze(c.File, searchDirs, refs, c.Forced);
                c.Verdict = new Verdict { Unsupported = res.Unsupported.Count, UsesComTypesOf = res.UsesComTypesOf.ToList(), Summary = res.ToString() };
            }
            catch (Exception e) { c.Verdict = new Verdict { Unsupported = 1, Summary = $"the rewriter failed: {e.GetType().Name}: {e.Message}" }; }
            c.Verdict.Summary += $" [judged in {sw.ElapsedMilliseconds} ms]";
            cache.Verdicts[key] = c.Verdict;
            dirty = true;
            judged++;
        }
        foreach (var c in candidates) Judge(c, RefsOf(c, null));
        // Windows: a COM interface other assemblies use may receive their built-in RCWs -- but not from an
        // assembly that uses its COM types, which is rewritten with it or left with it (the fixpoint below);
        // judged again without those references, trusting them
        if (!partial)
            foreach (var c in candidates)
            {
                var users = candidates.Where(u => u != c && u.Verdict!.UsesComTypesOf.Contains(c.Name, StringComparer.OrdinalIgnoreCase)).Select(u => u.Name).ToList();
                if (users.Count == 0 || c.Verdict!.Unsupported == 0) continue;
                var before = c.Verdict;
                Judge(c, RefsOf(c, users));
                if (c.Verdict!.Unsupported < before.Unsupported) c.Trusts = users;
                else c.Verdict = before;
            }

        // Windows: rewritten together or not at all -- an assembly whose COM types another uses goes with it
        bool Held(AppFile c) => !partial && !force(c.Name) && AwaitingGate.ContainsKey(c.Name);
        var chosen = candidates.Where(c => (partial || c.Verdict!.Unsupported == 0 || force(c.Name)) && !Held(c) && !(c.Forced && c.Verdict!.UsesComTypesOf.Count == 0)).ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        if (!partial)
        {
            for (bool changed = true; changed;)
            {
                changed = false;
                foreach (var c in chosen.Values.ToList())
                    if (!force(c.Name) && (c.Verdict!.UsesComTypesOf.Any(u => !chosen.ContainsKey(u)) || c.Trusts.Any(u => !chosen.ContainsKey(u))
                        || (c.Forced && c.Verdict.UsesComTypesOf.Count == 0)))
                    { chosen.Remove(c.Name); changed = true; }
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
            string why = c.Verdict!.Summary + (c.Trusts.Count > 0 ? $" (its interfaces' users {string.Join(", ", c.Trusts)} go with it)" : "") + (!rewrite && c.Verdict.Unsupported == 0 && Held(c) ? $" (rewritable, awaiting its gate: {AwaitingGate[c.Name]})"
                : !rewrite && c.Verdict.Unsupported == 0 && c.Verdict.UsesComTypesOf.Count > 0 ? " (left with them)" : "");
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
            ScanRcwMakers(md, f);
            return true;
        }
        catch (Exception e) when (e is BadImageFormatException or IOException or InvalidOperationException) { return false; }
    }

    private static readonly HashSet<string> s_rcwMarshal = new(StringComparer.Ordinal)
    {
        "GetObjectForIUnknown", "GetTypedObjectForIUnknown", "GetUniqueObjectForIUnknown", "GetObjectForNativeVariant",
        "GetObjectsForNativeVariants", "CreateWrapperOfType", "BindToMoniker", "GetActiveObject", "GetDelegateForFunctionPointer",
        "GetFunctionPointerForDelegate",
    };

    /// <summary>How <paramref name="f"/> could make built-in RCWs without declaring COM types (AppFile.RcwApis, ...).</summary>
    private static void ScanRcwMakers(MetadataReader md, AppFile f)
    {
        foreach (var h in md.MemberReferences)
        {
            var mr = md.GetMemberReference(h);
            if (mr.Parent.Kind != HandleKind.TypeReference) continue;
            var tr = md.GetTypeReference((TypeReferenceHandle)mr.Parent);
            string type = md.GetString(tr.Namespace) + "." + md.GetString(tr.Name), name = md.GetString(mr.Name);
            if ((type == "System.Runtime.InteropServices.Marshal" && s_rcwMarshal.Contains(name))
                || (type == "System.Type" && name is "GetTypeFromCLSID" or "GetTypeFromProgID"))
            { f.RcwApis = true; break; }
        }
        var provider = new ScopeCollector(md);
        foreach (var h in md.MethodDefinitions)
        {
            var m = md.GetMethodDefinition(h);
            if ((m.Attributes & System.Reflection.MethodAttributes.PinvokeImpl) == 0) continue;
            try { m.DecodeSignature(provider, null); }
            catch (BadImageFormatException) { f.PInvokeDelegates = true; }
        }
        f.PInvokeScopes.UnionWith(provider.Scopes);
        if (provider.Delegates) f.PInvokeDelegates = true;
    }

    /// <summary>A signature's types: the assemblies of those it references, and whether one is a delegate of
    /// the file's own (a callback native code may call with interface pointers).</summary>
    private sealed class ScopeCollector : ISignatureTypeProvider<int, object?>
    {
        private readonly MetadataReader _md;
        public readonly HashSet<string> Scopes = new(StringComparer.OrdinalIgnoreCase);
        public bool Delegates;
        public ScopeCollector(MetadataReader md) { _md = md; }
        public int GetTypeFromReference(MetadataReader r, TypeReferenceHandle h, byte k)
        {
            var scope = r.GetTypeReference(h).ResolutionScope;
            while (scope.Kind == HandleKind.TypeReference) scope = r.GetTypeReference((TypeReferenceHandle)scope).ResolutionScope;
            if (scope.Kind == HandleKind.AssemblyReference) Scopes.Add(r.GetString(r.GetAssemblyReference((AssemblyReferenceHandle)scope).Name));
            return 0;
        }
        public int GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle h, byte k)
        {
            var b = r.GetTypeDefinition(h).BaseType;
            if (b.Kind == HandleKind.TypeReference && r.GetString(r.GetTypeReference((TypeReferenceHandle)b).Name) is "MulticastDelegate" or "Delegate") Delegates = true;
            return 0;
        }
        public int GetTypeFromSpecification(MetadataReader r, object? c, TypeSpecificationHandle h, byte k) => r.GetTypeSpecification(h).DecodeSignature(this, c);
        public int GetPrimitiveType(PrimitiveTypeCode t) => 0;
        public int GetSZArrayType(int e) => 0;
        public int GetArrayType(int e, ArrayShape s) => 0;
        public int GetByReferenceType(int e) => 0;
        public int GetPointerType(int e) => 0;
        public int GetPinnedType(int e) => 0;
        public int GetGenericInstantiation(int g, System.Collections.Immutable.ImmutableArray<int> a) => 0;
        public int GetGenericMethodParameter(object? c, int i) => 0;
        public int GetGenericTypeParameter(object? c, int i) => 0;
        public int GetFunctionPointerType(MethodSignature<int> s) => 0;
        public int GetModifiedType(int m, int u, bool r) => 0;
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

    /// <summary>For each of <paramref name="targets"/>, by referencing assembly: the names of its types
    /// (Namespace.Name, or Name for a nested one) that the other files reference.</summary>
    private static Dictionary<string, Dictionary<string, HashSet<string>>> ExternalReferences(IEnumerable<AppFile> files, HashSet<string> targets)
    {
        var result = new Dictionary<string, Dictionary<string, HashSet<string>>>(StringComparer.OrdinalIgnoreCase);
        foreach (var af in files)
        {
            string f = af.File;
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
                    if (!result.TryGetValue(asm, out var byReferrer)) result[asm] = byReferrer = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                    if (!byReferrer.TryGetValue(af.Name, out var set)) byReferrer[af.Name] = set = new HashSet<string>(StringComparer.Ordinal);
                    set.Add(nested || ns.Length == 0 ? name : ns + "." + name);
                }
            }
            catch (BadImageFormatException) { }
        }
        return result;
    }

    private static string HashText(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..8].ToLowerInvariant();
}
