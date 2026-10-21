// The rewrite at build time, for the heads that bundle the app and compile it ahead of time -- the
// browser, iOS, Android (Documentation/pinvoke-rewriter.md). The same rewrites the launcher's step and
// the loader make when an app is started (ComImportStep, ComImportLoader), over the app's assemblies
// before they are trimmed and compiled, chosen as off Windows:
//
// - an assembly that declares COM types is rewritten for them, with the assemblies that use its COM
//   types and can make RCWs of their own (judged as members of its group);
// - an assembly that P/Invokes a Windows DLL is rewritten for those P/Invokes.
//
// Location calls are not rewritten: a rewritten assembly here is a file of the bundle, where it is
// found as any other is. Nor is the entry assembly left out: nothing has loaded it before the rewrite.

namespace WpfWebGpu.ComRewrite;

public static class BuildRewrite
{
    /// <summary>One assembly rewritten: its original, the rewritten copy, and what was rewritten.</summary>
    public sealed record Rewritten(string Input, string Output, string Summary);

    /// <summary>
    /// Rewrites what of <paramref name="inputs"/> needs it into <paramref name="outDir"/> (same file names,
    /// with their .pdb when the original has one). <paramref name="searchDirs"/>: where references and the
    /// implementation assemblies (Win32Catalog.ImplementationAssemblies) are found. Assemblies of the stack
    /// itself (<paramref name="isStack"/>) are never rewritten.
    /// </summary>
    public static List<Rewritten> Run(IEnumerable<string> inputs, IEnumerable<string> searchDirs, string outDir, Func<string, bool> isStack, Action<string> log)
    {
        var dirs = searchDirs.ToList();
        var files = new List<AssemblyScan>();
        foreach (string input in inputs.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (AssemblyScan.Read(input) is not { } scan || isStack(scan.Name)) continue;
            if (files.Any(f => string.Equals(f.Name, scan.Name, StringComparison.OrdinalIgnoreCase))) continue;   // the same assembly twice: the first
            files.Add(scan);
        }

        Win32Catalog catalog = Win32Catalog.For(dirs);
        foreach (string problem in catalog.Problems) log($"pinvoke: {problem}");
        var pinvokes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in files)
        {
            if (!ComRewriter.ImportsWindows(f.File, catalog, windows: false)) continue;
            if (!f.ILOnly) { log($"pinvoke {f.Name}: mixed-mode (not IL-only): its Windows P/Invokes are not rewritten"); continue; }
            pinvokes.Add(f.Name);
        }

        // COM: what declares COM types, and those of its users that can make RCWs of their own (the
        // launcher's group members, ComImportStep), which go with it when they use its COM types
        var comNames = files.Where(f => f.DeclaresCom).Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var com = new HashSet<string>(comNames, StringComparer.OrdinalIgnoreCase);
        foreach (var f in files)
        {
            if (f.DeclaresCom || !f.References.Any(comNames.Contains)) continue;
            if (!(f.RcwApis || f.ComObjectApis || f.PInvokeDelegates || f.PInvokeScopes.Overlaps(comNames))) continue;
            try
            {
                var verdict = ComRewriter.Analyze(f.File, dirs, null, force: true);
                if (verdict.UsesComTypesOf.Count > 0) com.Add(f.Name);
            }
            catch (Exception e) { log($"comimport {f.Name}: could not be judged ({e.GetType().Name}: {e.Message}); left"); }
        }

        Directory.CreateDirectory(outDir);
        var done = new List<Rewritten>();
        foreach (var f in files)
        {
            bool c = com.Contains(f.Name), p = pinvokes.Contains(f.Name);
            if (!c && !p) continue;
            if (!f.ILOnly) { log($"comimport {f.Name}: mixed-mode (not IL-only): not rewritten"); continue; }
            string output = Path.Combine(outDir, Path.GetFileName(f.File));
            var (image, symbols, result) = ComRewriter.RewriteToMemory(f.File, dirs, com: c, pinvokes: p, windows: false, catalog: catalog);
            using (image)
            using (symbols)
            {
                Write(output, image);
                if (symbols != null) Write(Path.ChangeExtension(output, ".pdb"), symbols);
            }
            string summary = (c ? result.ToString() : "") + (p ? (c ? "; " : "") + result.Win32Summary : "");
            done.Add(new Rewritten(f.File, output, summary));
            log($"rewriter: {f.Name}: {summary}");
        }
        return done;
    }

    private static void Write(string path, MemoryStream s)
    {
        string temp = path + "." + Environment.ProcessId + ".tmp";
        using (var f = File.Create(temp)) s.WriteTo(f);
        File.Move(temp, path, overwrite: true);
    }
}
