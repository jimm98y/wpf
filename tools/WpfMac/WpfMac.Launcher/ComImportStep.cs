// The COM-import rewrite as a step of preparing an app (Documentation/comimport-rewriter.md): every app
// assembly that declares [ComImport] types is judged, rewritten into the cache when it may be, and the
// merged deps.json then names the rewritten copy instead of the app's file.
//
// Windows: all or nothing per assembly -- rewritten only when the rewriter supports every ComImport use
// in it, and only together with the assemblies whose COM types it uses (their objects must be
// ComObjects too); else left on built-in COM. Elsewhere there is no built-in COM: what is supported is
// rewritten, and the rest throws by name when called.

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

    private sealed class Candidate
    {
        public required string File, Name, Asset;
        public required JsonObject Runtime;
        public required JsonNode? Props;
        public Verdict Verdict = new();
    }

    private sealed class Verdict
    {
        public int Unsupported { get; set; }
        public List<string> UsesComTypesOf { get; set; } = new();
        public string Summary { get; set; } = "";
    }

    /// <summary>
    /// Rewrites what may be rewritten among the app's runtime assemblies in <paramref name="deps"/> and
    /// points the deps at the copies under <paramref name="cacheRoot"/>/comimport. One line per ComImport
    /// assembly goes to <paramref name="notes"/>.
    /// </summary>
    public static void Apply(JsonObject deps, string appDir, string overlayDir, string cacheRoot, bool windows, string? setting, List<string> notes, List<string> list)
    {
        // what an earlier wpfmac wrote: rewritten copies on disk (now made in memory, at load)
        string old = Path.Combine(cacheRoot, Folder);
        if (Directory.Exists(old)) { try { Directory.Delete(old, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        var inScope = Scope(setting);
        string targetName = deps["runtimeTarget"]?["name"]?.GetValue<string>() ?? "";
        if (deps["targets"]?[targetName] is not JsonObject target || deps["libraries"] is not JsonObject libraries) return;
        string[] searchDirs = { appDir, overlayDir, System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory() };

        // the app's assemblies, and which declare COM types
        var appFiles = new List<string>();
        var candidates = new List<Candidate>();
        foreach (var (libKey, libNode) in target.ToList())
        {
            if (libKey == DepsMerger.OverlayLibrary || libKey.StartsWith("WpfMac.", StringComparison.Ordinal)) continue;      // the stack's own: never
            if (libNode is not JsonObject lib || lib["runtime"] is not JsonObject runtime) continue;
            foreach (var (asset, props) in runtime.ToList())
            {
                string file = Path.Combine(appDir, asset);
                if (!File.Exists(file)) continue;
                appFiles.Add(file);
                if (!ComRewriter.DeclaresComImport(file)) continue;
                string name = Path.GetFileNameWithoutExtension(file);
                if (!inScope(name)) { notes.Add($"comimport {name}: left (WPF_COMIMPORT_REWRITE)"); continue; }
                candidates.Add(new Candidate { File = file, Name = name, Asset = asset, Runtime = runtime, Props = props });
            }
        }
        if (candidates.Count == 0) return;

        // what of each candidate the other app assemblies reference by name
        var external = ExternalReferences(appFiles, candidates.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase));

        // judge each (nothing is written: the verdicts go to merge-report.txt)
        bool partial = !windows;
        foreach (var c in candidates)
        {
            var refs = external.TryGetValue(c.Name, out var r) ? r : new HashSet<string>();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var res = ComRewriter.Rewrite(c.File, null, searchDirs, partial, refs);
                c.Verdict = new Verdict { Unsupported = res.Unsupported.Count, UsesComTypesOf = res.UsesComTypesOf.ToList(), Summary = res.ToString() };
            }
            catch (Exception e) { c.Verdict = new Verdict { Unsupported = 1, Summary = $"the rewriter failed: {e.GetType().Name}: {e.Message}" }; }
            c.Verdict.Summary += $" [judged in {sw.ElapsedMilliseconds} ms]";
        }

        // Windows: rewritten together or not at all -- an assembly whose COM types another uses goes with it
        var chosen = candidates.Where(c => partial || c.Verdict.Unsupported == 0).ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        if (!partial)
        {
            for (bool changed = true; changed;)
            {
                changed = false;
                foreach (var c in chosen.Values.ToList())
                    if (c.Verdict.UsesComTypesOf.Any(u => !chosen.ContainsKey(u))) { chosen.Remove(c.Name); changed = true; }
            }
        }

        // The chosen leave the deps: the startup hook's resolver gets the first request for each, and
        // loads it rewritten in memory from the original (WpfMac.ComRewrite.ComImportLoader).
        foreach (var c in candidates)
        {
            bool rewrite = chosen.ContainsKey(c.Name);
            string why = c.Verdict.Summary + (!rewrite && c.Verdict.Unsupported == 0 && c.Verdict.UsesComTypesOf.Count > 0 ? " (left with them)" : "");
            if (rewrite)
            {
                c.Runtime.Remove(c.Asset);
                list.Add($"{c.Name}|{c.File}");
            }
            notes.Add($"comimport {c.Name}: {(rewrite ? "rewritten in memory at load" : "left on built-in COM")} -- {why}");
        }
        foreach (var (_, libNode) in target)
            if (libNode is JsonObject lib && lib["runtime"] is JsonObject rt && rt.Count == 0) lib.Remove("runtime");
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
                    // the outermost scope names the assembly
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
}
