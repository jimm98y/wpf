// The two steps of a bundled head's build that prepare an app's assemblies for the stack (wpf-rewrite runs them
// for WpfWebGpu.Sdk; the Packager runs them on its own, filling a template):
//
// - Placeholders: what the app's assemblies reference and nothing provides (an assembly a Windows install
//   leaves out because nothing the app does needs it) as a placeholder holding the types they name, so the
//   runtime can load the classes that name them; or, where an assembly carries it as a resource, that one.
// - Rewrite: their COM types and Windows P/Invokes onto the stack (BuildRewrite), and System.Drawing.Primitives'
//   SystemColors made its Windows build (SystemColorsPatch).

namespace WpfWebGpu.ComRewrite;

public static class RewriteSteps
{
    /// <summary>Writes into <paramref name="outDir"/> what <paramref name="inputs"/> reference and neither they
    /// nor <paramref name="published"/> provide; returns the files written.</summary>
    public static List<string> Placeholders(IReadOnlyList<string> inputs, IEnumerable<string> published, IReadOnlyList<string> search, string outDir, Action<string> log)
    {
        var written = new List<string>();
        var scans = inputs.Select(i => AssemblyScan.Read(i)).Where(x => x != null).Select(x => x!).ToList();
        var provided = new HashSet<string>(scans.Select(x => x.Name), StringComparer.OrdinalIgnoreCase);
        provided.UnionWith(published);
        // An app may carry an assembly inside another, as a resource it loads itself when the name is
        // asked for (an app's script engine embeds its JScript and resolves it from the resource): that
        // assembly is the app's, not missing, and ships as it is -- what the app would load anyway. What
        // it references is looked at in turn.
        var embedded = ComRewrite.Placeholders.EmbeddedAssemblies(scans.Select(x => x.File));
        List<string> missing;
        while (true)
        {
            missing = scans.SelectMany(x => x.References).Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(r => !provided.Contains(r)).OrderBy(r => r, StringComparer.OrdinalIgnoreCase).ToList();
            var found = ComRewrite.Placeholders.MatchEmbedded(missing, scans.Select(x => x.File), embedded, search);
            if (found.Count == 0) break;
            Directory.CreateDirectory(outDir);
            foreach (var (name, (bytes, source)) in found)
            {
                string path = Path.Combine(outDir, name + ".dll");
                File.WriteAllBytes(path, bytes);
                provided.Add(name);
                written.Add(path);
                if (AssemblyScan.Read(path) is { } scan) scans.Add(scan);
                log($"wpf-rewrite: {name}, referenced and not shipped, is the app's own from {source}: bundled from there");
            }
        }
        if (missing.Count > 0)
        {
            written.AddRange(ComRewrite.Placeholders.Write(missing, scans.Select(x => x.File), outDir));
            log($"wpf-rewrite: referenced and not shipped, placeholders of the types named in them: {string.Join(", ", missing)}");
        }
        return written;
    }

    /// <summary>Rewrites what of <paramref name="inputs"/> needs it into <paramref name="outDir"/>, under the same
    /// file names; and <paramref name="primitives"/>, given, made its Windows build there too.</summary>
    public static List<BuildRewrite.Rewritten> Rewrite(IReadOnlyList<string> inputs, IReadOnlyList<string> search, string outDir, string? primitives, Action<string> log)
    {
        var all = inputs.ToList();
        // System.Drawing.Primitives made its Windows build (SystemColorsPatch): its user32 GetSysColor is
        // then rewritten with the app's P/Invokes, onto the stack's user32
        if (primitives != null && SystemColorsPatch.Write(primitives, Path.Combine(outDir, ".primitives"), log) is { } patched)
            all.Add(patched);
        var done = BuildRewrite.Run(all, search, outDir, name => name.StartsWith("WpfWebGpu.", StringComparison.Ordinal), log);
        log($"wpf-rewrite: {all.Count} assemblies read, {done.Count} rewritten");
        return done;
    }
}
